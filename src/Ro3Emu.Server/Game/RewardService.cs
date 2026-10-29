using System.Text.Json;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Kill rewards: the monster's drop chains are rolled for the killer
/// (NPCConfig._iDrop / _iNormalDrop → MultiverseGroupConfig → NPCDropConfig[env 0 reduced / env 1 Bonus Time, inferred]
/// / additive NpcNormalDropConfig → DropPoolConfig slots → weighted DropGroupConfig rows). Item 100 rows are Base EXP,
/// scaled by LvExpCorrectionConfig; Job EXP equals Base EXP (inferred). Currencies go to the currency map, everything
/// else to the bag; then one 31322 drop notify makes
/// the client animate the loot from the corpse.
/// </summary>
public sealed class RewardService : IGameTicker
{
    private const int BaseExpItem = 100, JobExpItem = 101;
    private const int RewardDrop = 8; // ManifestType Drop

    private readonly CharacterService _character;
    private readonly TeamService _teams;
    private readonly GameData _data;
    private readonly ILogger<RewardService> _log;
    private readonly Random _random = new();
    private Dictionary<int, long>? _expCorrection;
    private readonly DropAttenuation _attenuation;
    private readonly RewardRateOptions _rates;
    private readonly SessionRegistry _sessions;
    private readonly RoleObligations<PartyExperienceObligationRow> _partyExperience;

    /// <summary>Field AFK selects the enhanced _iDrop table only for qualified ordinary field kills.
    /// Other kills use environment 0, preserving ordinary rewards (localization 10960000077; mapping inferred).</summary>
    public Func<GameSession, MonsterUnit, Task<bool>>? QualifiesForBonus { get; set; }
    /// <summary>Actual committed kill drops, after EXP correction; excludes GM, quest and other item grants.</summary>
    public List<Func<GameSession, MonsterUnit, IReadOnlyList<(int Entry, long Count)>, Task>> KillDropsGranted { get; } = new();
    internal Action<GameSession, MonsterUnit, RewardSettlement, IReadOnlyList<(int Entry, int Count)>, long, long>? StageAfkKill { get; set; }
    /// <summary>Monster Hunt: the area drop rolled with an ordinary field kill (MonsterHuntService).</summary>
    internal List<Func<GameSession, MonsterUnit, IEnumerable<(int Entry, int Count)>>> ExtraKillDrops { get; } = [];
    /// <summary>Monster Hunt: its kill counters join the kill's own transaction, after the AFK child (MonsterHuntService).</summary>
    internal Action<GameSession, MonsterUnit, RewardSettlement>? StageHuntKill { get; set; }

    public RewardService(CombatService combat, CharacterService character, TeamService teams, GameData data,
        AccountStore accounts, SessionRegistry sessions, GameLoop loop, TimeProvider time,
        IOptions<ServerOptions> options, ILogger<RewardService> log)
    {
        this._character = character;
        this._teams = teams;
        this._data = data;
        this._log = log;
        this._sessions = sessions;
        _rates = options.Value.RewardRates;
        _attenuation = new DropAttenuation(data);
        _partyExperience = new RoleObligations<PartyExperienceObligationRow>("Party experience", accounts, loop, time,
            Online, s => s.Role != null && s.Progress != null && s.Attachment?.Cancelled != true,
            DeliverPartyExperienceAsync, ObligationRetry.TickWithBackoff, log);
        combat.BeforeKilled.Add(PrepareKilledAsync);
        combat.Killed.Add(KilledAsync);
    }

    private sealed class KillOutcome
    {
        internal required Role Role;
        internal required List<(int Entry, int Count)> Items;
        internal long BaseExp;
        internal long JobExp;
        internal required PartyExperienceObligationRow[] PartyExperience;
        internal bool Published;
    }
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<MonsterUnit, KillOutcome> _outcomes = new();
    internal Func<GameSession, MonsterUnit, (List<(int Entry, int Count)> Items, long BaseExp, long JobExp)>? ResolveKillForTest { get; set; }

