using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Server.Game;

/// <summary>
/// 83410/5011 appearance lottery: DrawConfig, DrawPoolConfig and LC_DAL_AppearanceLottery.
/// Ticket-only settlement is one award transaction with the lottery counters and pet roster.
/// </summary>
public sealed class LotteryService
{
    public const uint DrawRequest = 83410, LotterySync = 5011;
    private readonly GameData data;
    private readonly CharacterService character;
    private readonly PetService pets;
    private readonly RoleStore roles;
    private readonly FunctionService functions;
    private readonly SeasonService seasons;
    private readonly ActivityClock activityClock;
    private readonly TimeProvider clock;
    private readonly Deadlines deadlines;
    private readonly GameLoop loop;
    private readonly HashSet<(GameSession Session, long Generation)> queued = [];

    public LotteryService(GameData data, CharacterService character, PetService pets, RoleStore roles,
        FunctionService functions, SeasonService seasons, ActivityClock activityClock, TimeProvider clock,
        SessionRegistry sessions, GameLoop loop, Deadlines deadlines, NewActivityService activities)
    {
        this.data = data; this.character = character; this.pets = pets; this.roles = roles;
        this.functions = functions; this.seasons = seasons; this.activityClock = activityClock; this.clock = clock;
        this.deadlines = deadlines; this.loop = loop;
        character.StateChanged.Add(QueueRefresh);
        activities.ProjectionChanged.Add(QueueRefresh);
    }

    private sealed class LotterySave
    {
        public byte[] Data { get; set; } = [];
        public long Day { get; set; }
    }

    private static long[] Flat(JsonElement row, string key) => row.TryGetProperty(key, out var values)
        && values.ValueKind == JsonValueKind.Array
        ? values.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray() : [];

    private bool Open(GameSession session, JsonElement draw)
    {
        long function = GameData.Int(draw, "_iFunctionID");
        if (function > 0 && !functions.IsOpen(session, function)) return false;
        long activityId = GameData.Int(draw, "_iNewAct");
        if (activityId == 0) return true;
        if (session.NewActivities?.ActivityInfo.ContainsKey(activityId) != true
            || data.Row("NewActivityConfig", activityId) is not { } activity) return false;
        if (activity.TryGetProperty("_kMultiverseArray", out var books)
            && books.ValueKind == JsonValueKind.Array
            && !books.EnumerateArray().Any(book => GameData.Number(book) is 0
                || GameData.Number(book) == (session.SeasonState?.PlayBookId ?? seasons.PlayBook))) return false;
        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        return functions.ConditionMet(session, GameData.Int(activity, "_kOpenCondition"))
            && activityClock.ResolveActivity(activity, now, session.Role!.CreateTime, seasons.PlayBook).Contains(now);
    }

    private DBLotteryData Project(GameSession session, DBLotteryData source, bool resetDay)
    {
        var next = source.Clone();
        if (resetDay)
        {
            foreach (var (poolId, state) in next.LotteryPool)
            {
                if (data.Row("DrawConfig", poolId) is { } draw && Flat(draw, "_kMaxCnt") is [1, ..])
                    state.DailyCount = 0;
            }
            next.DailyRecord.Clear();
        }
        foreach (var row in data.Table("DrawConfig").Values)
        {
            int id = checked((int)GameData.Int(row, "_iID"));
            if (id <= 0) continue;
            if (!next.LotteryPool.TryGetValue(id, out var pool)) next.LotteryPool[id] = pool = new LotteryPoolData { PoolId = id };
            pool.PoolId = id;
            pool.IsOpen = Open(session, row);
            pool.MaxDrawTypeOpen = Flat(row, "_kMaxRoll") is [> 0, > 0];
        }
        return next;
    }

    internal static byte[] Pack(DBLotteryData data, long day) =>
        JsonSerializer.SerializeToUtf8Bytes(new LotterySave { Data = data.ToByteArray(), Day = day });

    public async Task LoginAsync(GameSession session)
    {
        byte[]? saved = roles.LoadLottery(session.Role!.Id);
        var envelope = saved == null ? null : JsonSerializer.Deserialize<LotterySave>(saved);
        var loaded = envelope?.Data.Length > 0 ? DBLotteryData.Parser.ParseFrom(envelope.Data) : new DBLotteryData();
        long day = activityClock.Day(clock.GetUtcNow().ToUnixTimeSeconds());
        var projected = Project(session, loaded, envelope?.Day != day);
        var packed = Pack(projected, day);
        if (!roles.BlobUnchanged(session.Role.Id, "lottery", packed))
        {
            var initial = roles.NewTransaction();
            initial.RoleBlob(session.Role.Id, "lottery", packed);
            await roles.CommitAsync(initial, "lottery/login-reconcile");
        }
        session.Lottery = projected;
        session.LotteryDay = day;
        await CharacterService.SyncAsync(session, LotterySync, session.Lottery.Clone());
        Schedule(session);
    }

