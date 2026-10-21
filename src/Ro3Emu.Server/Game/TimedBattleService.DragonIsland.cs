using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Battle of Dragon Coffin Island (DungeonConfig 3010303, gameplay 34): a 15V15 score battle with a PvE layer
/// (UITips 290002-290005: "the Faction with the higher Score wins" when the countdown ends; score from Dragonblood
/// Crystals, kills, attacking Fafnir and killing the Demonic Dragon Soul's carrier). The arena script
/// Config/Dungeon/DungeonLua_3010303.lua (not in the exported tables, so its values are listed here) gives:
/// <list type="bullet">
/// <item>DungeonBase.DungeonStageModule: stage 0 preparation 30 s, 1 battle 270 s, 2 end 60 s (PrepareTimeLimit 30000,
/// LevelTimeLimit 300000); the client keeps them from 30401 stageModuleData (LC_DunGeon_DragonIsland:79-137).</item>
/// <item>GroupSpawnPointA 23 / GroupSpawnPointB 26: the two camps' starting points.</item>
/// <item>SpawnBossID 16340001 (Fafnir) at SpawnBossPoint 25 after SpawnBossTime 90000 ms.</item>
/// <item>DragonGlassSpawnTime 30/90/150/210/240 s: the crystal waves; DragonGlassSpawnPointGroupList the scene groups of
/// each wave, DragonGlassGroupSpawnNumList how many per group; DragonGlassIDList 16340002-16340005 the crystals, picked
/// up by their interaction skills (DragonGlassInteractionIDList; SkillConfig _kScriptLogic DragonIsland_interact,
/// _kLogicData [buff, ms]).</item>
/// <item>DragonBreathID 16340007, the Demonic Dragon Soul, at one of DragonBreathSpawnPoint 34-37 after a time in
/// DragonBreathSpawnTimeRange 50-60 s, picked up by skill 16340501 (buff 16340501 until death).</item>
/// </list>
/// Score rows are BattleEventPointConfig with _kGameType 34: _kScore pairs (scope, points), scope 4 the camp and 2 the
/// player (inferred from the kill row [4,20,3,20,2,20] and the assist row without a 4). The camp totals are dungeon
/// var 304 CCampScoreList [camp, score, camp, score]; the soul carrier is var 925 CDragonBreathPlayerID.
/// </summary>
public sealed partial class TimedBattleService
{
    private const int DragonScene = 3010303, DragonBoss = 16340001, DragonSoul = 16340007, DragonSoulSkill = 16340501;
    private const long DragonPrepareSeconds = 30, DragonBossSeconds = 90;
    private static readonly long[] DragonCampPoints = [23, 26];
    private const long DragonBossPoint = 25;
    private static readonly long[] DragonSoulPoints = [34, 35, 36, 37];
    private static readonly long[] DragonGlassSeconds = [30, 90, 150, 210, 240];
    private static readonly int[][] DragonGlassGroups = [[38, 171], [39, 85, 109, 110, 314, 315], [39, 85, 109, 110, 314, 315],
        [39, 85, 109, 110, 314, 315], [39, 85, 109, 110, 314, 315]];
    private static readonly int[][] DragonGlassCounts = [[6, 6], [6, 6, 6, 6, 4, 4], [6, 6, 6, 6, 4, 4], [6, 6, 6, 6, 4, 4], [6, 6, 6, 6, 4, 4]];
    private static readonly int[] DragonGlasses = [16340002, 16340003, 16340004, 16340005];
    // BattleEventPointConfig rows of game type 34.
    private const int ScoreKill = 42010, ScoreBossFirst = 42017, ScoreBossLast = 42018, ScoreBossDamage = 42019, ScoreSoul = 42020;
    // DungeonVariableConfig 304 CCampScoreList, 925 CDragonBreathPlayerID.
    private const int CampScoreVar = 304, SoulCarrierVar = 925;