    /// <summary>Resolve and admit once before any lethal hit/death packet. A rejected pre-admission hit is
    /// not a successful kill. An accepted write retains its role owner through detach and durable completion.</summary>
    internal Task PrepareKilledAsync(GameSession player, MonsterUnit monster)
    {
        if (monster.RewardSettlement?.Committed == true) return Task.CompletedTask;
        if (monster.RewardCommit != null) return monster.RewardCommit;
        if (player.Role == null) throw new OperationCanceledException("Kill has no recipient.");
        if (monster.RewardRecipient != 0 && monster.RewardRecipient != player.Role.Id)
            throw new InvalidOperationException("Staged kill belongs to another recipient.");
        monster.RewardRecipient = player.Role.Id;
        monster.RewardSource ??= new RewardSourceId(_character.RewardRealmId, RewardSourceId.ProcessRun, monster.SpawnGeneration);
        monster.RewardSettlement ??= new RewardSettlement(monster.RewardSource.Key, "monster-kill", autoDecompose: true)
        { DeferPresentation = true };
        return monster.RewardCommit = CommitKillAsync(player, monster);
    }

    private async Task CommitKillAsync(GameSession player, MonsterUnit monster)
    {
        var role = player.Role!;
        var afk = player.WildAfk;
        bool SameRole() => ReferenceEquals(player.Role, role) && ReferenceEquals(player.WildAfk, afk)
            && player.Progress != null && player.Attachment?.Cancelled != true;
        var settlement = monster.RewardSettlement!;
        try
        {
            if (await _character.RewardSourceCommittedAsync(settlement.SourceKey))
            { settlement.Committed = true; return; }
            // Bonus eligibility may wait on the AFK owner; no successful lethal output precedes admission.
            bool bonus = QualifiesForBonus != null && await QualifiesForBonus(player, monster);
            var taskParticipants = _character.GetKillTaskParticipantKeys?.Invoke(player, monster) ?? [];
            if (_outcomes.TryGetValue(monster, out var stale) && stale.Role.Id != role.Id) _outcomes.Remove(monster);
            _outcomes.TryGetValue(monster, out var existing);
            var candidates = existing == null
                ? _teams.ExperienceRecipients(player)
                    .Select(s => new PartyCandidate(s, s.Role!.Id, s.Attachment)).ToArray()
                : new[] { new PartyCandidate(player, role.Id, player.Attachment) }
                    .Concat(existing.PartyExperience.Select(row =>
                        new PartyCandidate(Online(row.RoleId), row.RoleId, Online(row.RoleId)?.Attachment))).ToArray();
            var partyKeys = candidates.SelectMany(candidate => new[]
                {
                    Ownership.ReservationKey.Role(candidate.RoleId),
                    RoleObligations<PartyExperienceObligationRow>.Key(PartyExperienceSource(settlement, candidate.RoleId))
                })
                .Concat(taskParticipants).Distinct().ToArray();
            using (await CharacterService.Lock(player, partyKeys))
            {
                if (!SameRole()) throw new OperationCanceledException("Kill recipient detached before reward admission.");
                if (!_outcomes.TryGetValue(monster, out var outcome))
                {
                    var eligibleNow = _teams.ExperienceRecipients(player).ToHashSet(ReferenceEqualityComparer.Instance);
                    var eligible = candidates.Where(candidate => candidate.Session != null && candidate.RoleId == candidate.Session.Role?.Id &&
                            ReferenceEquals(candidate.Attachment, candidate.Session.Attachment) && eligibleNow.Contains(candidate.Session))
                        .Select(candidate => candidate.Session!).ToArray();
                    if (!eligible.Contains(player, ReferenceEqualityComparer.Instance)) eligible = [player];
                    var (items, baseExp, jobExp) = ResolveKill(player, monster, bonus);
                    items = items.Select(item =>
                        {
                            if (_data.Row("ItemConfig", item.Entry) is { } row && new ItemConfig(row).Type == 1)
                                return item.Entry == 1
                                    ? (item.Entry, _rates.ScaleZenyToInt(item.Count))
                                    : item;
                            return (item.Entry, _rates.ScaleMonsterDrop(item.Count, _random));
                        })
                        .Where(item => item.Item2 > 0).ToList();
                    baseExp = _rates.ScaleBaseExperience(baseExp);
                    jobExp = _rates.ScaleJobExperience(jobExp);
                    var shares = SplitPartyExperience(settlement, eligible, baseExp, jobExp, role.Id);
                    outcome = new KillOutcome
                    {
                        Role = role, Items = items, BaseExp = shares.KillerBaseExp, JobExp = shares.KillerJobExp,
                        PartyExperience = shares.Obligations
                    };
                    _outcomes.Add(monster, outcome);
                }
                StagePartyExperience(settlement, outcome.PartyExperience);
                _character.StageKillTaskObligations?.Invoke(player, monster, settlement);
                // A retry re-stages: the later stage is undone first, so the AFK reset never restores a stale wrapper.
                settlement.ResetHuntStage?.Invoke();
                StageAfkKill?.Invoke(player, monster, settlement, outcome.Items, outcome.BaseExp, outcome.JobExp);
                StageHuntKill?.Invoke(player, monster, settlement);
                settlement.LibraryMonster = monster.Template.NpcId;
                await _character.ExchangeLockedAsync(player, [], outcome.Items, popup: false, sync: outcome.Items.Count > 0,
                    baseExp: outcome.BaseExp, jobExp: outcome.JobExp, rewardSettlement: settlement,
                    persist: settlement.Enlist, committed: settlement.Applied);
                foreach (var row in outcome.PartyExperience) _partyExperience.Track(row);
            }
        }
        catch
        {
            // A failed apply/notice cannot resurrect a committed source. The owned writer quarantines
            // ambiguous/failed live state; restart and login reload the durable bag and progression.
            try
            {
                if (await _character.RewardSourceCommittedAsync(settlement.SourceKey))
                {
                    settlement.Committed = true;
                    // Failed Apply cannot safely run room/quest callbacks on an old owner snapshot.
                    // Abort the attachment; process recovery reloads the committed outcome and durable obligations.
                    player.CancelPendingCharacterIntents();
                    player.AbortForRecovery();
                }
                else monster.RewardCommit = null;
            }
            catch
            {
                settlement.OutcomeUnknown = true;
                player.CancelPendingCharacterIntents();
                player.AbortForRecovery();
            }
            throw;
        }
    }