    /// <summary>Recompute banner availability on task/function, season and clock changes before the next draw.</summary>
    public Task ReconcileAsync(GameSession session) => loop.IsOnLoop
        ? ReconcileAsync(session, deadlines.Capture(session))
        : loop.InvokeAsync(() => ReconcileAsync(session, deadlines.Capture(session)));

    private async Task ReconcileAsync(GameSession session, DeadlineLease attachment)
    {
        if (!session.ActivitiesReady || session.Lottery == null || session.Role == null) return;
        bool succeeded = false;
        var role = session.Role;
        if (role == null) return;
        var roleAttachment = session.Attachment;
        var owners = loop.Owners ?? throw new InvalidOperationException("Lottery needs an owner registry.");
        using var scope = await owners.EnterWithGateAsync("lottery/reconcile", session.ActivityGate,
            [ReservationKey.Role(role.Id)]);
        try
        {
            if (!session.ActivitiesReady || session.Lottery == null || session.Role == null
                || !ReferenceEquals(session.Attachment, roleAttachment) || !deadlines.IsCurrent(session, attachment)) return;
            long day = activityClock.Day(clock.GetUtcNow().ToUnixTimeSeconds());
            var projected = Project(session, session.Lottery, session.LotteryDay != day);
            if (session.LotteryDay == day && session.Lottery.Equals(projected)) { succeeded = true; return; }
            var builder = roles.NewTransaction();
            builder.RoleBlob(role.Id, "lottery", Pack(projected, day));
            await roles.CommitAsync(builder, "lottery/reconcile");
            session.Lottery = projected;
            session.LotteryDay = day;
            await CharacterService.SyncAsync(session, LotterySync, projected.Clone());
            succeeded = true;
        }
        finally
        {
            session.ActivityGate.Release();
            if (deadlines.IsCurrent(session, attachment))
            {
                if (succeeded) Schedule(session);
                else deadlines.Set(session, nameof(LotteryService), clock.GetUtcNow().AddSeconds(1), OnDeadlineAsync);
            }
        }
    }

    private void QueueRefresh(GameSession session)
    {
        if (!session.ActivitiesReady || session.Lottery == null) return;
        if (!loop.IsOnLoop)
        {
            long? role = session.Role?.Id;
            int serial = session.AccountEntitySerial;
            loop.PostOwned(session, () =>
            {
                if (session.Role?.Id == role && session.AccountEntitySerial == serial) QueueRefresh(session);
                return Task.CompletedTask;
            });
            return;
        }
        var attachment = deadlines.Capture(session);
        if (attachment.AttachmentGeneration == 0) return;
        var key = (session, attachment.AttachmentGeneration);
        if (!queued.Add(key)) return;
        loop.PostOwned(session, async () =>
        {
            try { if (deadlines.IsCurrent(session, attachment)) await ReconcileAsync(session); }
            finally { queued.Remove(key); }
        }, "lottery projection", discarded: () => queued.Remove(key));
    }

    /// <summary>Daily counters change at civil midnight; banners at their configured window boundaries.</summary>
    private void Schedule(GameSession session)
    {
        if (session.Role == null || session.Lottery == null) return;
        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        var tomorrow = clock.GetLocalNow().Date.AddDays(1);
        long next = new DateTimeOffset(tomorrow, clock.LocalTimeZone.GetUtcOffset(tomorrow)).ToUnixTimeSeconds();
        foreach (var draw in data.Table("DrawConfig").Values)
        {
            if (data.Row("NewActivityConfig", GameData.Int(draw, "_iNewAct")) is not { } activity) continue;
            var window = activityClock.ResolveActivity(activity, now, session.Role.CreateTime, seasons.PlayBook);
            if (window.Start > now) next = Math.Min(next, window.Start);
            if (window.End > now) next = Math.Min(next, window.End);
        }
        deadlines.Set(session, nameof(LotteryService), DateTimeOffset.FromUnixTimeSeconds(next), OnDeadlineAsync);
    }

    private Task OnDeadlineAsync(GameSession session, DateTimeOffset due, DeadlineLease lease, CancellationToken cancellation)
        => ReconcileAsync(session, lease);

    private sealed record Outcome(int Kind, int Id, int Count, int Group);