    private sealed class DragonRun
    {
        public int Stage;
        public readonly Dictionary<int, long> CampScore = new() { [1] = 0, [2] = 0 };
        public readonly Dictionary<GameSession, long> Score = new();
        public readonly Dictionary<GameSession, long> BossDamage = new();
        public MonsterUnit? Boss;
        public bool BossSpawned, BossStruck;
        public int GlassWaves;
        public readonly Dictionary<long, (MonsterUnit Unit, int Skill)> Pickups = new();
        public long SoulAt;
        public bool SoulSpawned;
        public GameSession? SoulCarrier;
    }

    private static bool IsDragon(Match match) => match.Dragon != null;

    /// <summary>Camp (faction) 1 Blue / 2 Red (enum_LeisureTeamCamp): whole squads alternate between them (inferred).</summary>
    private static int DragonCamp(int index, int teamSize) => index / teamSize % 2 + 1;

    private DragonRun NewDragonRun(Match match) => new() { SoulAt = match.Open + 50 + Random.Shared.Next(0, 11) };

    private int DragonStage(Match match, long now) => now < match.Open + DragonPrepareSeconds ? 0 : now < match.Deadline ? 1 : 2;

    /// <summary>The 30401 every load and every change gets: stages and the camp score (and soul carrier) vars.</summary>
    private DungeonBaseInfoNotify DragonInfo(Match match)
    {
        var run = match.Dragon!;
        var init = new DungeonInitData();
        var dragon = new DragonIslandBattleInitData { MatchEntryId = DragonEntry };
        foreach (var player in match.Players)
            dragon.Players[match.Participants[player].Id] = new DragonIslandBattlePlayerSlot
            { PlayerId = match.Participants[player].Id, Faction = match.Faction[player] };
        init.DragonIslandBattleInitData = dragon;
        long prepareEnd = (match.Open + DragonPrepareSeconds) * 1000, end = match.Deadline * 1000;
        var info = new DungeonBaseInfoNotify
        {
            DungeonEntry = match.Room!.SceneEntry, DungeonId = match.Room.Id, Stage = run.Stage, InitData = init,
            ChangeType = (DungeonBaseInfoNotify.Types.eChangeType)2,
            StageModuleData = new DungeonStageModuleData
            {
                MapState = run.Stage == 2 ? 2 : 1, Stage = run.Stage,
                MapStateStartTime = match.Open * 1000, MapStateEndTime = end,
                StageStartTime = run.Stage == 0 ? match.Open * 1000 : prepareEnd,
                StageEndTime = run.Stage == 0 ? prepareEnd : end,
                NextStageStartTime = run.Stage == 0 ? prepareEnd : 0, NextStageEndTime = run.Stage == 0 ? end : 0
            },
        };
        var camps = new DungeonVar { VarId = CampScoreVar };
        camps.Vi64.Add([1, run.CampScore[1], 2, run.CampScore[2]]);
        info.Vars.Add(camps);
        var soul = new DungeonVar { VarId = SoulCarrierVar };
        soul.Vi64.Add(run.SoulCarrier is { } carrier && match.Participants.TryGetValue(carrier, out var p) ? p.Id : 0);
        info.Vars.Add(soul);
        return info;
    }

    private Task BroadcastDragonAsync(Match match)
    {
        var info = DragonInfo(match);
        return RoleFanout.EachAsync(match.Players.Where(p => match.Loaded.Contains(p) && _active.GetValueOrDefault(p) == match
            && p.InstanceId == match.Room?.Id), p => p.SendGameAsync(DungeonService.BaseInfo, info));
    }

    private async Task DragonLoadedAsync(Match match, GameSession session)
    {
        await session.SendGameAsync(DungeonService.BaseInfo, DragonInfo(match));
        // Each camp starts at its own point (GroupSpawnPointA/B).
        if (_worldData.Map(DragonScene)?.Points.TryGetValue(DragonCampPoints[match.Faction[session] - 1], out var start) == true)
            await _scene.TeleportInMapAsync(session, start);
    }

    /// <summary>Adds a BattleEventPointConfig row's points: scope 4 to the player's camp, scope 2 to the player.</summary>
    private void DragonScore(Match match, GameSession player, int row, long times = 1)
    {
        var run = match.Dragon!;
        if (times <= 0 || _data.Row("BattleEventPointConfig", row) is not { } config || !config.TryGetProperty("_kScore", out var pairs)
            || pairs.ValueKind != JsonValueKind.Array || !match.Faction.TryGetValue(player, out int camp)) return;
        var values = pairs.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray();
        for (int i = 0; i + 1 < values.Length; i += 2)
            if (values[i] == 4) run.CampScore[camp] += values[i + 1] * times;
            else if (values[i] == 2) run.Score[player] = run.Score.GetValueOrDefault(player) + values[i + 1] * times;
    }