    private sealed record PartyCandidate(GameSession? Session, long RoleId, object? Attachment);
    private sealed record PartyShares(long KillerBaseExp, long KillerJobExp,
        PartyExperienceObligationRow[] Obligations);

    private static string PartyExperienceSource(RewardSettlement settlement, long roleId) =>
        $"{settlement.SourceKey}/party-exp/{roleId}";

    /// <summary>
    /// Server policy while the publisher formula remains unobservable: the already corrected, boosted and rate-scaled
    /// field EXP pool is divided evenly among the frozen five-slot recipients.  Any integer remainder follows slot order.
    /// </summary>
    private static PartyShares SplitPartyExperience(RewardSettlement settlement,
        IReadOnlyList<GameSession> recipients, long baseExp, long jobExp, long killerRole)
    {
        int count = Math.Max(1, recipients.Count);
        static long Share(long total, int divisor, int index) =>
            total <= 0 ? 0 : total / divisor + (index < total % divisor ? 1 : 0);
        long killerBase = 0, killerJob = 0;
        var obligations = new List<PartyExperienceObligationRow>();
        for (int i = 0; i < recipients.Count; i++)
        {
            long role = recipients[i].Role!.Id;
            long baseShare = Share(baseExp, count, i), jobShare = Share(jobExp, count, i);
            if (role == killerRole)
            {
                killerBase = baseShare;
                killerJob = jobShare;
            }
            else if (baseShare > 0 || jobShare > 0)
            {
                obligations.Add(new PartyExperienceObligationRow(PartyExperienceSource(settlement, role),
                    role, baseShare, jobShare));
            }
        }
        return new PartyShares(killerBase, killerJob, obligations.ToArray());
    }