    private Outcome? Roll(IReadOnlySet<long> groups)
    {
        var rows = data.Table("DrawPoolConfig").Values.Where(row => groups.Contains(GameData.Int(row, "_iGroupID"))
            && GameData.Int(row, "_iWeight") > 0 && Candidates(row).Count > 0).ToArray();
        long total = rows.Sum(row => GameData.Int(row, "_iWeight"));
        if (total <= 0) return null;
        long roll = Random.Shared.NextInt64(total);
        foreach (var row in rows)
        {
            roll -= GameData.Int(row, "_iWeight");
            if (roll >= 0) continue;
            var choices = Candidates(row);
            var selected = choices[Random.Shared.Next(choices.Count)];
            return selected with { Group = checked((int)GameData.Int(row, "_iGroupID")) };
        }
        return null;
    }

    private List<Outcome> Candidates(JsonElement row)
    {
        var results = new List<Outcome>();
        if (!row.TryGetProperty("_kItem", out var value) || value.ValueKind != JsonValueKind.Array) return results;
        foreach (var tuple in value.EnumerateArray())
        {
            if (tuple.ValueKind != JsonValueKind.Array || tuple.GetArrayLength() != 3) continue;
            int kind = checked((int)(GameData.Number(tuple[0]) ?? 0));
            int id = checked((int)(GameData.Number(tuple[1]) ?? 0));
            int count = checked((int)(GameData.Number(tuple[2]) ?? 0));
            if (count > 0 && (kind == 1 && count == 1 && data.Row("PetBaseConfig", id) != null
                    || kind == 2 && data.Row("ItemConfig", id) != null)) results.Add(new(kind, id, count, 0));
        }
        return results;
    }