    /// <summary>A kill in the battle: the kill's points; killing the soul's carrier seizes the soul (42020).</summary>
    private async Task DragonKillAsync(Match match, GameSession killer, GameSession victim)
    {
        var run = match.Dragon!;
        match.Kills[killer] = match.Kills.GetValueOrDefault(killer) + 1;
        DragonScore(match, killer, ScoreKill);
        if (run.SoulCarrier == victim) await GiveSoulAsync(match, killer);
        await BroadcastDragonAsync(match);
    }

    private async Task GiveSoulAsync(Match match, GameSession carrier)
    {
        var run = match.Dragon!;
        run.SoulCarrier = carrier;
        DragonScore(match, carrier, ScoreSoul);
        // Skill 16340501 _kLogicData [16340501, -1]: the buff lasts until death (BuffConfig _iDeadClear 1).
        int left = (int)Math.Max(1000, (match.Deadline - _time.GetUtcNow().ToUnixTimeSeconds()) * 1000);
        await _buffs.AddAsync(carrier, DragonSoulSkill, left, match.Room!.Id);
    }

    /// <summary>Damage on Fafnir: the first strike (42017) and every 1/DragonIslandBossPoints of its HP (42019, inferred step).</summary>
    private void DragonDealt(GameSession s, MonsterUnit monster, long damage)
    {
        if (!_active.TryGetValue(s, out var match) || match.Dragon is not { } run || run.Boss != monster || match.Settled || damage <= 0) return;
        bool scored = !run.BossStruck;
        if (!run.BossStruck) { run.BossStruck = true; DragonScore(match, s, ScoreBossFirst); }
        long step = Math.Max(1, monster.Template.MaxHp / Math.Max(1, PublicValue("DragonIslandBossPoints")));
        long before = run.BossDamage.GetValueOrDefault(s);
        run.BossDamage[s] = before + damage;
        long steps = (before + damage) / step - before / step;
        DragonScore(match, s, ScoreBossDamage, steps);
        if (scored || steps > 0) _loop.PostRuntime(() => BroadcastDragonAsync(match)); // only when a score changed
    }

    private async Task DragonBossKilledAsync(GameSession s, MonsterUnit monster)
    {
        if (!_active.TryGetValue(s, out var match) || match.Dragon is not { } run || run.Boss != monster || match.Settled) return;
        run.Boss = null;
        DragonScore(match, s, ScoreBossLast);
        await BroadcastDragonAsync(match);
    }

    /// <summary>
    /// The interaction skills of the crystals and the soul (SkillConfig 1634xx01, DragonIsland_interact): the cast on a
    /// pickup of the caster's match takes it: its buff (_kLogicData [buff, ms]) and its score row (BattleEventPointConfig
    /// type 149/152 naming the skill). Any other cast goes on to combat.
    /// </summary>
    public async Task<bool> TryDragonPickupAsync(GameSession s, CastSkillReq req)
    {
        if (!_active.TryGetValue(s, out var match) || match.Dragon is not { } run || !run.Pickups.TryGetValue(req.TargetId, out var pickup)
            || pickup.Skill != req.SkillId) return false;
        int result = match.Settled || run.Stage != 1 || s.Dead || s.InstanceId != match.Room?.Id ? ErrorCodes.FeatureUnavailable
            : MonsterService.Distance(pickup.Unit, s) > 6f ? ErrorCodes.NpcTooFar : 0;
        await s.SendGameAsync(CombatService.UseSkill, new CastSkillRsp { Result = result });
        if (result != 0) return true;
        run.Pickups.Remove(req.TargetId);
        await _instances.RemoveMonsterAsync(match.Room!, pickup.Unit);
        if (req.SkillId == DragonSoulSkill) await GiveSoulAsync(match, s);
        else
        {
            if (_data.Row("SkillConfig", req.SkillId) is { } skill && skill.TryGetProperty("_kLogicData", out var logic)
                && logic.ValueKind == JsonValueKind.Array && logic.GetArrayLength() >= 2)
                await _buffs.AddAsync(s, (int)(GameData.Number(logic[0]) ?? 0), (int)(GameData.Number(logic[1]) ?? 0), match.Room!.Id);
            var row = _data.Table("BattleEventPointConfig").Values.FirstOrDefault(r => GameData.Int(r, "_iType") == 149
                && r.TryGetProperty("_kData", out var d) && d.ValueKind == JsonValueKind.Array && d.GetArrayLength() > 0
                && GameData.Number(d[0]) == req.SkillId && JsonSerializer.Serialize(r.GetProperty("_kGameType")).Contains("34"));
            if (row.ValueKind == JsonValueKind.Object) DragonScore(match, s, (int)GameData.Int(row, "_iID"));
        }
        await BroadcastDragonAsync(match);
        return true;
    }