    private static void StagePartyExperience(RewardSettlement settlement,
        IReadOnlyList<PartyExperienceObligationRow> obligations)
    {
        if (obligations.Count == 0 || settlement.PartyExperienceStaged) return;
        var prior = settlement.Enlist;
        settlement.Enlist = builder =>
        {
            prior?.Invoke(builder);
            builder.Add(RoleObligations<PartyExperienceObligationRow>.CreatePlan(obligations));
        };
        settlement.PartyExperienceStaged = true;
    }

    private GameSession? Online(long roleId) =>
        _sessions.All().FirstOrDefault(session => session.Role?.Id == roleId);

    private async Task<bool> DeliverPartyExperienceAsync(GameSession session, PartyExperienceObligationRow row)
    {
        using (await CharacterService.Lock(session,
                   [RoleObligations<PartyExperienceObligationRow>.Key(row.SourceKey)]))
        {
            if (session.Role?.Id != row.RoleId || session.Progress == null || session.Attachment?.Cancelled == true)
                throw new OperationCanceledException("Party experience recipient left the game.");
            var grants = new List<AwardGrant>();
            long remainingJob = row.JobExp;
            while (remainingJob > 0)
            {
                int part = (int)Math.Min(int.MaxValue, remainingJob);
                grants.Add(new AwardGrant(JobExpItem, part));
                remainingJob -= part;
            }
            bool paid = await _character.ApplyAwardAsync(session, new AwardPlan(0, 0, grants),
                taskBaseExp: row.BaseExp, persist: builder =>
                    builder.Add(RoleObligations<PartyExperienceObligationRow>.DeliverPlan(row)),
                stateLockHeld: true);
            if (!paid) throw new InvalidOperationException("Frozen party experience was not delivered.");
            return true;
        }
    }

    internal Task<Ownership.ReservationKey[]> PartyExperienceLoginKeysAsync(long roleId) =>
        _partyExperience.LoginKeysAsync(roleId);

    internal Task RecoverPartyExperienceAsync(GameSession session) => session.Role is not { } role
        ? Task.CompletedTask
        : _partyExperience.DeliverPendingAsync(session, role.Id,
            () => session.Role?.Id == role.Id && session.Attachment?.Cancelled != true);

    public void Tick(GameTime _) => _partyExperience.Tick();

