using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Battle Merit (Battle Glory, function 7205, open at base level 99): the merit points (currency 15, "War Merit
/// Points") a role earns in the public territory PvP, the merit rank they reach, and its rank rewards.
/// <list type="bullet">
/// <item>5001 AUTO_SYNC DBBattleGloryInfo (LC_DAL_BattleGlory): the client reads curRankId, isSpecialRank and the keys of
/// rankRewardReceiveInfo; dayGainScore and sceneInfo are never read.</item>
/// <item>BattleGloryDan: a rank needs the sum of the _iGloryCondition of the ranks below it
/// (Lua_CFG_BattleGloryDan.GetNextGloryTotal): 1000 for rank 2 up to 36000 for rank 9. Rank 10 (_iRankCondition 10)
/// is the champion rank: LC_DAL_BattleGlory._updateRealLevel shows GetFinallyLevelId (the row with an
/// _iRankCondition) while 5001 isSpecialRank is set, curRankId still being the points rank. The server sets it for a
/// role in the top _iRankCondition places of leaderboard 2102 (Micro/RankService, this season's points) that has
/// reached the highest points rank (inferred: the champion stands above rank 9).</item>
/// <item>83400 BattleGloryRankRewardReq {rankReward: {level: 1}} claims a reached rank's _iNormalRewardGroup once
/// (UI_Sys_TerritoryWars_BattleFeatsRewardListWidgetCtrl: ranks 2-9, claimable at curRankId). The client has no
/// handler for the reply; it reads the claim from 5001.</item>
/// </list>
/// Only the client's display is in the client; the rules are the server's (inferred, <see cref="BattleGloryRules"/>).
/// The points come from the maps with a BattleGloryBase row (30301-30319), between WorldHostile players: a kill pays
/// _iKillBase, a hit on the victim in the last 10 s an assist's _iAssistBase, a death _iDeathBase. Each is scaled by
/// BattleGloryKillDif / BattleGloryDeathDif by how many times today the same pair met that way (the 6th pays nothing),
/// kills and assists also by BattleGloryPowerDif of the victim's power minus the earner's. A death pays too (inferred:
/// the record source enum names Kill, Assist and Death as ways points came). PublicParam BattleGloryDailyLimit caps a
/// game day (05:00). The rank follows the season's earned points and never drops. The state is the role's territory
/// RoleState: Glory for this season, GlorySeasons for the past ones (as its honor's Seasons).
/// </summary>
public sealed partial class TerritoryWarsService
{
    public const uint GlorySync = 5001, GloryRankReward = 83400;
    public const int GloryFunction = 7205, GloryCurrency = 15;
    private const long GloryAssistWindowMs = 10_000;

    /// <summary>One season's Battle Merit of a role.</summary>
    public sealed class GloryState
    {
        public string Epoch { get; set; } = "";
        public long Points { get; set; }
        public long Rank { get; set; } = 1;
        public HashSet<long> Claimed { get; set; } = [];
        public int Day { get; set; }
        public long DayGain { get; set; }
        /// <summary>Today's meetings per "kind:other role" (the KillDif / DeathDif count).</summary>
        public Dictionary<string, int> Counts { get; set; } = [];
    }

    internal enum GloryKind { Kill = 1, Assist = 2, Death = 3 }

    /// <summary>Who hit whom last in the public PvP maps (victim, attacker) -> ms, for assists; entries older than the
    /// assist window are dropped as hits come, and a role's are dropped when it leaves the scene.</summary>
    private readonly Dictionary<long, Dictionary<long, long>> _gloryHits = [];
    private BattleGloryRules? _gloryRules;

    /// <summary>The role's place on leaderboard 2102 (RankService.RankOfAsync), 0 unlisted; set by the host.</summary>
    public Func<long, Task<int>>? GloryBoardRank { get; set; }
    private BattleGloryRules GloryRules => _gloryRules ??= new BattleGloryRules(_data);

    private int GloryDay => DateOnly.FromDateTime(DailyActivityService.GameDay(_clock.LocalNow.DateTime)).DayNumber;

    /// <summary>The role's Battle Merit for this season and game day: a past season's goes to GlorySeasons, a new day
    /// starts its counters over. Mutates the given state (a copy about to be saved).</summary>
    private GloryState Glory(RoleState role)
    {
        var glory = role.Glory;
        if (glory.Epoch != Epoch)
        {
            if (glory.Epoch.Length > 0) role.GlorySeasons[glory.Epoch] = glory;
            glory = role.Glory = role.GlorySeasons.Remove(Epoch, out var resumed) ? resumed : new GloryState { Epoch = Epoch };
        }
        if (glory.Day != GloryDay) { glory.Day = GloryDay; glory.DayGain = 0; glory.Counts.Clear(); }
        return glory;
    }

    /// <summary>5001 for a loaded role: read without copying (StateChanged calls it on every bag or EXP change).</summary>
    private async Task SyncGloryAsync(GameSession s)
    {
        if (s.Role is not { } owner || s.Progress == null || s.TerritoryWars is not { } role || !_functions.IsOpen(s, GloryFunction))
            return;
        var glory = SeasonGlory(role, Epoch);
        var info = new DBBattleGloryInfo
        {
            CurRankId = glory?.Rank ?? 1, DayGainScore = glory != null && glory.Day == GloryDay ? glory.DayGain : 0,
        };
        foreach (long rank in glory?.Claimed ?? []) info.RankRewardReceiveInfo[rank] = 1;
        if (glory != null && GloryBoardRank is { } boardRank && GloryRules.ChampionPossible(glory.Rank))
            info.IsSpecialRank = GloryRules.IsChampion(glory.Rank, await boardRank(owner.Id));
        if (ReferenceEquals(s.Role, owner)) await CharacterService.SyncAsync(s, GlorySync, info);
    }

    /// <summary>A role's Battle Merit of one season: the current state, or its archived season.</summary>
    private static GloryState? SeasonGlory(RoleState role, string epoch) =>
        role.Glory.Epoch == epoch ? role.Glory : role.GlorySeasons.GetValueOrDefault(epoch);

    /// <summary>The Battle Merit points a saved role.territory_wars earned in a season (leaderboard 2102).</summary>
    public static long GloryPointsOf(byte[] territoryWars, string epoch) =>
        epoch.Length > 0 && System.Text.Json.JsonSerializer.Deserialize<RoleState>(territoryWars) is { } role
            ? SeasonGlory(role, epoch)?.Points ?? 0 : 0;

    /// <summary>Task target 106610 (TaskObjectives): this season's points of a loaded role, -1 before it loads.</summary>
    public long GloryPoints(GameSession s) => s.TerritoryWars is { } role ? SeasonGlory(role, Epoch)?.Points ?? 0 : -1;

    private BattleGloryBase? GloryMap(GameSession s) => s.InstanceId == 0 && s.LineNumber == 1
        ? BattleGloryBase.Get(_data, s.SceneEntry) : null;

    private static long GloryPower(GameSession s) => PowerService.Total(PowerService.Current(s));

    /// <summary>CombatService.PvpDealtAsync: the hit an assist needs.</summary>
    private Task GloryHitAsync(GameSession source, GameSession target, long amount, bool lethal)
    {
        if (amount > 0 && source.Role is { } attacker && target.Role is { } victim && GloryMap(source) != null)
        {
            long now = MonsterService.NowMs;
            if (!_gloryHits.TryGetValue(victim.Id, out var hits)) _gloryHits[victim.Id] = hits = [];
            foreach (long stale in hits.Where(h => now - h.Value > GloryAssistWindowMs).Select(h => h.Key).ToArray()) hits.Remove(stale);
            hits[attacker.Id] = now;
        }
        return Task.CompletedTask;
    }

    /// <summary>SceneService.Leaving (logouts too): the role's hits, as victim and as attacker.</summary>
    private Task GloryLeavingAsync(GameSession s)
    {
        if (s.Role is not { } role || _gloryHits.Count == 0) return Task.CompletedTask;
        _gloryHits.Remove(role.Id);
        foreach (var (victim, hits) in _gloryHits.ToArray())
            if (hits.Remove(role.Id) && hits.Count == 0) _gloryHits.Remove(victim);
        return Task.CompletedTask;
    }

    /// <summary>CombatService.PlayerKilled: the killer's, the assists' and the victim's merit points.</summary>
    private async Task GloryKilledAsync(GameSession killer, GameSession victim)
    {
        if (killer.Role is not { } k || victim.Role is not { } v) return;
        _gloryHits.Remove(v.Id, out var hits);
        if (GloryMap(killer) is not { } map || !WorldHostile(killer, victim)) return;
        // Everything the awards need is taken before the first await: the earners may move meanwhile.
        long now = MonsterService.NowMs, victimPower = GloryPower(victim);
        var players = _world.PlayersIn(killer.WorldScene).Where(p => p.Role != null).ToDictionary(p => p.Role!.Id);
        var helpers = (hits ?? []).Where(h => h.Key != k.Id && now - h.Value <= GloryAssistWindowMs)
            .Select(h => players.GetValueOrDefault(h.Key)).OfType<GameSession>().Where(p => WorldHostile(p, victim))
            .Select(p => (Session: p, Power: GloryPower(p))).ToArray();
        long killerPower = GloryPower(killer);
        await AwardGloryAsync(killer, GloryKind.Kill, v.Id, map.KillBase, victimPower - killerPower);
        foreach (var (helper, power) in helpers)
            await AwardGloryAsync(helper, GloryKind.Assist, v.Id, map.AssistBase, victimPower - power);
        await AwardGloryAsync(victim, GloryKind.Death, k.Id, map.DeathBase, null);
    }

    /// <summary>One earner's points: scaled, capped for the day, granted with the role state as one receipt. A meeting
    /// that pays nothing (the 6th, a power gap past the table, a full day) only counts, in memory.</summary>
    private async Task AwardGloryAsync(GameSession s, GloryKind kind, long other, long basePoints, long? powerDifference)
    {
        if (s.Role is not { } role) return;
        var attachment = s.Attachment;
        bool paid = false;
        using var reservation = await EnterGateAsync("territory/battle glory", s);
        using var stagedTerritoryState = StageState();
        try
        {
            using var held = await CharacterService.Lock(s);
            if (!ReferenceEquals(s.Role, role) || !ReferenceEquals(s.Attachment, attachment) || s.Progress == null
                || !_functions.IsOpen(s, GloryFunction)) return;
            var next = Copy(s.TerritoryWars ?? LoadRole(role.Id));
            var glory = Glory(next);
            string key = $"{(int)kind}:{other}";
            int count = glory.Counts[key] = glory.Counts.GetValueOrDefault(key) + 1;
            long points = BattleGloryRules.Cap(GloryRules.Points(kind, basePoints, count, powerDifference), glory.DayGain,
                Param("BattleGloryDailyLimit"));
            if (points == 0)
            {
                s.TerritoryWars = next;
                return;
            }
            glory.Points += points; glory.DayGain += points;
            glory.Rank = Math.Max(glory.Rank, GloryRules.RankFor(glory.Points));
            var grants = new List<AwardGrant> { new(GloryCurrency, (int)points, 0) };
            string source = $"battle-glory:{Epoch}:{role.Id}:{kind}:{other}:{Guid.NewGuid():N}";
            if (!await _character.ApplyAwardAsync(s, new AwardPlan(0, 0, grants), persist: tx =>
                {
                    SaveRole(role.Id, next, tx);
                    tx.WithEventEconomy(EventEconomy.ForRole(_data, s, "battle-glory", source, [], grants,
                        new { Kind = kind.ToString(), Other = other, Points = points, glory.Rank }));
                }, stateLockHeld: true, committed: () => s.TerritoryWars = next))
                return;
            paid = true;
            LogGloryEarned(_log, role.Id, points, (int)kind, other, glory.Rank);
        }
        finally { stagedTerritoryState.Dispose(); _gate.Release(); reservation?.Dispose(); }
        if (paid) await SyncGloryAsync(s);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Role {Role} earns {Points} Battle Merit (kind {Kind} of {Other}), rank {Rank}")]
    private static partial void LogGloryEarned(ILogger logger, long role, long points, int kind, long other, long rank);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Battle Merit rank {Rank} reward unresolved for {Role}: {Reason}")]
    private static partial void LogGloryRewardUnresolved(ILogger logger, int rank, long role, string reason);

    /// <summary>83400: reached ranks' rewards, once each; no more keys than there are ranks.</summary>
    private async Task GloryRewardAsync(GameSession s, BattleGloryRankRewardReq req)
    {
        var response = new BattleGloryRankRewardResp();
        if (req.RankReward.Count == 0 || req.RankReward.Count > GloryRules.Ranks.Count) response.Result = ErrorCodes.Parameter;
        else
            using (await CharacterService.Lock(s))
                foreach (int level in req.RankReward.Keys.Order())
                {
                    int result = await ClaimGloryRankAsync(s, level);
                    response.ReceiveResult[level] = result;
                    if (result != 0 && response.Result == 0) response.Result = result;
                }
        await SyncGloryAsync(s);
        await Send(s, GloryRankReward, response);
    }

    private async Task<int> ClaimGloryRankAsync(GameSession s, int level)
    {
        if (s.Role is not { } role || s.Progress == null) return ErrorCodes.Unknown;
        var next = Copy(s.TerritoryWars ?? LoadRole(role.Id));
        var glory = Glory(next);
        int refusal = GloryRules.ClaimError(level, glory, _functions.IsOpen(s, GloryFunction));
        if (refusal != 0) return refusal;
        long group = GloryRules.Ranks.First(d => d.Id == level).NormalRewardGroup;
        glory.Claimed.Add(level);
        AwardPlan plan;
        try { plan = _awards.Resolve(s, group, _clock.PlayBook); }
        catch (AwardResolutionException e)
        {
            LogGloryRewardUnresolved(_log, level, role.Id, e.Message);
            return ErrorCodes.RewardMissing;
        }
        string source = $"battle-glory-rank:{Epoch}:{role.Id}:{level}";
        return await _character.ApplyAwardAsync(s, plan, persist: tx =>
            {
                SaveRole(role.Id, next, tx);
                if (plan.Grants.Any(g => g.Entry is not (100 or 101)))
                    tx.WithEventEconomy(EventEconomy.ForRole(_data, s, "battle-glory-rank", source, [], plan.Grants,
                        new { Rank = level, Award = group, plan.AwardId }));
            }, stateLockHeld: true, committed: () => s.TerritoryWars = next) ? 0 : ErrorCodes.Unknown;
    }
}

/// <summary>
/// Battle Merit's rules over the client tables, apart from any session (inferred, see TerritoryWarsService.BattleGlory):
/// the rank a point total reaches, a meeting's points, the day's cap and a claim's refusal.
/// </summary>
internal sealed class BattleGloryRules(GameData data)
{
    /// <summary>BattleGloryDan in id order.</summary>
    public IReadOnlyList<BattleGloryDan> Ranks { get; } = [.. BattleGloryDan.All(data).OrderBy(r => r.Id)];
    private readonly (long Count, long Coe)[] _kills = [.. BattleGloryKillDif.All(data).Select(r => (r.Count, r.Coe)).OrderBy(r => r.Count)];
    private readonly (long Count, long Coe)[] _deaths = [.. BattleGloryDeathDif.All(data).Select(r => (r.Count, r.Coe)).OrderBy(r => r.Count)];
    private readonly BattleGloryPowerDif[] _power = [.. BattleGloryPowerDif.All(data)];

    /// <summary>The highest rank whose summed conditions of the ranks below the points reach (rank 1 at 0; the
    /// leaderboard rank, _iRankCondition, is not a points rank).</summary>
    public long RankFor(long points)
    {
        long rank = 1, needed = 0;
        foreach (var dan in Ranks.Where(d => d.RankCondition == 0))
        {
            if (dan.Id > 1)
            {
                if (needed > points) break;
                rank = dan.Id;
            }
            needed += dan.GloryCondition;
        }
        return rank;
    }

    /// <summary>The repeat row for the nth meeting today (the last row with _iCount up to n); none listed: unscaled.</summary>
    private static long Repeat((long Count, long Coe)[] rows, int count) =>
        rows.LastOrDefault(r => r.Count <= count, (0, 10000)).Coe;

    /// <summary>BattleGloryPowerDif: the row with the largest threshold the difference reaches on its side; none: unscaled.</summary>
    public long PowerCoe(long difference) => _power
        .Where(r => difference >= 0 ? r.Battle > 0 && r.Battle <= difference : r.Battle < 0 && r.Battle >= difference)
        .OrderByDescending(r => Math.Abs(r.Battle)).Select(r => r.Coe).FirstOrDefault(10000);

    /// <summary>A meeting's points before the day's cap: the base, by the repeat row, kills and assists by the power gap.</summary>
    public long Points(TerritoryWarsService.GloryKind kind, long basePoints, int count, long? powerDifference)
    {
        long points = basePoints * Repeat(kind == TerritoryWarsService.GloryKind.Death ? _deaths : _kills, count) / 10000;
        return powerDifference is { } difference ? points * PowerCoe(difference) / 10000 : points;
    }

    /// <summary>The champion rank: the BattleGloryDan row with an _iRankCondition (10, the top 10 of leaderboard 2102).</summary>
    private BattleGloryDan? Champion => Ranks.Cast<BattleGloryDan?>().FirstOrDefault(d => d!.Value.RankCondition > 0);

    /// <summary>The highest rank points reach (9).</summary>
    private long TopPointsRank => Ranks.Where(d => d.RankCondition == 0).Select(d => d.Id).DefaultIfEmpty(1).Max();

    /// <summary>Whether a points rank can stand for the champion rank at all (inferred: it reached the highest points rank).</summary>
    public bool ChampionPossible(long pointsRank) => Champion != null && pointsRank >= TopPointsRank;

    /// <summary>5001 isSpecialRank: the highest points rank and a place within the champion row's _iRankCondition on
    /// leaderboard 2102 (0 = not listed). Inferred.</summary>
    public bool IsChampion(long pointsRank, int boardPlace) =>
        ChampionPossible(pointsRank) && boardPlace > 0 && boardPlace <= Champion!.Value.RankCondition;

    /// <summary>What the day still takes; a limit of 0 (no PublicParam row) caps nothing.</summary>
    public static long Cap(long points, long dayGain, long limit) =>
        limit > 0 ? Math.Clamp(points, 0, Math.Max(0, limit - dayGain)) : Math.Max(0, points);

    /// <summary>83400's refusal for one rank, or 0: a reward rank (_iNormalRewardGroup, not the leaderboard's), reached
    /// and not yet claimed, with the function open.</summary>
    public int ClaimError(int level, TerritoryWarsService.GloryState glory, bool functionOpen) =>
        !functionOpen ? ErrorCodes.FunctionLocked
        : Ranks.FirstOrDefault(d => d.Id == level) is not { NormalRewardGroup: > 0, RankCondition: 0 } ? ErrorCodes.RewardMissing
        : glory.Claimed.Contains(level) ? ErrorCodes.RewardClaimed
        : glory.Rank < level ? ErrorCodes.NoRankReward : 0;
}