    /// <summary>Once a second: the stage changes, Fafnir, the crystal waves and the soul on their configured times.</summary>
    private async Task DragonTickAsync(Match match)
    {
        if (match.Settled || match.Dragon is not { } run || match.Room is not { Closed: false } room) return;
        long now = _time.GetUtcNow().ToUnixTimeSeconds(), elapsed = now - match.Open;
        bool changed = false;
        int stage = DragonStage(match, now);
        if (stage != run.Stage && stage < 2) { run.Stage = stage; changed = true; }
        var map = _worldData.Map(DragonScene);
        if (run.Stage == 1 && !run.BossSpawned && elapsed >= DragonBossSeconds)
        {
            run.BossSpawned = true;
            if (map?.Points.TryGetValue(DragonBossPoint, out var at) == true)
                run.Boss = await _instances.SpawnAsync(room, DragonBoss, map.Grounded(at));
        }
        while (run.GlassWaves < DragonGlassSeconds.Length && elapsed >= DragonGlassSeconds[run.GlassWaves])
        {
            int wave = run.GlassWaves++;
            for (int g = 0; g < DragonGlassGroups[wave].Length && map != null; g++)
            {
                if (!map.DungeonGroups.TryGetValue(DragonGlassGroups[wave][g], out var points) || points.Count == 0) continue;
                // (inferred) A uniform crystal kind at distinct random points of the group.
                foreach (var point in points.OrderBy(_ => Random.Shared.Next()).Take(DragonGlassCounts[wave][g]))
                {
                    int npc = DragonGlasses[Random.Shared.Next(DragonGlasses.Length)];
                    if (await _instances.SpawnAsync(room, npc, map.Grounded(new MapPoint(point.X, point.Y, point.Z, point.Face))) is { } unit
                        && InteractionOf(npc) is int skill) run.Pickups[unit.Uid] = (unit, skill);
                }
            }
        }
        if (!run.SoulSpawned && now >= run.SoulAt && map != null)
        {
            run.SoulSpawned = true;
            long pointId = DragonSoulPoints[Random.Shared.Next(DragonSoulPoints.Length)];
            if (map.Points.TryGetValue(pointId, out var at) && await _instances.SpawnAsync(room, DragonSoul, map.Grounded(at)) is { } soul)
                run.Pickups[soul.Uid] = (soul, DragonSoulSkill);
        }
        if (changed) await BroadcastDragonAsync(match);
    }

    /// <summary>NPCConfig _iInteractID[0]: the crystal's interaction, whose id is also its interaction skill's.</summary>
    private int? InteractionOf(int npc) => _data.Row("NPCConfig", npc) is { } row && row.TryGetProperty("_iInteractID", out var ids)
        && ids.ValueKind == JsonValueKind.Array && ids.GetArrayLength() > 0 ? (int)(GameData.Number(ids[0]) ?? 0) : null;

    /// <summary>The camp with the higher score; 0 on a tie (no winner, inferred).</summary>
    private static int DragonLeader(Match match) => match.Dragon is not { } run || run.CampScore[1] == run.CampScore[2] ? 0
        : run.CampScore[1] > run.CampScore[2] ? 1 : 2;
}