    private (List<(int Entry, int Count)> Items, long BaseExp, long JobExp) ResolveKill(GameSession player, MonsterUnit monster, bool bonus)
    {
        if (ResolveKillForTest != null) return ResolveKillForTest(player, monster);
        // Team-boss personal awards have their own durable dungeon obligation; ordinary loot stays empty.
        if (player.DungeonAttempt is { Plan.Multi: not null } dungeon && monster.InstanceId == dungeon.Room.Id)
            return ([], 0, 0);
        if (_data.Row("NPCConfig", monster.Template.NpcId) is not { } npc) return ([], 0, 0);
        var drops = new List<(int Entry, int Count)>();
        // (inferred) NPCDropConfig environment 0 is reduced and 1 is Bonus Time: the former carries
        // small EXP at player levels 1-99, the latter larger EXP. Fatigue environment 2 has no proven trigger.
        // _iNormalDrop remains additive under its own level/map conditions; no numeric multiplier is invented.
        if (Resolve(GameData.Int(npc, "_iDrop")) is var dropId and > 0 && _data.Row("NPCDropConfig", dropId) is { } nd
            && nd.ValueKind == JsonValueKind.Object && nd.TryGetProperty(bonus ? "1" : "0", out var env))
            RollPools(env, player, drops, monster.Template.Level);
        if (npc.TryGetProperty("_iNormalDrop", out var normals) && normals.ValueKind == JsonValueKind.Array)
        {
            foreach (var n in normals.EnumerateArray())
            {
                if (GameData.Number(n) is not { } nid || Resolve((long)nid) is not (var id and > 0) || _data.Row("NpcNormalDropConfig", id) is not { } row) continue;
                if (row.TryGetProperty("_iMapId", out var maps) && maps.ValueKind == JsonValueKind.Array && maps.GetArrayLength() > 0
                    && !maps.EnumerateArray().Any(m => GameData.Number(m) == player.SceneEntry))
                    continue;
                RollPools(row, player, drops, monster.Template.Level);
            }
        }

        foreach (var extra in ExtraKillDrops) drops.AddRange(extra(player, monster));
        long baseExp = drops.Where(d => d.Entry == BaseExpItem).Sum(d => (long)d.Count);
        var items = drops.Where(d => d.Entry is not (BaseExpItem or JobExpItem) && _data.Row("ItemConfig", d.Entry) != null).ToList();
        if (baseExp > 0)
        {
            long gap = player.Progress!.Level - monster.Template.Level;
            baseExp = baseExp * Correction((int)Math.Clamp(gap, -200, 200)) / 10000;
            baseExp = Math.Max(1, baseExp * _teams.ExpBonus(player) / 10000);
        }
        // BaseExpAddConfig catch-up below the server level raises Base EXP only (CharacterService.LevelRules).
        return (items, _character.WithPursueBonus(player.Progress!.Level, baseExp), baseExp);
    }

    internal async Task KilledAsync(GameSession player, MonsterUnit monster)
    {
        await PrepareKilledAsync(player, monster); // also covers existing service/test callbacks
        if (!_outcomes.TryGetValue(monster, out var outcome) || outcome.Published) return;
        outcome.Published = true;
        var role = outcome.Role;
        if (!ReferenceEquals(player.Role, role) || player.Attachment?.Cancelled == true) return;
        using (await CharacterService.Lock(player))
        {
            if (!ReferenceEquals(player.Role, role) || player.Attachment?.Cancelled == true) return;
            await monster.RewardSettlement!.PresentAsync();
            if (outcome.Items.Count > 0)
            {
                var notify = new GetRewardNotify
                {
                    Position = monster.Pos, TargetEntry = monster.Template.NpcId,
                    TargetUid = role.Id, RewardType = { RewardDrop }
                };
                foreach (var (entry, count) in outcome.Items)
                    notify.Rewards.Add(new ItemBaseInfo
                    {
                        ItemEntry = entry, Stack = count,
                        ItemInfos = { [10001] = new ItemAttrValue { LongVal = 0 } }
                    });
                await player.SendGameAsync(CharacterService.GetRewardNotify, notify);
            }
        }
        // The death is now published.  Pay every frozen share independently; a disconnect or failure leaves its
        // obligation for the retry ticker/login, while the kill source and the whole recipient set remain durable.
        foreach (var row in outcome.PartyExperience)
        {
            if (Online(row.RoleId) is { Progress: not null } recipient && recipient.Attachment?.Cancelled != true)
                await _partyExperience.TryPayAsync(recipient, row);
            else _partyExperience.Forget(row.SourceKey);
        }
        var granted = outcome.Items.Select(i => (i.Entry, Count: (long)i.Count)).ToList();
        if (outcome.BaseExp > 0) granted.Add((BaseExpItem, outcome.BaseExp));
        if (outcome.JobExp > 0) granted.Add((JobExpItem, outcome.JobExp));
        foreach (var handler in KillDropsGranted)
        {
            if (!ReferenceEquals(player.Role, role) || player.Attachment?.Cancelled == true) return;
            await handler(player, monster, granted);
        }
        _log.LogInformation("{Name} durable loot from {Npc}: exp {Exp}, source {Source}",
            role.Name, monster.Template.NpcId, outcome.BaseExp, monster.RewardSource!.Key);
    }
    /// <summary>MultiverseGroupConfig id → the playbook's value, else _iValue_default (playbook 0 here).</summary>
    private long Resolve(long multiverseId) =>
        multiverseId > 0 && _data.Row("MultiverseGroupConfig", multiverseId) is { } mg ? GameData.Int(mg, "_iValue_default") : 0;

