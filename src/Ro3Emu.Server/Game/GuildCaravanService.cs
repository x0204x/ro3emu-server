using System.Collections.Concurrent;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Guild Caravan (LC_GuildCatShopServer, LC_DAL_GuildCatShop, GuildTransportConfig and GuildSettingConfig).
/// A role owns its order and five daily rounds; membership only authorizes help and receives configured guild funds.
/// (inferred) Stable weighted order draw, 05:00 dayVersion, reward mail at rollover, and cross-guild help cleanup.
/// </summary>
public sealed class GuildCaravanService(GameData data, GuildStore guilds, AccountStore accounts, RoleStore roles,
    CharacterService character, AwardService awards, MailService mail, GuildService guildService,
    TaskService tasks, SeasonService seasons, FunctionService functions, SessionRegistry sessions, TimeProvider clock, GameLoop loop,
    ILogger<GuildCaravanService> log) : IGameTicker
{
    public const uint Show = 32247, Submit = 32249, WantHelp = 32250, HelpOther = 32251,
        HelpList = 32252, Depart = 32255, Refresh = 32288, HelpNotify = 32289;

    private readonly GameData _data = data;
    private readonly GuildStore _guilds = guilds;
    private readonly AccountStore _accounts = accounts;
    private readonly RoleStore _roles = roles;
    private readonly CharacterService _character = character;
    private readonly AwardService _awards = awards;
    private readonly MailService _mail = mail;
    private readonly GuildService _guildService = guildService;
    private readonly TaskService _tasks = tasks;
    private readonly SeasonService _seasons = seasons;
    private readonly FunctionService _functions = functions;
    private readonly SessionRegistry _sessions = sessions;
    private readonly TimeProvider _clock = clock;
    private readonly GameLoop _loop = loop;
    private readonly ILogger<GuildCaravanService> _log = log;
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _gates = new();
    private TimeSpan _lastTick;
    // Deterministic pre-admission suspension; never installed by production callers.
    internal Func<string, Task>? BeforeCharacterAdmissionForTest { get; set; }

    private sealed class State
    {
        public long Revision { get; set; }
        public long GuildId { get; set; }
        public long Day { get; set; }
        public long CurRound { get; set; }
        public long RefreshTimes { get; set; }
        public long HelpOtherCount { get; set; }
        public long ReqHelpOtherCount { get; set; }
        public bool IsAwardGot { get; set; }
        public long LastRefreshTime { get; set; }
        public List<Sku> Skus { get; set; } = new();
        public List<PendingAward> PendingAwards { get; set; } = new();
    }

    private sealed class PendingAward
    {
        // Natural source survives a helper disconnect, guild switch and recipient login.
        public string SourceKey { get; set; } = "";
        public long GuildId { get; set; }
        public bool GuildBooked { get; set; }
        public List<PendingGrant> Grants { get; set; } = new();
    }

    private sealed class PendingGrant
    {
        public int Entry { get; set; }
        public int Count { get; set; }
        public int BindType { get; set; }
        public long ExpireTimeStamp { get; set; }
    }

    private sealed class Sku
    {
        public long Index { get; set; }
        public long CfgId { get; set; }
        public int Status { get; set; }
        public string HelperName { get; set; } = "";
        public long HelperId { get; set; }
        public long ReqHelpTime { get; set; }
    }

    private SemaphoreSlim Gate(long guildId) => _gates.GetOrAdd(guildId, _ => new SemaphoreSlim(1, 1));

    private ReservationKey[] Keys(long guildId, params long[] additionalRoles)
    {
        // Historical/unbooked pending awards can credit their original guild after a
        // membership switch. Reserve each such treasury and its projection recipients
        // before admitting the recipient role; recovery rows keep the same guild key.
        var orders = additionalRoles.Where(id => id > 0).Select(Read).OfType<State>().ToArray();
        var guildIds = orders.Select(order => order.GuildId).Where(id => id > 0)
            .Concat(orders.SelectMany(order => order.PendingAwards
                .Where(p => !p.GuildBooked && p.GuildId > 0).Select(p => p.GuildId)))
            .Append(guildId).Distinct().ToArray();
        var ids = guildIds.SelectMany(id => _guilds.Get(id)?.Members.Keys ?? Enumerable.Empty<long>())
            .Concat(additionalRoles).Where(id => id > 0).Distinct().ToArray();
        return [.. guildIds.SelectMany(id =>
            {
                string guild = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return new[] { ReservationKey.Entity("guild", guild),
                    ReservationKey.Entity("guild_caravan_version", guild) };
            }),
            .. ids.SelectMany(id => new[] { ReservationKey.Role(id),
                ReservationKey.Entity("guild_caravan_member", id.ToString(System.Globalization.CultureInfo.InvariantCulture)) })];
    }

    /// <summary>C/D 15: the caravan login's gate keys (the role's guild or its pending order's guild, every member,
    /// the caravan rows), reserved up front by the role select.</summary>
    internal ReservationKey[] LoginKeys(long roleId)
    {
        long gateId = _guilds.ForRole(roleId)?.Id ?? Read(roleId)?.GuildId ?? 0;
        return gateId == 0 ? [] : Keys(gateId, roleId);
    }

    private static Task? Detaching(OwnerRegistry owners, IEnumerable<ReservationKey> keys) =>
        keys.Where(key => key.Kind == ReservationKind.RolePart)
            .Select(key => long.Parse(key.Id, System.Globalization.CultureInfo.InvariantCulture))
            .Select(id => owners.Cached(id)?.Detaching).FirstOrDefault(task => task != null);

    private sealed class GuildGateScope(SemaphoreSlim gate, LegacyOwnerScope owner,
        SemaphoreSlim? characterGate = null) : IDisposable
    {
        public OwnerContext Context => owner.Context;
        public bool CharacterLocked => characterGate != null;
        public void Dispose() { characterGate?.Release(); gate.Release(); owner.Dispose(); }
    }

    // The member set can change while an intent waits. Admit the guild gate and all role/entity
    // reservations together, then restage before reading or changing any order.
    private Task<GuildGateScope> EnterWithGuildGateAsync(string origin, long guildId,
        params long[] additionalRoles) => EnterWithGuildGateAsync(origin, guildId, additionalRoles, null);

    private Task<GuildGateScope> EnterWithCharacterGateAsync(string origin, long guildId,
        GameSession characterSession, params long[] additionalRoles) =>
        EnterWithGuildGateAsync(origin, guildId, additionalRoles, characterSession);

    private async Task<GuildGateScope> EnterWithGuildGateAsync(string origin, long guildId,
        long[] additionalRoles, GameSession? characterSession = null)
    {
        var owners = _loop.Owners ?? throw new InvalidOperationException("Caravan owner registry is unavailable.");
        var gate = Gate(guildId);
        bool inherited = owners.Current is { RuntimeOnly: false };
        var initialRole = characterSession?.Role;
        long? initialRoleId = initialRole?.Id;
        var initialAttachment = characterSession?.Attachment;
        if (characterSession != null && BeforeCharacterAdmissionForTest is { } beforeAdmission)
            await beforeAdmission(origin);
        while (true)
        {
            if (characterSession != null && (initialRole == null || initialAttachment == null
                || !ReferenceEquals(characterSession.Role, initialRole)
                || characterSession.Role?.Id != initialRoleId
                || !ReferenceEquals(characterSession.Attachment, initialAttachment)
                || initialAttachment.Cancelled))
                throw new OperationCanceledException("Caravan actor changed before character admission.");
            var keys = Keys(guildId, additionalRoles);
            // Offline recipients may still be quiescing after their socket closed. Await the
            // handover outside every guild gate and reservation, then stage the member set again.
            if (!inherited && Detaching(owners, keys) is { } handover)
            { await handover; continue; }
            if (characterSession == null)
            {
                var scope = await owners.EnterWithGateAsync(origin, gate, keys);
                var pendingHandover = !inherited ? Detaching(owners, keys) : null;
                if (pendingHandover == null && keys.ToHashSet().SetEquals(Keys(guildId, additionalRoles)))
                {
                    return new GuildGateScope(gate, scope);
                }
                gate.Release();
                scope.Dispose();
                if (pendingHandover != null) await pendingHandover;
                continue;
            }
            // The paid paths need both the old guild gate and Character.StateLock. Wait for
            // both without an owner reservation, then admit every role/member/guild key together.
            // A guild leave can therefore finish while a payment is parked at StateLock.
            await gate.WaitAsync();
            bool characterLocked = false;
            bool handedOff = false;
            LegacyOwnerScope? admitted = null;
            Task? changed = null;
            try
            {
                await characterSession.StateLock.WaitAsync();
                characterLocked = true;
                if (!ReferenceEquals(characterSession.Role, initialRole)
                    || characterSession.Role?.Id != initialRoleId
                    || !ReferenceEquals(characterSession.Attachment, initialAttachment)
                    || initialAttachment?.Cancelled == true)
                    throw new OperationCanceledException("Caravan actor changed while waiting for the character gate.");
                changed = !inherited ? Detaching(owners, keys) : null;
                if (changed == null) admitted = owners.TryEnter(origin, keys);
                if (changed == null && admitted == null) changed = owners.Reservations.Changed;
                else if (changed == null && admitted != null
                    && keys.ToHashSet().SetEquals(Keys(guildId, additionalRoles)))
                {
                    if (characterSession.Owner is { } owner) admitted.Context.Pin(owner);
                    handedOff = true;
                    return new GuildGateScope(gate, admitted, characterSession.StateLock);
                }
            }
            finally
            {
                if (!handedOff)
                {
                    admitted?.Dispose();
                    if (characterLocked) characterSession.StateLock.Release();
                    gate.Release();
                }
            }
            if (changed != null) await changed;
        }
    }
    private long Day => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime.AddHours(-5)).DayNumber;
    private long NowMs => _clock.GetUtcNow().ToUnixTimeMilliseconds();
    private GuildStore.State? Guild(GameSession s) => s.Role is { } role && s.Guild?.GuildId is > 0
        && _guilds.ForRole(role.Id) is { } guild && guild.Id == s.Guild.GuildId
        && guild.Status == (int)GuildStatus.Normal && guild.Members.ContainsKey(role.Id) ? guild : null;
    private bool Eligible(GameSession s) => s.Progress is { } progress
        && _data.Row("DailyActivityConfig", 101) is { } row
        && progress.Level >= GameData.Int(row, "_iiLv")
        && _functions.IsOpen(s, 1320);

    private State? Read(long roleId) => _accounts.ReadSync<State?>(new CaravanMemberReadPlan<State>(roleId));

    private EconomyEnvelope MetadataEnvelope(long roleId, string kind)
    {
        var role = _roles.Get(roleId) ?? throw new InvalidOperationException("Caravan owner role is missing.");
        string operation = Guid.NewGuid().ToString("N");
        string source = $"guild-caravan:{kind}:{roleId}:{operation}";
        return new EconomyEnvelope(operation, role.Account, roleId, kind, source, source, NowMs, [], []);
    }

    private async Task SaveAsync(long roleId, State state)
    {
        var transaction = _accounts.NewTransaction();
        EnlistMemberSave(transaction, roleId, state.GuildId, state, false);
        transaction.WithEconomy(MetadataEnvelope(roleId, "member-save"));
        await transaction.CommitAsync(_accounts, "guild-caravan/member-save");
    }

    private long Version(long guildId, long day)
    {
        // (inferred) The protocol has one dayVersion for a guild help list, while skuIndex must stay 1..8.
        // A guild-wide monotonic revision rejects actions against refreshed or replaced slots.
        long revision = _accounts.ReadSync<long>(new CaravanVersionReadPlan(guildId, day));
        return checked(day * 1_000_000 + revision);
    }

    private async Task SaveBumpAsync(long roleId, State state)
    {
        var transaction = _accounts.NewTransaction();
        EnlistMemberSave(transaction, roleId, state.GuildId, state, false);
        transaction.Add(new CaravanVersionPlan(state.GuildId, state.Day));
        transaction.WithEconomy(MetadataEnvelope(roleId, "member-version"));
        await transaction.CommitAsync(_accounts, "guild-caravan/member-version");
    }

    private long Setting(string key, long fallback)
    {
        if (!_data.Table("GuildSettingConfig").TryGetValue(key, out var row)
            || !row.TryGetProperty("_kValue", out var value)) return fallback;
        return value.ValueKind == JsonValueKind.Number ? (long)(GameData.Number(value) ?? fallback) : fallback;
    }

    private long[] Mix(int round)
    {
        if (_data.Table("GuildSettingConfig").TryGetValue("GuildTransportRoundSAB", out var row)
            && row.TryGetProperty("_kValue", out var array) && array.ValueKind == JsonValueKind.Array
            && round >= 0 && round < array.GetArrayLength() && array[round].ValueKind == JsonValueKind.Array)
            return array[round].EnumerateArray().Select(x => (long)(GameData.Number(x) ?? 0)).ToArray();
        return [];
    }

    private List<Sku> Generate(GameSession s, long day, long round, long salt = 0)
    {
        var result = new List<Sku>();
        long[] mix = Mix((int)round);
        // (inferred) A stable weighted draw with replacement fills late rounds even when realm level 1 has
        // only one type-1 row; the authored S/A/B mix still fixes the number of slots in each category.
        var random = new Random(unchecked((int)(s.Role!.Id * 7919L + day * 104729L + round * 15485863L + salt * 32452843L)));
        int openDay = (int)Math.Max(0, (_clock.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(_seasons.StartTime)).TotalDays);
        for (int type = 1; type <= mix.Length; type++)
        {
            var pool = _data.Table("GuildTransportConfig").Values.Where(row =>
                GameData.Int(row, "_iType") == type && GameData.Int(row, "_iDayMin") <= openDay
                && row.TryGetProperty("_kServerLv", out var levels) && levels.ValueKind == JsonValueKind.Array
                // (inferred) Realm level starts at one; personal level is not the server-level config axis.
                && levels.GetArrayLength() >= 2 && 1 >= (GameData.Number(levels[0]) ?? 1)
                && 1 <= (GameData.Number(levels[1]) ?? 99)
                && row.TryGetProperty("_kMultiverseArray", out var books) && books.ValueKind == JsonValueKind.Array
                && books.EnumerateArray().Any(x => GameData.Number(x) is 0 || GameData.Number(x) == _seasons.PlayBook)
                && GameData.Int(row, "_iBasicWeight") > 0 && _data.Row("ItemConfig", GameData.Int(row, "_iItem")) != null)
                .ToList();
            for (int i = 0; i < mix[type - 1] && pool.Count > 0; i++)
            {
                long total = pool.Sum(row => GameData.Int(row, "_iBasicWeight"));
                long pick = random.NextInt64(total);
                int selected = 0;
                for (; selected < pool.Count - 1; selected++)
                    if ((pick -= GameData.Int(pool[selected], "_iBasicWeight")) < 0) break;
                result.Add(new Sku { Index = result.Count + 1, CfgId = GameData.Int(pool[selected], "_iID") });
            }
        }
        return result;
    }

    private long SlotAward(Sku sku, long round)
    {
        var row = _data.Row("GuildTransportConfig", sku.CfgId);
        return row is { } r && r.TryGetProperty("_kAward", out var award) && award.ValueKind == JsonValueKind.Array
            && round >= 0 && round < award.GetArrayLength() ? (long)(GameData.Number(award[(int)round]) ?? 0) : 0;
    }

    private long DepartureAward(State state)
    {
        int count = state.Skus.Count(s => s.Status == 1);
        var threshold = _data.Table("GuildTransportTaskConfig").Values
            .Where(r => GameData.Int(r, "_iNum") <= count).OrderByDescending(r => GameData.Int(r, "_iNum")).FirstOrDefault();
        return threshold.ValueKind == JsonValueKind.Object && threshold.TryGetProperty("_kAward", out var groups)
            && groups.ValueKind == JsonValueKind.Array && state.CurRound < groups.GetArrayLength()
            ? (long)(GameData.Number(groups[(int)state.CurRound]) ?? 0) : 0;
    }

    private async Task<State> EnsureAsync(GameSession s, GuildStore.State guild)
    {
        if (Guild(s)?.Id != guild.Id || !Eligible(s)) throw new CaravanMembershipChangedException();
        long roleId = s.Role!.Id;
        var state = Read(roleId) ?? new State { Day = Day, GuildId = guild.Id };
        state.PendingAwards ??= new(); state.Skus ??= new();
        if (state.Day != Day)
            state = await RolloverAsync(roleId, state, s.Progress!);
        if (Guild(s)?.Id != guild.Id || !Eligible(s) || state.Day != Day)
            throw new CaravanMembershipChangedException();
        if (state.GuildId != guild.Id)
        {
            // (inferred) A guild switch keeps paid/done slots and daily limits; only old-guild help requests expire.
            state.GuildId = guild.Id;
            foreach (var sku in state.Skus.Where(x => x.Status == 2))
            { sku.Status = 0; sku.ReqHelpTime = 0; }
            await SaveAsync(roleId, state);
        }
        if (state.CurRound < Setting("GuildTransportRoundNum", 5) && state.Skus.Count == 0)
        {
            state.Skus = Generate(s, state.Day, state.CurRound);
            await SaveAsync(roleId, state);
        }
        return state;
    }

    private async Task<State> RolloverAsync(long roleId, State state, RoleProgress progress)
    {
        if (state.Day == Day) return state;
        // (inferred) Yesterday's compensation settles the order without a 104011 departure event;
        // today's ToDo progress is earned only by a new button departure.
        var next = new State
        {
            Day = Day, GuildId = state.GuildId, Revision = state.Revision,
            PendingAwards = state.PendingAwards.ToList()
        };
        long owed = !state.IsAwardGot && state.Skus.Count(x => x.Status == 1) >= Setting("GuildTransportRequestItemTypeNum", 5)
            ? DepartureAward(state) : 0;
        if (owed > 0)
        {
            // (inferred) MailConfig 1023 compensates a loaded caravan left unclaimed at daily 05:00.
            var plan = _awards.ResolveForProgress(progress, owed);
            var attachments = plan.Grants.Where(g => g.Entry != 104)
                .Select(g => new MailService.Attachment(g.Entry, g.Count, g.BindType)).ToArray();
            long funds = plan.Grants.Where(g => g.Entry == 104).Sum(g => (long)g.Count);
            long contribution = plan.Grants.Where(g => g.Entry == 102).Sum(g => (long)g.Count);
            string source = $"guild-caravan:{roleId}:{state.Day}:{state.CurRound}";
            var effects = new List<EconomyEffect>();
            foreach (var grant in plan.Grants.Where(g => g.Entry is not (100 or 101 or 104)))
                effects.Add(new(Asset(grant.Entry), RoleOwner(roleId), EconomyCompartment.Mail,
                    grant.Count, EconomyLegKind.Source, EconomyReason.ActivityReward));
            if (funds > 0) effects.Add(new(new(EconomyAssetKind.GuildResource, EconomyDenominations.GuildMoney),
                GuildOwner(state.GuildId), GuildMoneyCompartment(state.GuildId), funds,
                EconomyLegKind.Source, EconomyReason.GuildResourceGrant));
            // Entry 102 is personal currency. Member contribution is progression metadata.
            var actor = _roles.Get(roleId) ?? throw new InvalidOperationException("Caravan rollover recipient is missing.");
            string operation = Guid.NewGuid().ToString("N");
            await _mail.SendTemplateOnceAsync(roleId, 1023, attachments,
                source, tx =>
                {
                    EnlistMemberSave(tx, roleId, state.GuildId, next, false);
                    if (funds > 0 || contribution > 0)
                        _guilds.EnlistCaravanReward(state.GuildId, roleId, funds, contribution, tx);
                }, _loop.Owners?.Current, parentEconomy: expired => new EconomyEnvelope(operation,
                    actor.Account, roleId, "guild-caravan-rollover", source, source, NowMs,
                    effects.Concat(expired).ToArray(), []));
            if (funds > 0 || contribution > 0)
                await _guildService.RefreshCaravanResourcesAsync(state.GuildId);
        }
        else await SaveAsync(roleId, next);
        return next;
    }

    public void Tick(GameTime time)
    {
        if (time.Now - _lastTick < TimeSpan.FromMinutes(1)) return;
        _lastTick = time.Now;
        // C/D 16: each role's rollover admits its own guild gate; the sweep needs no Realm (as Realm it waited behind
        // owned work for up to 16 s in S3). One sweep at a time: at 05:00 a sweep can outlast the minute.
        if (_sweeping) return;
        _sweeping = true;
        try
        {
            _loop.PostRuntime(async () =>
            {
                try { await ReconcileAllAsync(); }
                finally { _sweeping = false; }
            }, "guild caravan sweep");
        }
        catch { _sweeping = false; throw; }
    }
    private bool _sweeping;

    /// <summary>Settle yesterday's connected and offline orders at 05:00, once per persisted role.</summary>
    public async Task ReconcileAllAsync()
    {
        var roleIds = _accounts.ReadSync<List<long>>(new CaravanRoleIdsReadPlan());
        foreach (long roleId in roleIds)
        {
            var snapshot = Read(roleId);
            if (snapshot == null || snapshot.Day == Day) continue;
            try
            {
                // A failed admission skips this role only; the next sweep retries it.
                using var operation = await EnterWithGuildGateAsync("guild-caravan/rollover", snapshot.GuildId, roleId);
                var current = Read(roleId);
                if (current == null || current.Day == Day || current.GuildId != snapshot.GuildId) continue;
                await RolloverAsync(roleId, current, _roles.LoadProgress(roleId));
                if (_sessions.All().FirstOrDefault(x => x.Role?.Id == roleId) is { } online && Guild(online) is { } guild)
                {
                    var today = await EnsureAsync(online, guild);
                    await ShowCoreAsync(online, today);
                }
            }
            catch (Exception ex) { _log.LogError(ex, "Guild caravan reset failed for role {Role}", roleId); }
        }
    }

    private static DBGuildCatShopSku Proto(Sku sku) => new()
    {
        Index = sku.Index, CfgId = sku.CfgId, Status = (GuildCatShopItemStatu)sku.Status,
        HelperName = sku.HelperName, HelperId = sku.HelperId, ReqHelpTime = sku.ReqHelpTime,
    };

    private DBGuildCatShopInfo Proto(State state, long roleId)
    {
        var info = new DBGuildCatShopInfo
        {
            PlayerID = roleId, IsAwardGot = state.IsAwardGot, LastRefleshTime = state.LastRefreshTime,
            DayVersion = Version(state.GuildId, state.Day), HelperOtherCount = state.HelpOtherCount, CurRound = state.CurRound,
            RefreshTimes = state.RefreshTimes, ReqHelperOtherCount = state.ReqHelpOtherCount,
        };
        info.SkuData.AddRange(state.Skus.Select(Proto));
        return info;
    }

    private async Task PublishAsync(GameSession s, State state)
    {
        if (s.Guild == null || Guild(s)?.Id != state.GuildId) return;
        var next = s.Guild.Clone();
        next.CatShopInfo = Proto(state, s.Role!.Id);
        // (inferred) The guild activity red dot means this active order has every cargo slot loaded.
        next.IsCatShopAllSubmit = !state.IsAwardGot && state.Skus.Count > 0
            && state.Skus.All(x => x.Status == 1);
        if (!next.Equals(s.Guild))
        {
            var transaction = _roles.NewTransaction();
            transaction.RoleBlob(s.Role.Id, "guild_info", next.ToByteArray());
            transaction.WithEconomy(MetadataEnvelope(s.Role.Id, "projection"));
            await _roles.CommitAsync(transaction, "guild-caravan/projection");
        }
        s.Guild = next;
        await CharacterService.SyncAsync(s, GuildService.GuildSync, next);
    }

    private async Task ShowCoreAsync(GameSession s, State state, int result = 0)
    {
        var response = new GuildCatShopShowResp
        {
            Result = result, IsAwardGot = state.IsAwardGot, DayVersion = Version(state.GuildId, state.Day),
            HelpOtherCount = state.HelpOtherCount, CurRound = state.CurRound,
            RefreshTimes = state.RefreshTimes, ReqHelpOtherCount = state.ReqHelpOtherCount,
        };
        response.SkuData.AddRange(state.Skus.Select(Proto));
        await PublishAsync(s, state);
        await s.SendGameAsync(Show, response);
    }

    private async Task PushOtherShowsAsync(GuildStore.State guild, long exceptRole)
    {
        // A refresh or next round changes the shared version; open panels must receive it before their next action.
        foreach (var peer in _sessions.All().Where(x => x.Role is { } role && role.Id != exceptRole
            && guild.Members.ContainsKey(role.Id)).ToArray())
        {
            if (Guild(peer)?.Id != guild.Id) continue;
            var state = await EnsureAsync(peer, guild);
            await ShowCoreAsync(peer, state);
        }
    }

    private async Task RefreshDepartureTasksAsync(GameSession s)
    {
        using var scope = _loop.Owners is { } owners
            ? await owners.EnterAsync("guild-caravan/depart-task-refresh", ReservationKey.Role(s.Role!.Id)) : null;
        await _tasks.RefreshAsync(s);
    }

    public async Task LoginAsync(GameSession s)
    {
        var guild = Eligible(s) ? Guild(s) : null;
        // A helper can finish an offline member's cargo just before a kick. The earned
        // requester award remains role-owned and must be delivered even when guildless.
        long gateId = guild?.Id ?? Read(s.Role!.Id)?.GuildId ?? 0;
        if (gateId == 0) return;
        using var operation = await EnterWithCharacterGateAsync("guild-caravan/login", gateId, s, s.Role!.Id);
        try
        {
            if (guild == null)
            {
                var pendingState = Read(s.Role!.Id);
                if (pendingState == null) return;
                if (pendingState.Day != Day)
                    pendingState = await RolloverAsync(s.Role.Id, pendingState, s.Progress!);
                await DeliverPendingAsync(s, pendingState, pendingState.GuildId, operation.CharacterLocked);
                return;
            }
            var state = await EnsureAsync(s, guild);
            await DeliverPendingAsync(s, state, guild.Id, operation.CharacterLocked);
            await PublishAsync(s, state);
            await ShowCoreAsync(s, state);
        }
        catch (CaravanMembershipChangedException) { /* Guild membership changed during login. */ }
    }

    public async Task ShowAsync(GameSession s)
    {
        if (Guild(s) is not { } guild)
        { await s.SendGameAsync(Show, new GuildCatShopShowResp { Result = ErrorCodes.NotInGuild }); return; }
        if (!Eligible(s))
        { await s.SendGameAsync(Show, new GuildCatShopShowResp { Result = ErrorCodes.FeatureUnavailable }); return; }
        // Materialize the guild switch before waiting for the recipient's character gate.
        // A pending grant is a separate phase and re-reads the order after admission.
        using (var order = await EnterWithGuildGateAsync("guild-caravan/show-order", guild.Id, s.Role!.Id))
        {
            try { await EnsureAsync(s, guild); }
            catch (CaravanMembershipChangedException)
            { await s.SendGameAsync(Show, new GuildCatShopShowResp { Result = ErrorCodes.NotInGuild }); return; }
        }
        using var operation = await EnterWithCharacterGateAsync("guild-caravan/show-delivery", guild.Id, s, s.Role!.Id);
        try
        {
            if (Guild(s)?.Id != guild.Id) throw new CaravanMembershipChangedException();
            var state = await EnsureAsync(s, guild);
            await DeliverPendingAsync(s, state, guild.Id, operation.CharacterLocked);
            await ShowCoreAsync(s, state);
        }
        catch (CaravanMembershipChangedException)
        { await s.SendGameAsync(Show, new GuildCatShopShowResp { Result = ErrorCodes.NotInGuild }); }
    }

    private Sku? Slot(State state, long index) => state.Skus.FirstOrDefault(s => s.Index == index);
    private int Check(State state, long version, Sku? sku) => version != Version(state.GuildId, state.Day) ? ErrorCodes.CaravanOrderExpired
        : state.CurRound >= Setting("GuildTransportRoundNum", 5) ? ErrorCodes.CaravanDeparted
        : sku == null ? ErrorCodes.Parameter : 0;

    private static void EnlistMemberSave(RoleItemPlanBuilder transaction, long roleId, long guildId,
        State state, bool requireMembership)
    {
        long expected = state.Revision, next = checked(expected + 1);
        var copy = JsonSerializer.Deserialize<State>(JsonSerializer.SerializeToUtf8Bytes(state))!;
        copy.Revision = next;
        transaction.Add(new CaravanMemberWritePlan(roleId, guildId, requireMembership, expected, next,
            JsonSerializer.SerializeToUtf8Bytes(copy)), receipt => state.Revision = (long)receipt!);
    }

    private EconomyAsset Asset(int entry)
    {
        var row = _data.Row("ItemConfig", entry)
            ?? throw new InvalidOperationException($"Missing caravan award denomination {entry}.");
        return new EconomyAsset(GameData.Int(row, "_iType") == 1 ? EconomyAssetKind.Currency : EconomyAssetKind.Item, entry);
    }

    private static EconomyOwner RoleOwner(long role) => new(EconomyOwnerKind.Role,
        role.ToString(System.Globalization.CultureInfo.InvariantCulture));
    private static EconomyOwner GuildOwner(long guild) => new(EconomyOwnerKind.Guild,
        guild.ToString(System.Globalization.CultureInfo.InvariantCulture));
    private EconomyCompartment GuildMoneyCompartment(long guild)
    {
        if (_guilds.Get(guild) != null) return EconomyCompartment.GuildTreasury;
        if (_guilds.HasRecovery(guild)) return EconomyCompartment.Recovery;
        throw new InvalidOperationException($"Unresolved guild {guild} caravan funds require durable recovery.");
    }
    private static EconomyCompartment PersonalCompartment(EconomyAsset asset) =>
        asset.Kind == EconomyAssetKind.Currency ? EconomyCompartment.Currency : EconomyCompartment.Inventory;

    private List<EconomyEffect> AwardEffects(long role, long guild, AwardPlan plan, bool guildBooked,
        PendingAward? pendingDelivery = null)
    {
        var effects = new List<EconomyEffect>();
        int edge = 0;
        foreach (var grant in plan.Grants.Where(g => g.Entry is not (100 or 101)))
        {
            if (grant.Entry == 104)
            {
                if (guildBooked) continue; // The helper already created this treasury value.
                var asset = new EconomyAsset(EconomyAssetKind.GuildResource, EconomyDenominations.GuildMoney);
                if (pendingDelivery != null)
                {
                    effects.Add(new(asset, GuildOwner(guild), EconomyCompartment.CaravanPending,
                        -grant.Count, EconomyLegKind.Transfer, EconomyReason.GuildResourceTransfer, ++edge));
                    effects.Add(new(asset, GuildOwner(guild), GuildMoneyCompartment(guild),
                        grant.Count, EconomyLegKind.Transfer, EconomyReason.GuildResourceTransfer, edge));
                }
                else effects.Add(new(asset, GuildOwner(guild), GuildMoneyCompartment(guild),
                    grant.Count, EconomyLegKind.Source, EconomyReason.GuildResourceGrant));
                continue;
            }
            var personal = Asset(grant.Entry);
            if (pendingDelivery != null)
            {
                effects.Add(new(personal, RoleOwner(role), EconomyCompartment.CaravanPending,
                    -grant.Count, EconomyLegKind.Transfer, EconomyReason.CaravanPendingDelivery, ++edge));
                effects.Add(new(personal, RoleOwner(role), PersonalCompartment(personal),
                    grant.Count, EconomyLegKind.Transfer, EconomyReason.CaravanPendingDelivery, edge));
            }
            else effects.Add(new(personal, RoleOwner(role), PersonalCompartment(personal),
                grant.Count, EconomyLegKind.Source, EconomyReason.ActivityReward));
        }
        return effects;
    }

    private List<EconomyEffect> PendingCreationEffects(long role, long guild, AwardPlan plan)
    {
        var effects = new List<EconomyEffect>();
        foreach (var grant in plan.Grants.Where(g => g.Entry is not (100 or 101 or 104)))
            effects.Add(new(Asset(grant.Entry), RoleOwner(role), EconomyCompartment.CaravanPending,
                grant.Count, EconomyLegKind.Source, EconomyReason.ActivityReward));
        long funds = plan.Grants.Where(g => g.Entry == 104).Sum(g => (long)g.Count);
        if (funds > 0) effects.Add(new(new(EconomyAssetKind.GuildResource, EconomyDenominations.GuildMoney),
            GuildOwner(guild), GuildMoneyCompartment(guild), funds,
            EconomyLegKind.Source, EconomyReason.GuildResourceGrant));
        return effects;
    }

    private async Task<GetRewardNotify?> ExchangeAwardAsync(GameSession s, State state, long group, long guildId,
        IReadOnlyList<(int Entry, long Count)> cost, Action<State> mutate, Action<RoleItemPlanBuilder>? additional = null,
        AwardPlan? fixedPlan = null, Func<bool>? valid = null, bool guildBooked = false,
        bool deferResourceRefresh = false, IReadOnlyList<Ownership.ReservationKey>? ownerKeys = null,
        bool stateLockHeld = false,
        string? sourceKey = null, IReadOnlyList<EconomyEffect>? extraEffects = null, PendingAward? pendingDelivery = null)
    {
        var role = s.Role;
        var plan = fixedPlan ?? _awards.Resolve(s, group);
        var next = JsonSerializer.Deserialize<State>(JsonSerializer.SerializeToUtf8Bytes(state))!;
        mutate(next);
        long funds = plan.Grants.Where(g => g.Entry == 104).Sum(g => (long)g.Count);
        long contribute = plan.Grants.Where(g => g.Entry == 102).Sum(g => (long)g.Count);
        var goods = plan.Grants.Where(g => g.Entry is not (100 or 101 or 104) && g.BindType == 0)
            .Select(g => (g.Entry, g.Count)).ToArray();
        var bound = plan.Grants.Where(g => g.Entry is not (100 or 101 or 104) && g.BindType != 0)
            .Select(g => (g.Entry, g.Count, g.BindType)).ToArray();
        var effects = AwardEffects(role!.Id, guildId, plan, guildBooked, pendingDelivery);
        foreach (var (entry, count) in cost)
        {
            var asset = Asset(entry);
            effects.Add(new(asset, RoleOwner(role.Id), PersonalCompartment(asset),
                -count, EconomyLegKind.Sink, EconomyReason.ItemUse));
        }
        if (extraEffects != null) effects.AddRange(extraEffects);
        string operation = Guid.NewGuid().ToString("N");
        sourceKey ??= $"guild-caravan:operation:{role.Id}:{operation}";
        var economy = new EconomyEnvelope(operation, role.Account, role.Id,
            pendingDelivery == null ? "guild-caravan-award" : "guild-caravan-pending-delivery",
            sourceKey, sourceKey, NowMs, effects, []);
        using var guard = stateLockHeld ? null : ownerKeys == null
            ? await CharacterService.Lock(s) : await CharacterService.Lock(s, ownerKeys);
        if (!ReferenceEquals(s.Role, role) || s.Progress == null || state.Day != Day
            || !_sessions.All().Any(x => ReferenceEquals(x, s))
            || fixedPlan == null && (!Eligible(s) || Guild(s)?.Id != guildId || Read(role!.Id)?.GuildId != state.GuildId)
            || valid?.Invoke() == false) return null;
        List<InvItem>? committed;
        try
        {
            committed = await _character.ExchangeLockedAsync(s, cost, goods, boundGoods: bound,
            baseExp: plan.Grants.Where(g => g.Entry == 100).Sum(g => (long)g.Count),
            jobExp: plan.Grants.Where(g => g.Entry == 101).Sum(g => (long)g.Count),
            popup: false, persist: tx =>
            {
                EnlistMemberSave(tx, s.Role!.Id, guildId, next, fixedPlan == null);
                if (!guildBooked && (funds > 0 || contribute > 0))
                    _guilds.EnlistCaravanReward(guildId, s.Role.Id, funds, contribute, tx);
                additional?.Invoke(tx);
                tx.WithEconomy(economy);
            }, asyncCommit: true);
        }
        catch (CaravanMembershipChangedException) { return null; }
        if (committed == null) return null;
        Copy(next, state);
        var notify = new GetRewardNotify { TargetUid = s.Role!.Id };
        foreach (var grant in plan.Grants.Where(g => g.Entry is not (100 or 101)))
        {
            notify.Rewards.Add(new ItemBaseInfo { ItemEntry = grant.Entry, Stack = grant.Count });
            notify.RewardType.Add(1);
        }
        if (!deferResourceRefresh && !guildBooked && (funds > 0 || contribute > 0))
            await _guildService.RefreshCaravanResourcesAsync(guildId);
        return notify;
    }

    private static void Copy(State from, State into)
    {
        into.Revision = from.Revision;
        into.GuildId = from.GuildId; into.Day = from.Day; into.CurRound = from.CurRound;
        into.RefreshTimes = from.RefreshTimes; into.HelpOtherCount = from.HelpOtherCount;
        into.ReqHelpOtherCount = from.ReqHelpOtherCount; into.IsAwardGot = from.IsAwardGot;
        into.LastRefreshTime = from.LastRefreshTime; into.Skus = from.Skus; into.PendingAwards = from.PendingAwards;
    }

    private async Task DeliverPendingAsync(GameSession s, State state, long guildId, bool stateLockHeld = false)
    {
        while (state.PendingAwards.Count > 0)
        {
            var pending = state.PendingAwards[0];
            var plan = new AwardPlan(0, 0, pending.Grants
                .Select(g => new AwardGrant(g.Entry, g.Count, g.BindType, g.ExpireTimeStamp)));
            // Claim the member row and any unbooked guild credit together with StateLock.
            // Waiting for that gate must not hold a recipient reservation: a guild migration can replace this order.
            var memberKey = Ownership.ReservationKey.Entity("guild_caravan_member",
                s.Role!.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Ownership.ReservationKey[] ownerKeys = pending.GuildBooked
                ? [memberKey]
                : [memberKey, Ownership.ReservationKey.Entity("guild",
                    pending.GuildId.ToString(System.Globalization.CultureInfo.InvariantCulture))];
            // (inferred) The requester's resolved award waits for a live role, including a guildless login;
            // GuildBooked prevents its guild portion being credited again after a helper booked it.
            string source = pending.SourceKey.Length > 0 ? pending.SourceKey
                : $"guild-caravan:legacy-pending:{s.Role.Id}:{state.Day}:{state.Revision}";
            if (await ExchangeAwardAsync(s, state, 0, pending.GuildId, [], x => x.PendingAwards.RemoveAt(0),
                fixedPlan: plan, guildBooked: pending.GuildBooked, ownerKeys: ownerKeys, stateLockHeld: stateLockHeld,
                sourceKey: source + ":deliver", pendingDelivery: pending) == null) break;
        }
    }

    public async Task SubmitAsync(GameSession s, GuildCatShopItemSubmitReq req)
    {
        var resp = new GuildCatShopItemSubmitResp { SkuIndex = req.SkuIndex };
        if (Guild(s) is not { } guild) resp.Result = ErrorCodes.NotInGuild;
        else if (!Eligible(s)) resp.Result = ErrorCodes.FeatureUnavailable;
        else
        {
            using var operation = await EnterWithCharacterGateAsync("guild-caravan/submit", guild.Id, s, s.Role!.Id);
            try
            {
                var state = await EnsureAsync(s, guild);
                var sku = Slot(state, req.SkuIndex);
                resp.Result = Check(state, req.DayVersion, sku);
                if (resp.Result == 0 && sku!.Status != 0) resp.Result = ErrorCodes.CaravanLoaded;
                if (resp.Result == 0 && _data.Row("GuildTransportConfig", sku!.CfgId) is { } row)
                {
                    int entry = (int)GameData.Int(row, "_iItem"), count = (int)GameData.Int(row, "_iNum");
                    long award = SlotAward(sku, state.CurRound);
                    if (entry <= 0 || count <= 0 || award <= 0) resp.Result = ErrorCodes.Unknown;
                    else
                    {
                        var reward = await ExchangeAwardAsync(s, state, award, guild.Id, [(entry, count)],
                            next => next.Skus.Single(x => x.Index == req.SkuIndex).Status = 1,
                            stateLockHeld: operation.CharacterLocked,
                            sourceKey: $"guild-caravan:submit:{s.Role!.Id}:{state.Day}:{state.CurRound}:{req.SkuIndex}");
                        if (reward == null) resp.Result = ErrorCodes.ItemsShort;
                        else
                        {
                            resp.Award = reward; resp.Data = Proto(Slot(state, req.SkuIndex)!);
                            resp.SubmittedCount = state.Skus.Count(x => x.Status == 1);
                            resp.ReqHelperOtherCount = state.ReqHelpOtherCount;
                            resp.CurRound = state.CurRound;
                            await PublishAsync(s, state);
                        }
                    }
                }
            }
            catch (CaravanMembershipChangedException) { resp.Result = ErrorCodes.NotInGuild; }
        }
        await s.SendGameAsync(Submit, resp);
    }

    public async Task WantHelpAsync(GameSession s, GuildCatShopItemWantHelpReq req)
    {
        var resp = new GuildCatShopItemWantHelpResp { SkuIndex = req.SkuIndex };
        if (Guild(s) is not { } guild) resp.Result = ErrorCodes.NotInGuild;
        else if (!Eligible(s)) resp.Result = ErrorCodes.FeatureUnavailable;
        else
        {
            using var operation = await EnterWithGuildGateAsync("guild-caravan/want-help", guild.Id, s.Role!.Id);
            try
            {
                var state = await EnsureAsync(s, guild);
                var sku = Slot(state, req.SkuIndex);
                resp.Result = Check(state, req.DayVersion, sku);
                if (resp.Result == 0 && req.IsCancle && sku!.Status != 2) resp.Result = ErrorCodes.CaravanNotAsking;
                if (resp.Result == 0 && !req.IsCancle && sku!.Status != 0) resp.Result = ErrorCodes.CaravanCannotAsk;
                // (inferred) The client labels five as the request threshold; count only this owner's
                // loaded cargo. A helped slot cannot be submitted or requested again.
                if (resp.Result == 0 && !req.IsCancle && state.Skus.Count(x => x.Status == 1)
                    < Setting("GuildTransportRequestItemTypeNum", 5)) resp.Result = ErrorCodes.CaravanLoadShort;
                if (resp.Result == 0 && !req.IsCancle && state.ReqHelpOtherCount >= Setting("GuildTransportRequestDayNum", 3))
                    resp.Result = ErrorCodes.CaravanHelpLimit;
                // (inferred) reqHelpTime is wire milliseconds; cancellation clears the timestamp but
                // does not refund the daily allowance. The authored RequestCD is currently zero.
                if (resp.Result == 0 && !req.IsCancle && sku!.ReqHelpTime > 0
                    && sku.ReqHelpTime + Setting("GuildTransportRequestCD", 0) * 1000 > NowMs)
                    resp.Result = ErrorCodes.Cooldown;
                if (resp.Result == 0)
                {
                    sku!.Status = req.IsCancle ? 0 : 2;
                    sku.ReqHelpTime = req.IsCancle ? 0 : NowMs;
                    if (!req.IsCancle) state.ReqHelpOtherCount++;
                    await SaveAsync(s.Role!.Id, state);
                    resp.Data = Proto(sku); resp.ReqHelperOtherCount = state.ReqHelpOtherCount;
                    await PublishAsync(s, state);
                }
            }
            catch (CaravanMembershipChangedException) { resp.Result = ErrorCodes.NotInGuild; }
        }
        await s.SendGameAsync(WantHelp, resp);
    }

    public async Task HelpListAsync(GameSession s)
    {
        var resp = new GuildCatShopItemHelpListResp();
        if (Guild(s) is not { } guild) resp.Result = ErrorCodes.NotInGuild;
        else if (!Eligible(s)) resp.Result = ErrorCodes.FeatureUnavailable;
        else
        {
            using var operation = await EnterWithGuildGateAsync("guild-caravan/help-list", guild.Id, s.Role!.Id);
            try
            {
                var own = await EnsureAsync(s, guild);
                resp.DayVersion = Version(guild.Id, own.Day); resp.HelpOtherCount = own.HelpOtherCount;
                foreach (var member in _guilds.Get(guild.Id)?.Members.Values.AsEnumerable()
                    ?? Enumerable.Empty<GuildStore.Member>())
                    if (Read(member.RoleId) is { } other && other.GuildId == guild.Id && other.Day == own.Day)
                        foreach (var sku in other.Skus.Where(x => x.Status == 2))
                            resp.Skus.Add(new GuildCatShopItemHelpSku
                            { PlayerID = member.RoleId, PlayerName = member.Name, Data = Proto(sku) });
            }
            catch (CaravanMembershipChangedException) { resp.Result = ErrorCodes.NotInGuild; resp.Skus.Clear(); }
        }
        await s.SendGameAsync(HelpList, resp);
    }

    public async Task HelpOtherAsync(GameSession s, GuildCatShopItemHelpOtherReq req)
    {
        var resp = new GuildCatShopItemHelpOtherResp { SkuIndex = req.SkuIndex, PlayerID = req.PlayerID };
        if (Guild(s) is not { } guild) resp.Result = ErrorCodes.NotInGuild;
        else if (!Eligible(s)) resp.Result = ErrorCodes.FeatureUnavailable;
        else
        {
            var helperRole = s.Role!;
            var helperAttachment = s.Attachment;
            var helperOwner = s.Owner;
            long? helperEpoch = helperOwner?.LifecycleEpoch;
            long helperId = helperRole.Id;
            string helperName = helperRole.Name;
            State? committedOwn = null, committedTarget = null;
            bool refreshResources = false;
            // The helper payment also saves the recipient's order and may credit guild resources.
            // The helper payment, recipient pending grant and guild credit commit in one operation.
            // Release the gate with the owner scope before waiting for recipient delivery.
            using (var scope = await EnterWithCharacterGateAsync("guild-caravan/help-other", guild.Id, s,
                helperId, req.PlayerID))
            {
                if (!ReferenceEquals(s.Role, helperRole) || !ReferenceEquals(s.Attachment, helperAttachment)
                    || !ReferenceEquals(s.Owner, helperOwner) || s.Owner?.LifecycleEpoch != helperEpoch
                    || Guild(s)?.Id != guild.Id) resp.Result = ErrorCodes.NotInGuild;
                else
                {
                    if (s.Owner is { } owner) scope.Context.Pin(owner);
                    try
                    {
                        if (!ReferenceEquals(s.Role, helperRole) || !ReferenceEquals(s.Attachment, helperAttachment)
                            || !ReferenceEquals(s.Owner, helperOwner) || s.Owner?.LifecycleEpoch != helperEpoch
                            || Guild(s)?.Id != guild.Id) throw new CaravanMembershipChangedException();
                        var own = await EnsureAsync(s, guild);
                        var target = guild.Members.ContainsKey(req.PlayerID) ? Read(req.PlayerID) : null;
                        var sku = target == null ? null : Slot(target, req.SkuIndex);
                        resp.Result = req.PlayerID == helperId ? ErrorCodes.CaravanSelfHelp
                            : target == null || target.GuildId != guild.Id ? ErrorCodes.GuildMemberMissing
                            : req.DayVersion != Version(guild.Id, own.Day) ? ErrorCodes.CaravanOrderExpired
                            : sku == null ? ErrorCodes.Parameter : 0;
                        if (resp.Result == 0 && target!.Day != own.Day) resp.Result = ErrorCodes.CaravanOrderExpired;
                        if (resp.Result == 0 && sku!.Status != 2) resp.Result = ErrorCodes.CaravanRequestMissing;
                        if (resp.Result == 0 && own.HelpOtherCount >= Setting("GuildTransportHelpDayNum", 3))
                            resp.Result = ErrorCodes.CaravanHelpLimit;
                        if (resp.Result == 0 && _data.Row("GuildTransportConfig", sku!.CfgId) is { } row)
                        {
                            int entry = (int)GameData.Int(row, "_iItem"), count = (int)GameData.Int(row, "_iNum");
                            long targetAward = SlotAward(sku, target!.CurRound);
                            long helperAward = GameData.Int(row, "_iAward");
                            if (entry <= 0 || count <= 0 || targetAward <= 0 || helperAward <= 0) resp.Result = ErrorCodes.Unknown;
                            else
                            {
                                var targetNext = JsonSerializer.Deserialize<State>(JsonSerializer.SerializeToUtf8Bytes(target))!;
                                var slot = targetNext.Skus.Single(x => x.Index == req.SkuIndex);
                                slot.Status = 1; slot.HelperName = helperName; slot.HelperId = helperId; slot.ReqHelpTime = 0;
                                var targetOnline = _sessions.All().FirstOrDefault(x => x.Role?.Id == req.PlayerID);
                                var targetPlan = _awards.ResolveForProgress(targetOnline?.Progress ?? _roles.LoadProgress(req.PlayerID), targetAward);
                                // The helper books the recipient's configured award now; personal grants remain pending.
                                targetNext.PendingAwards.Add(new PendingAward
                                {
                                    SourceKey = $"guild-caravan:help:{guild.Id}:{req.PlayerID}:{target.Day}:{target.CurRound}:{req.SkuIndex}",
                                    GuildId = guild.Id, GuildBooked = true,
                                    Grants = targetPlan.Grants.Select(g => new PendingGrant
                                    { Entry = g.Entry, Count = g.Count, BindType = g.BindType, ExpireTimeStamp = g.ExpireTimeStamp }).ToList()
                                });
                                long targetFunds = targetPlan.Grants.Where(g => g.Entry == 104).Sum(g => (long)g.Count);
                                long targetContribution = targetPlan.Grants.Where(g => g.Entry == 102).Sum(g => (long)g.Count);
                                var reward = await ExchangeAwardAsync(s, own, helperAward, guild.Id, [(entry, count)],
                                    next => next.HelpOtherCount++, tx =>
                                    {
                                        EnlistMemberSave(tx, req.PlayerID, guild.Id, targetNext, true);
                                        if (targetFunds > 0 || targetContribution > 0)
                                            _guilds.EnlistCaravanReward(guild.Id, req.PlayerID, targetFunds, targetContribution, tx);
                                    }, valid: () => _guilds.ForRole(req.PlayerID)?.Id == guild.Id,
                                    deferResourceRefresh: true,
                                    stateLockHeld: scope.CharacterLocked,
                                    sourceKey: $"guild-caravan:help:{guild.Id}:{req.PlayerID}:{target.Day}:{target.CurRound}:{req.SkuIndex}",
                                    extraEffects: PendingCreationEffects(req.PlayerID, guild.Id, targetPlan));
                                if (reward == null) resp.Result = ErrorCodes.ItemsShort;
                                else
                                {
                                    resp.ItemId = entry; resp.ItemNum = count;
                                    committedOwn = own;
                                    committedTarget = targetNext;
                                    refreshResources = targetFunds > 0 || targetContribution > 0
                                        || reward.Rewards.Any(item => item.ItemEntry is 102 or 104);
                                }
                            }
                        }
                    }
                    catch (CaravanMembershipChangedException) { resp.Result = ErrorCodes.NotInGuild; }
                }
            }
            if (committedOwn != null && committedTarget != null)
            {
                // Notifications may persist role guild-info, so use a new short realm scope.
                using (var noticeScope = _loop.Owners is { } noticeOwners
                    ? await noticeOwners.EnterAsync("guild-caravan/help-notice", [Ownership.ReservationKey.Realm]) : null)
                {
                    if (refreshResources) await _guildService.RefreshCaravanResourcesAsync(guild.Id);
                    if (ReferenceEquals(s.Role, helperRole) && ReferenceEquals(s.Attachment, helperAttachment)
                        && ReferenceEquals(s.Owner, helperOwner) && s.Owner?.LifecycleEpoch == helperEpoch
                        && _sessions.All().Any(x => ReferenceEquals(x, s))
                        && Guild(s)?.Id == guild.Id
                        && Read(helperId) is { } currentOwn && currentOwn.GuildId == guild.Id)
                        await PublishAsync(s, currentOwn);
                }
                // Delivery is intentionally outside the helper's two-role reservation and old guild gate.
                // A guild migration may replace the recipient's order while its StateLock is unavailable.
                if (_sessions.All().FirstOrDefault(x => x.Role?.Id == req.PlayerID) is { } online
                    && online.Attachment?.Cancelled != true && online.Owner?.State != RoleOwnerState.Quiescing
                    && Guild(online)?.Id == guild.Id)
                {
                    var recipientRole = online.Role;
                    var recipientAttachment = online.Attachment;
                    var recipientOwner = online.Owner;
                    long? recipientEpoch = recipientOwner?.LifecycleEpoch;
                    try
                    {
                        // Wait for the recipient's character gate before reserving its role and order.
                        // A kick/join can replace the order while the target is unavailable.
                        using (var delivery = await EnterWithCharacterGateAsync(
                            "guild-caravan/recipient-delivery", guild.Id, online, req.PlayerID))
                            if (Read(req.PlayerID) is { } currentPending)
                                await DeliverPendingAsync(online, currentPending, currentPending.GuildId,
                                    delivery.CharacterLocked);
                        if (ReferenceEquals(online.Role, recipientRole)
                            && ReferenceEquals(online.Attachment, recipientAttachment)
                            && ReferenceEquals(online.Owner, recipientOwner)
                            && online.Owner?.LifecycleEpoch == recipientEpoch
                            && _sessions.All().Any(x => ReferenceEquals(x, online))
                            && Guild(online)?.Id == guild.Id)
                        {
                            using var noticeScope = _loop.Owners is { } recipientOwners
                                ? await recipientOwners.EnterAsync("guild-caravan/recipient-notice",
                                    [Ownership.ReservationKey.Role(req.PlayerID)]) : null;
                            if (ReferenceEquals(online.Role, recipientRole)
                                && ReferenceEquals(online.Attachment, recipientAttachment)
                                && ReferenceEquals(online.Owner, recipientOwner)
                                && online.Owner?.LifecycleEpoch == recipientEpoch
                                && _sessions.All().Any(x => ReferenceEquals(x, online))
                                && Guild(online)?.Id == guild.Id
                                && Read(req.PlayerID) is { } currentOrder && currentOrder.GuildId == guild.Id)
                            {
                                await ShowCoreAsync(online, currentOrder);
                                await online.SendGameAsync(HelpNotify, new GuildCatShopHelpNotify { HelperName = helperName });
                            }
                        }
                    }
                    catch (OperationCanceledException) when (recipientAttachment?.Cancelled == true
                        || !ReferenceEquals(online.Attachment, recipientAttachment)
                        || !ReferenceEquals(online.Owner, recipientOwner)
                        || !_sessions.All().Any(x => ReferenceEquals(x, online)))
                    {
                        // The helper payment and pending grant already committed. A detached
                        // recipient can claim it at login; preserve the helper's success reply.
                        _log.LogDebug("Guild caravan recipient {Role} detached after committed help", req.PlayerID);
                    }
                }
            }
        }
        await s.SendGameAsync(HelpOther, resp);
    }

    public async Task DepartAsync(GameSession s)
    {
        var resp = new GuildCatShopDepartResp();
        if (Guild(s) is not { } guild) resp.Result = ErrorCodes.NotInGuild;
        else if (!Eligible(s)) resp.Result = ErrorCodes.FeatureUnavailable;
        else
        {
            // Task settlement can auto-deliver a reward and acquire StateLock itself.
            // Complete it before admitting the combined guild/character payment scope.
            await RefreshDepartureTasksAsync(s);
            bool departed = false;
            using (var operation = await EnterWithCharacterGateAsync("guild-caravan/depart", guild.Id, s, s.Role!.Id))
            {
                try
                {
                    var state = await EnsureAsync(s, guild);
                    int done = state.Skus.Count(x => x.Status == 1);
                    resp.Result = state.CurRound >= Setting("GuildTransportRoundNum", 5) ? ErrorCodes.CaravanDeparted
                        : done < Setting("GuildTransportRequestItemTypeNum", 5) ? ErrorCodes.CaravanLoadShort : 0;
                    if (resp.Result == 0)
                    {
                        long group = DepartureAward(state);
                        if (group == 0) resp.Result = ErrorCodes.Unknown;
                        else
                        {
                            GetRewardNotify? reward;
                            using (var taskGate = await TaskService.LockTasksAsync(s))
                            {
                                var staged = _tasks.StageCaravanDeparture(s);
                                reward = await ExchangeAwardAsync(s, state, group, guild.Id, [], next =>
                                {
                                    next.CurRound++;
                                    // (inferred) IsAwardGot marks exhaustion of all daily departures,
                                    // while the active order can still depart with five through eight loaded slots.
                                    next.IsAwardGot = next.CurRound >= Setting("GuildTransportRoundNum", 5);
                                    next.Skus.Clear();
                                }, tx =>
                                {
                                    tx.Add(new CaravanVersionPlan(guild.Id, state.Day));
                                    if (staged != null) tx.RoleBlob(s.Role!.Id, "tasks", staged.ToByteArray());
                                }, stateLockHeld: operation.CharacterLocked,
                                    sourceKey: $"guild-caravan:depart:{s.Role!.Id}:{state.Day}:{state.CurRound}");
                                if (reward != null && staged != null) s.Tasks = staged;
                            }
                            if (reward == null) resp.Result = ErrorCodes.Unknown;
                            else
                            {
                                resp.AwardId = group; resp.Award = reward;
                                if (!state.IsAwardGot) { state.Skus = Generate(s, state.Day, state.CurRound); await SaveAsync(s.Role!.Id, state); }
                                await PublishAsync(s, state);
                                await ShowCoreAsync(s, state);
                                await PushOtherShowsAsync(guild, s.Role!.Id);
                                departed = true;
                            }
                        }
                    }
                }
                catch (CaravanMembershipChangedException) { resp.Result = ErrorCodes.NotInGuild; }
            }
            if (departed) await RefreshDepartureTasksAsync(s);
        }
        await s.SendGameAsync(Depart, resp);
    }

    public async Task RefreshAsync(GameSession s, GuildCatShopRefreshReq req)
    {
        var resp = new GuildCatShopRefreshResp();
        if (Guild(s) is not { } guild) resp.Result = ErrorCodes.NotInGuild;
        else if (!Eligible(s)) resp.Result = ErrorCodes.FeatureUnavailable;
        else
        {
            using var operation = await EnterWithGuildGateAsync("guild-caravan/refresh", guild.Id, s.Role!.Id);
            try
            {
                var state = await EnsureAsync(s, guild);
                resp.Result = req.DayVersion != Version(guild.Id, state.Day) ? ErrorCodes.CaravanRefreshExpired
                    : state.IsAwardGot ? ErrorCodes.CaravanDeparted
                    : state.RefreshTimes >= Setting("GuildTransportRefreshNum", 5) ? ErrorCodes.CaravanRefreshLimit
                    : state.Skus.All(x => x.Status != 0) ? ErrorCodes.CaravanRefreshUnavailable : 0;
                if (resp.Result == 0)
                {
                    var replacement = Generate(s, state.Day, state.CurRound, state.RefreshTimes + 1);
                    foreach (var sku in state.Skus.Where(x => x.Status == 0))
                    {
                        long type = _data.Row("GuildTransportConfig", sku.CfgId) is { } current
                            ? GameData.Int(current, "_iType") : 0;
                        var fresh = replacement.FirstOrDefault(x => _data.Row("GuildTransportConfig", x.CfgId) is { } row
                            && GameData.Int(row, "_iType") == type && x.CfgId != sku.CfgId)
                            ?? replacement.FirstOrDefault(x => _data.Row("GuildTransportConfig", x.CfgId) is { } row
                                && GameData.Int(row, "_iType") == type);
                        if (fresh == null) continue;
                        sku.CfgId = fresh.CfgId; replacement.Remove(fresh);
                    }
                    state.RefreshTimes++; state.LastRefreshTime = NowMs;
                    await SaveBumpAsync(s.Role!.Id, state);
                    await ShowCoreAsync(s, state);
                    await PushOtherShowsAsync(guild, s.Role!.Id);
                }
            }
            catch (CaravanMembershipChangedException) { resp.Result = ErrorCodes.NotInGuild; }
        }
        await s.SendGameAsync(Refresh, resp);
    }
}