    /// <summary>LC_LotteryServer.SendLotteryReq and LC_DAL_AppearanceLottery response parser.</summary>
    public async Task DrawAsync(GameSession session, LotteryReq request)
    {
        var role = session.Role;
        if (role == null)
        {
            await session.SendGameAsync(DrawRequest, new LotteryResp
            {
                Result = ErrorCodes.FeatureUnavailable,
                Type = request.Type, PoolData = new LotteryPoolData { PoolId = request.PoolId }
            });
            return;
        }
        var attachment = session.Attachment;
        var owners = loop.Owners ?? throw new InvalidOperationException("Lottery needs an owner registry.");
        using var scope = await owners.EnterWithGateAsync("lottery/draw", session.ActivityGate,
            [ReservationKey.Role(role.Id), ReservationKey.Entity("lottery", role.Id.ToString())]);
        try
        {
            if (!session.ActivitiesReady || !ReferenceEquals(session.Attachment, attachment)
                || session.Role?.Id != role.Id || session.Lottery == null || session.Pets == null)
            {
                await session.SendGameAsync(DrawRequest, new LotteryResp
                {
                    Result = ErrorCodes.FeatureUnavailable, Type = request.Type,
                    PoolData = new LotteryPoolData { PoolId = request.PoolId },
                });
                return;
            }
            var row = data.Row("DrawConfig", request.PoolId);
            var source = session.Lottery ?? new DBLotteryData();
            long day = activityClock.Day(clock.GetUtcNow().ToUnixTimeSeconds());
            var next = Project(session, source, session.LotteryDay != day);
            var response = new LotteryResp
            {
                Type = request.Type,
                PoolData = next.LotteryPool.GetValueOrDefault(request.PoolId)?.Clone() ?? new LotteryPoolData { PoolId = request.PoolId }
            };
            int code = 0;
            if (row == null || request.PoolId <= 0) code = ErrorCodes.Parameter;
            else if (!response.PoolData.IsOpen) code = ErrorCodes.ActivityClosed;
            string field = request.Type switch
            {
                ELotteryPoolType.GachaCountMin => "_kMinRoll",
                ELotteryPoolType.GachaCountMid => "_kMidRoll",
                ELotteryPoolType.GachaCountMax => "_kMaxRoll",
                _ => "",
            };
            long[] mode = row is { } valid && field.Length > 0 ? Flat(valid, field) : [];
            if (code == 0 && (mode.Length != 2 || mode[0] <= 0 || mode[1] <= 0 || mode[1] > 50)) code = ErrorCodes.Parameter;
            int count = code == 0 ? (int)mode[1] : 0;
            int ticket = code == 0 ? checked((int)mode[0]) : 0;
            long[] limit = row is { } configured ? Flat(configured, "_kMaxCnt") : [];
            if (code == 0 && (limit.Length != 2 || limit[0] is not (1 or 2) || limit[1] <= 0
                || (long)response.PoolData.DailyCount + count > limit[1])) code = ErrorCodes.AttemptsExhausted;
            if (code == 0 && character.CountOf(session, ticket) < count) code = ErrorCodes.ItemsShort;
            if (code == 0 && session.Pets == null) code = ErrorCodes.FeatureUnavailable;
            if (code == 0)
            {
                var petState = session.Pets!.Clone();
                var goods = new List<AwardGrant>();
                var groups = Flat(row!.Value, "_kSubBanner").ToHashSet();
                var guaranteed = Flat(row.Value, "_kGuaranteedBanner").ToHashSet();
                int pityKey = checked((int)GameData.Int(row.Value, "_iType"));
                if (!next.PityRecords.TryGetValue(pityKey, out var pity)) next.PityRecords[pityKey] = pity = new LotteryPityRecord();
                int guarantee = checked((int)GameData.Int(row.Value, "_iGuaranteedCnt"));
                for (int index = 0; index < count; index++)
                {
                    bool forced = guarantee > 0 && pity.PityCount + 1 >= guarantee;
                    var outcome = Roll(forced ? guaranteed : groups);
                    if (outcome == null) { code = ErrorCodes.ItemNoConfig; break; }
                    bool hit = guaranteed.Contains(outcome.Group);
                    pity.TotalCount++;
                    pity.PityCount = hit ? 0 : pity.PityCount + 1;
                    pity.MustCount = guarantee;
                    if (forced) pity.HitPityCount++;
                    var result = new LotteryItemData
                    {
                        ItemId = outcome.Id, ItemCount = outcome.Count,
                        IsPet = outcome.Kind == 1, PoolId = request.PoolId
                    };
                    if (outcome.Kind == 1)
                    {
                        if (!pets.TryStageLotteryPet(petState, outcome.Id, out var broken)) { code = ErrorCodes.PetNoConfig; break; }
                        if (broken is { } material)
                        {
                            result.IsBroken = true;
                            goods.Add(new AwardGrant(material.Entry, material.Count));
                        }
                    }
                    else goods.Add(new AwardGrant(outcome.Id, outcome.Count));
                    response.ItemList.Add(result);
                }
                if (code == 0)
                {
                    long[] bonus = Flat(row.Value, "_kRollNum");
                    if (bonus is [> 0, > 0] && data.Row("ItemConfig", bonus[0]) != null)
                        goods.Add(new AwardGrant(checked((int)bonus[0]), checked((int)(bonus[1] * count))));
                    next.LotteryPool[request.PoolId].DailyCount += count;
                    next.DailyRecord[request.PoolId] = new LotteryModuleDailyCount { Count = next.LotteryPool[request.PoolId].DailyCount };
                    pets.FinalizeLotteryPets(petState);
                    byte[] saved = Pack(next, day), petBytes = petState.ToByteArray();
                    // A draw with a pet result is the E05b pet family envelope (pet legs and refunds); any other draw is the
                    // 13f event envelope. One builder carries one envelope.
                    bool petDraw = response.ItemList.Any(result => result.IsPet);
                    var familyEconomy = petDraw
                        ? pets.LotteryEconomy(session, request.PoolId, ticket, count, petState, saved,
                            goods.Select(grant => (grant.Entry, grant.Count)).ToArray(), response.ItemList)
                        : null;
                    // The client sends no request ID. The source is the server-admitted draw sequence;
                    // a later request starts a new sequence only after this commit publishes pity state.
                    string sourceKey = $"lottery/draw/{role.Id}/{pityKey}/{pity.TotalCount}";
                    var resolved = response.ItemList.Select(item => new
                    {
                        item.ItemId, item.ItemCount, item.IsPet, item.IsBroken, item.PoolId
                    }).ToArray();
                    var economy = petDraw ? null : EventEconomy.ForRole(data, session, "lottery-draw", sourceKey,
                        [(ticket, (long)count)], goods,
                        new
                        {
                            request.PoolId, Type = (int)request.Type, Day = day, Results = resolved,
                            Lottery = Convert.ToBase64String(saved), Pets = Convert.ToBase64String(petBytes)
                        });
                    if (!await character.ApplyAwardAsync(session, new AwardPlan(0, 0, goods),
                            costs: [(ticket, count)], persist: transaction =>
                            {
                                transaction.RoleBlob(session.Role!.Id, "lottery", saved);
                                transaction.RoleBlob(session.Role.Id, "pets", petBytes);
                                if (economy != null) transaction.WithEventEconomy(economy);
                            }, stateLockHeld: true, committed: () =>
                            {
                                session.Lottery = next;
                                session.LotteryDay = day;
                                session.Pets = petState;
                            }, familyEconomy: familyEconomy)) code = ErrorCodes.ItemsShort;
                    else
                    {
                        await CharacterService.SyncAsync(session, LotterySync, next.Clone());
                        await pets.SyncLotteryPetsAsync(session);
                        response.PoolData = next.LotteryPool[request.PoolId].Clone();
                        foreach (var (key, value) in next.PityRecords) response.PityRecords[key] = value.Clone();
                    }
                }
            }
            if (code != 0) response.ItemList.Clear();
            response.Result = code;
            await session.SendGameAsync(DrawRequest, response);
        }
        finally { session.ActivityGate.Release(); }
    }
}