    /// <summary>
    /// Every _iDropPoolN of a row; each pool slot [group, chance per million, rolls, picks] is rolled "rolls" times
    /// independently (MVP first-strike pool 21001 = [2020, 100%, 30, 1] = 30 x 1000 Minted Coin).
    /// </summary>
    /// <summary>A kill's pools: <paramref name="monsterLevel"/> is the slain monster's level (DropAttenuationConfig).</summary>
    private void RollPools(JsonElement row, GameSession player, List<(int, int)> drops, int monsterLevel)
    {
        foreach (var p in row.EnumerateObject().Where(p => p.Name.StartsWith("_iDropPool", StringComparison.Ordinal)))
            if (GameData.Number(p.Value) is { } poolId) RollPool((long)poolId, player.Progress!, drops, null, monsterLevel);
    }

    private void RollPool(long poolId, GameSession player, List<(int, int)> drops, List<(int Entry, int Count, int Bind)>? bound = null)
        => RollPool(poolId, player.Progress!, drops, bound);

    private void RollPool(long poolId, RoleProgress progress, List<(int, int)> drops, List<(int Entry, int Count, int Bind)>? bound = null,
        int monsterLevel = DropAttenuation.NoMonster)
    {
        if (_data.Row("DropPoolConfig", poolId) is not { } pool) return;
        for (int slot = 1; slot <= 16; slot++)
        {
            if (!pool.TryGetProperty($"_iDropGroup_{slot}", out var g) || g.ValueKind != JsonValueKind.Array || g.GetArrayLength() < 2) continue;
            long group = (long)(GameData.Number(g[0]) ?? 0);
            double chance = GameData.Number(g[1]) ?? 0;
            int rolls = g.GetArrayLength() >= 3 ? Math.Clamp((int)(GameData.Number(g[2]) ?? 1), 1, 1000) : 1;
            int picks = g.GetArrayLength() >= 4 ? Math.Max(1, (int)(GameData.Number(g[3]) ?? 1)) : 1;
            for (int r = 0; r < rolls; r++)
            {
                if (_random.NextDouble() * 1_000_000 >= chance) continue;
                for (int i = 0; i < picks; i++) PickFromGroup(group, progress, drops, bound, monsterLevel);
            }
        }
    }

    /// <summary>
    /// An award id (MultiverseGroupConfig → AwardConfig[0]._iDropPool → DropPoolConfig), rolled for this player;
    /// same item ids merged. Used for MVP rewards.
    /// </summary>
    public List<(int Entry, int Count)> RollAward(long multiverseId, GameSession player)
        => ScaleAwardGoods(RollAwardRaw(multiverseId, player));

    /// <summary>
    /// The unscaled award draw used only while composing a monster-kill outcome. CommitKillAsync applies the
    /// monster outcome rates once after all ordinary and extra kill drops have been merged.
    /// </summary>
    internal List<(int Entry, int Count)> RollAwardRaw(long multiverseId, GameSession player)
    {
        var drops = new List<(int, int)>();
        if (Resolve(multiverseId) is var awardId and > 0 && _data.Row("AwardConfig", awardId) is { ValueKind: JsonValueKind.Array } rows
            && rows.GetArrayLength() > 0 && GameData.Int(rows[0], "_iDropPool") is var pool and > 0)
            RollPool(pool, player, drops);
        return drops.GroupBy(d => d.Item1).Select(g => (g.Key, g.Sum(d => d.Item2))).ToList();
    }

    /// <summary>One DropPoolConfig draw for this player, each chosen DropGroupConfig._iBind retained, same goods merged.</summary>
    public List<(int Entry, int Count, int Bind)> RollPoolBound(long poolId, RoleProgress progress)
    {
        var bound = new List<(int Entry, int Count, int Bind)>();
        RollPool(poolId, progress, new List<(int, int)>(), bound);
        return ScaleAwardGoods(bound.GroupBy(x => (x.Entry, x.Bind))
            .Select(g => (g.Key.Entry, g.Sum(x => x.Count), g.Key.Bind)));
    }

    /// <summary>Stage-box pool draw with each chosen DropGroupConfig._iBind retained (draw rules inferred).</summary>
    public List<(int Entry, int Count, int Bind)> RollAwardBound(long awardId, GameSession player)
        => RollAwardBound(awardId, player.Progress!);

    /// <summary>Roll the same configured award for a committed offline dungeon participant.</summary>
    public List<(int Entry, int Count, int Bind)> RollAwardBound(long awardId, RoleProgress progress)
    {
        var bound = new List<(int Entry, int Count, int Bind)>();
        if (_data.Row("AwardConfig", awardId) is { } raw)
        {
            var row = raw.ValueKind == JsonValueKind.Array && raw.GetArrayLength() > 0 ? raw[0] : raw;
            if (row.ValueKind == JsonValueKind.Object && GameData.Int(row, "_iDropPool") is var pool and > 0)
                RollPool(pool, progress, new List<(int, int)>(), bound);
        }
        return ScaleAwardGoods(bound.GroupBy(x => (x.Entry, x.Bind))
            .Select(g => (g.Key.Entry, g.Sum(x => x.Count), g.Key.Bind)));
    }

    /// <summary>
    /// Auction source awards include fixed _kAward grants as well as _iDropPool draws. Lua_CFG_AwardConfig
    /// GetAwardList reads fixed tuples including binding; guild award arrays carry _kServerOpenDay ranges.
    /// (inferred) Those ranges are inclusive and choose the first matching row. Keep the pool-only overload above
    /// unchanged because DailyActivityService already grants its fixed rewards separately.
    /// </summary>
    public List<(int Entry, int Count, int Bind)> RollAuctionAwardBound(long awardId, RoleProgress progress, int serverOpenDay)
    {
        if (_data.Row("AwardConfig", awardId) is not { } raw)
            throw new InvalidOperationException($"Invalid auction award {awardId}.");
        var candidates = raw.ValueKind == JsonValueKind.Array ? raw.EnumerateArray().ToArray() : [raw];
        var row = candidates.FirstOrDefault(candidate => candidate.ValueKind == JsonValueKind.Object
            && (!candidate.TryGetProperty("_kServerOpenDay", out var days) || days.ValueKind != JsonValueKind.Array
                || days.GetArrayLength() < 2 || serverOpenDay >= (GameData.Number(days[0]) ?? 1)
                    && serverOpenDay <= (GameData.Number(days[1]) ?? int.MaxValue)));
        if (row.ValueKind != JsonValueKind.Object) return [];
        var goods = new List<(int Entry, int Count, int Bind)>();
        if (row.TryGetProperty("_kAward", out var fixedGoods) && fixedGoods.ValueKind == JsonValueKind.Array)
            foreach (var tuple in fixedGoods.EnumerateArray())
            {
                if (tuple.ValueKind != JsonValueKind.Array || tuple.GetArrayLength() < 2)
                    throw new InvalidOperationException($"Invalid fixed auction award {awardId}.");
                goods.Add((checked((int)(GameData.Number(tuple[0]) ?? 0)),
                    checked((int)(GameData.Number(tuple[1]) ?? 0)),
                    tuple.GetArrayLength() > 2 ? checked((int)(GameData.Number(tuple[2]) ?? 0)) : 0));
            }
        if (GameData.Int(row, "_iDropPool") is var pool and > 0)
            RollPool(pool, progress, [], goods);
        return ScaleAwardGoods(goods.GroupBy(g => (g.Entry, g.Bind))
            .Select(g => (g.Key.Entry, g.Sum(x => x.Count), g.Key.Bind)));
    }

    private List<(int Entry, int Count)> ScaleAwardGoods(IEnumerable<(int Entry, int Count)> goods) => goods
        .Select(g => (Original: g, Scaled: (g.Entry, Count: g.Count <= 0 ? g.Count : ScaleAwardQuantity(g.Entry, g.Count))))
        .Where(g => g.Original.Count <= 0 || g.Scaled.Count > 0).Select(g => g.Scaled).ToList();

    private List<(int Entry, int Count, int Bind)> ScaleAwardGoods(IEnumerable<(int Entry, int Count, int Bind)> goods) => goods
        .Select(g => (Original: g, Scaled: (g.Entry, Count: g.Count <= 0 ? g.Count : ScaleAwardQuantity(g.Entry, g.Count), g.Bind)))
        .Where(g => g.Original.Count <= 0 || g.Scaled.Count > 0).Select(g => g.Scaled).ToList();

    internal int ScaleConfiguredAward(int entry, int count) => _rates.ScaleConfiguredAward(entry, count);

    private int ScaleAwardQuantity(int entry, int count) => ScaleConfiguredAward(entry, count);

    private void PickFromGroup(long group, GameSession player, List<(int, int)> drops, List<(int Entry, int Count, int Bind)>? bound = null)
        => PickFromGroup(group, player.Progress!, drops, bound);

    internal void PickFromGroup(long group, RoleProgress progress, List<(int, int)> drops, List<(int Entry, int Count, int Bind)>? bound = null,
        int monsterLevel = DropAttenuation.NoMonster)
    {
        var rows = DropGroupConfig.Get(_data, group);
        if (rows.Count == 0) return;
        var p = progress;
        // D Asia omits _iWeights for an unweighted row; that row still participates with weight one (for example
        // group 105, and the equal-choice item groups). An explicitly serialized zero remains disabled. Groups
        // 3000..3999 are level-banded EXP curves: they also omit _kDropItem, while min/max carry the EXP amount.
        bool expCurve = group is >= 3000 and < 4000;
        long Weight(DropGroupConfig row)
        {
            if (!row.HasDropItem && (!expCurve || !row.HasMinNumber || !row.HasMaxNumber)) return 0;
            return row.WeightOrDefault;
        }
        var candidates = rows.Where(r =>
        {
            if (Weight(r) <= 0 || r.QuestLimit != 0 || r.Activityid != 0) return false;
            long min = r.MinLevelLimit, max = r.MaxLevelLimit;
            if (p.Level < min || max > 0 && p.Level > max) return false;
            return r.JobLimit.Length == 0 || r.JobLimit.Contains(p.Career);
        }).ToList();
        long total = candidates.Sum(Weight);
        if (total <= 0) return;
        long roll = (long)(_random.NextDouble() * total);
        foreach (var r in candidates)
        {
            roll -= Weight(r);
            if (roll >= 0) continue;
            int min = (int)r.MinNumberOrDefault;
            int max = (int)r.MaxNumberOrDefault;
            if (min <= 0 || max < min) return;
            int count = _random.Next(min, max + 1);
            int entry = r.HasDropItem ? (int)r.DropItem : BaseExpItem;
            int amount = _attenuation.Apply(r.Row, count, p.Level, monsterLevel, _random);
            if (amount <= 0) return;
            drops.Add((entry, amount));
            bound?.Add((entry, amount, (int)r.Bind));
            return;
        }
    }

    /// <summary>LvExpCorrectionConfig: player level − monster level → ratio per 10000.</summary>
    private long Correction(int gap)
    {
        _expCorrection ??= _data.Table("LvExpCorrectionConfig").Values
            .GroupBy(r => (int)GameData.Int(r, "_iBaseLvGap"))
            .ToDictionary(g => g.Key, g => GameData.Int(g.First(), "_iLvDifferenceCorrection"));
        return _expCorrection.GetValueOrDefault(gap, 10000);
    }
}
