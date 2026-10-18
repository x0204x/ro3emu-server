using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;
using Ro3Emu.Server.Game.Ownership;
using static Ro3Emu.Server.Game.ChickenRules;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Survivor Battle ("Chicken", match entry 2, scene 2240101) inside the match, after the queue/ready/room above:
/// the stage machine (30401 stageModuleData), the per-match round state (5036 ROUNDDUNGEONINFO), careers (82622) and
/// landing (82623/82630). Items, the poison zone and death/revive live in the other TimedBattleService.Chicken* files.
/// Parameters in <see cref="ChickenRules"/>.
/// A match is transient: none of this is persisted (only the settlement in TimedBattleService.Settlement is).
/// </summary>
public sealed partial class TimedBattleService
{
    public const uint RoundSync = 5036, SelectCareer = 82622, SelectPosition = 82623, PositionNotify = 82630;
    private const int AttrChickenPlayerId = 1200, AttrChickenPlayerName = 1201;

    /// <summary>One match's in-scene state.</summary>
    private sealed class ChickenRun
    {
        public int Stage = StageNone;
        public long StageStartMs, StageEndMs, BattleStartMs, BattleEndMs;
        public required Random Rng;
        public Dictionary<GameSession, ChickenPlayer> Players { get; } = new(ReferenceEqualityComparer.Instance);
        public List<ChickenChestUnit> Chests { get; } = new();
        public List<ZoneEvent> Zone { get; } = new();
        public List<ZoneShrink> Shrinks { get; } = new();
        public int ZonePublished = -1;
        public List<int> EliminatedFactions { get; } = new();
        public List<ReviveChannel> Channels { get; } = new();
        public int PickOrder;
        public long NextGuid = 1;
        public long LastTickMs;
        public HashSet<long> TagDropped { get; } = new();
    }

    /// <summary>One participant's match state (the 5036 data and the settlement statistics).</summary>
    private sealed class ChickenPlayer
    {
        public required int TeamIndex;
        public int Career;
        public int LandX = UnchosenX, LandZ = UnchosenZ, PickOrder;
        public bool LandChosen;
        public Dictionary<long, ChickenItem> Pack { get; } = new();
        public SortedDictionary<int, long> SkillBar { get; } = new();
        public SortedDictionary<int, long> EquipBar { get; } = new();
        public SortedDictionary<int, int> ItemBar { get; } = new();
        /// <summary>Carried ID tags: dead teammate id -> card guid in the pack (inferred key/value meaning).</summary>
        public Dictionary<long, long> IdentityCards { get; } = new();
        public Dictionary<int, long> ItemCd { get; } = new();
        public RoundRoleStatus State = RoundRoleStatus.InWar;
        public int FreeRelifeLeft = FreeRelifeCount;
        /// <summary>An open 82663 revive map: the player comes back at a point of its choice before this time.</summary>
        public long RelifeUntilMs;
        public float RelifeX, RelifeZ;
        public int Resurrections, TagDrops;
        public bool GaveUp;
        public long Damage, Injury, Cure, Assists, Deaths, Help, Rescued, Revived;
        public long EliminatedMs;
        public Dictionary<GameSession, long> LastHitBy { get; } = new(ReferenceEqualityComparer.Instance);
        public List<(long PerTick, long UntilMs)> Heals { get; } = new();
    }

    private ChickenRun NewRun(Match match)
    {
        var run = new ChickenRun { Rng = new Random(unchecked((int)(match.Room!.Id ^ (match.Room.Id >> 32)))) };
        foreach (var team in match.Players.GroupBy(p => match.Faction[p]))
        {
            int index = 0;
            foreach (var player in team) run.Players[player] = new ChickenPlayer { TeamIndex = index++ };
        }
        long now = NowMs();
        // (inferred) LoginWaitingTime folds into the first stage: the room opens on ChooseCareer while clients load.
        SetStage(run, StageChooseCareer, now, now + LoginWaitingMs + StageMs[StageChooseCareer]);
        return run;
    }

    private long NowMs() => time.GetUtcNow().ToUnixTimeMilliseconds();

    private static void SetStage(ChickenRun run, int stage, long start, long end)
    { run.Stage = stage; run.StageStartMs = start; run.StageEndMs = end; }

    private (Match Match, ChickenRun Run, ChickenPlayer Me)? Chicken(GameSession session) =>
        active.TryGetValue(session, out var match) && match.Chicken is { } run && !match.Settled
            && match.Room is { Closed: false } room && session.InstanceId == room.Id
            && run.Players.TryGetValue(session, out var me) ? (match, run, me) : null;

    private IEnumerable<GameSession> Team(Match match, int faction) =>
        match.Players.Where(p => match.Faction[p] == faction);

    private IEnumerable<GameSession> InRoom(Match match) => match.Players
        .Where(p => active.GetValueOrDefault(p) == match && match.Loaded.Contains(p) && match.Room is { } room && p.InstanceId == room.Id);

    // ---- careers and the match build ----

    private JsonElementRow? CareerRow(int career) =>
        data.Row("EatChickenGameCareer", career) is { } row && GameData.Int(row, "_iIsShow") == 1 ? new(row) : null;

    /// <summary>Wrapper so a missing row is a null reference, not a default JsonElement.</summary>
    private sealed record JsonElementRow(System.Text.Json.JsonElement Row);

    private System.Text.Json.JsonElement? ItemRow(int entry) => data.Row("EatChickenItemConfig", entry);

    /// <summary>
    /// CharacterService.MatchAttributes: from ShowCareer on, a participant fights with its match career
    /// (EatChickenGameCareer._iJobAttr, identical for all twelve careers: "fair competition for 60 players",
    /// DailyActivityConfig 114) plus its worn match equipment (EatChickenItemConfig._kAttribute), not its own build.
    /// </summary>
    private IReadOnlyList<(long Id, double Value)>? MatchAttributes(GameSession session)
    {
        if (!active.TryGetValue(session, out var match) || match.Chicken is not { } run || run.Stage < StageShowCareer
            || !run.Players.TryGetValue(session, out var me) || me.Career == 0 || CareerRow(me.Career) is not { } career) return null;
        var list = GameData.Pairs(career.Row, "_iJobAttr").ToList();
        foreach (long guid in me.EquipBar.Values)
            if (me.Pack.TryGetValue(guid, out var item) && ItemRow(item.Entry) is { } row)
                list.AddRange(GameData.Pairs(row, "_kAttribute"));
        return list;
    }

    /// <summary>
    /// CombatService.MatchSkillRule: a career's normal, talent and default skills (EatChickenGameCareer), its
    /// DungeonLua Chicken_CareerSkill, and the skills of skill items on the bar (EatChickenItemConfig._iSkill). The
    /// character's own normal attack stays usable (inferred fallback: which attack the client binds in the match is
    /// not established). Anything else of the job tree is refused in the match.
    /// </summary>
    private bool? MatchSkillRule(GameSession session, int skillId)
    {
        if (!active.TryGetValue(session, out var match) || match.Chicken is not { } run || run.Stage < StageShowCareer
            || !run.Players.TryGetValue(session, out var me) || me.Career == 0 || CareerRow(me.Career) is not { } career) return null;
        if (skillId == skills.NormalAttack(session)) return true;
        foreach (var field in new[] { "_iNormalSkillId", "_iTalentSkillId", "_iDefaultSkillId" })
            if (GameData.Int(career.Row, field) == skillId) return true;
        if (CareerSkill.GetValueOrDefault(me.Career) == skillId) return true;
        foreach (long guid in me.SkillBar.Values)
            if (me.Pack.TryGetValue(guid, out var item) && ItemRow(item.Entry) is { } row && GameData.Int(row, "_iSkill") == skillId)
                return true;
        return false;
    }

    /// <summary>(inferred) The career of a player who picked none in ChooseCareer: the first shown career no teammate took.</summary>
    private int DefaultCareer(Match match, ChickenRun run, GameSession player)
    {
        var taken = Team(match, match.Faction[player]).Select(p => run.Players[p].Career).ToHashSet();
        var shown = data.Table("EatChickenGameCareer").Values.Where(row => GameData.Int(row, "_iIsShow") == 1)
            .Select(row => (int)GameData.Int(row, "_iID")).Order().ToArray();
        return shown.FirstOrDefault(id => !taken.Contains(id), shown.FirstOrDefault());
    }

    /// <summary>82622: UI_Sys_Chicken_SchoolPanelCtrl.lua:816 -> LC_DAL_Chicken.lua:496-520, response :522-535.</summary>
    public async Task SelectCareerAsync(GameSession session, SelectRoleCareerReq req)
    {
        int result = ErrorCodes.Parameter;
        if (Chicken(session) is (var match, var run, var me))
            await RunMatchTransitionAsync(match, "chicken career", async () =>
            {
                if (match.Settled || run.Stage != StageChooseCareer) { result = ErrorCodes.FeatureUnavailable; return; }
                if (CareerRow(req.Career) == null) return;
                me.Career = req.Career;
                result = 0;
                await SyncTeamAsync(match, match.Faction[session]);
            });
        await session.SendGameAsync(SelectCareer, new SelectRoleCareerResp { Result = result, Career = result == 0 ? req.Career : 0 });
    }

    /// <summary>
    /// 82623: UI_Sys_Chicken_MapPanelModel.lua:211-225 floors the picked world x/z (no x100). Then the team's picks go
    /// out whole in 82630 (LC_DAL_Chicken.lua:1009-1057 clears the map before copying).
    /// </summary>
    public async Task SelectPositionAsync(GameSession session, SelectRolePositionReq req)
    {
        int result = ErrorCodes.Parameter;
        if (Chicken(session) is (var match, var run, var me))
            await RunMatchTransitionAsync(match, "chicken landing", async () =>
            {
                if (match.Settled || run.Stage != StageChoosePos) { result = ErrorCodes.FeatureUnavailable; return; }
                if (req.PositionX is < MapMin or > MapMax || req.PositionY is < MapMin or > MapMax
                    || req.PositionX == UnchosenX && req.PositionY == UnchosenZ) return;
                me.LandX = req.PositionX; me.LandZ = req.PositionY; me.LandChosen = true; me.PickOrder = ++run.PickOrder;
                result = 0;
                await session.SendGameAsync(SelectPosition, new SelectRolePositionResp
                { Result = 0, PositionX = req.PositionX, PositionY = req.PositionY });
                await SendTeamPicksAsync(match, run, match.Faction[session]);
            });
        if (result != 0) await session.SendGameAsync(SelectPosition, new SelectRolePositionResp { Result = result });
    }

    /// <summary>82630 to each team member: the team's chosen landings (enemies' picks are not shown, inferred).</summary>
    private async Task SendTeamPicksAsync(Match match, ChickenRun run, int faction)
    {
        var team = Team(match, faction).ToArray();
        var notify = new SelectRolePositionNotify();
        foreach (var mate in team)
        {
            var info = run.Players[mate];
            if (!info.LandChosen) continue;
            notify.Infos[match.Participants[mate].Id] = new SelectRolePositionNotify.Types.SelectRolePositionInfo
            {
                RoleId = match.Participants[mate].Id, PositionX = info.LandX, PositionY = info.LandZ, OpId = info.PickOrder,
                // (inferred) playerNum: how many of the team land on this point.
                PlayerNum = team.Count(other => run.Players[other] is { LandChosen: true } o && o.LandX == info.LandX && o.LandZ == info.LandZ),
            };
        }
        // An empty infos map decodes as nil and the handler pairs() it unguarded: nothing is sent before a first pick.
        if (notify.Infos.Count == 0) return;
        await RoleFanout.EachAsync(team.Where(p => InRoom(match).Contains(p)), p => p.SendGameAsync(PositionNotify, notify));
    }

    // ---- 5036 ----

    /// <summary>
    /// The 5036 RoundDungeonInfo of one participant (MG_Dungeon.proto:303-328; full handler LC_DAL_Chicken.lua:1723-2120).
    /// The bars and the pack are paired unguarded, and lua-protobuf decodes an empty map as nil, so each always holds a
    /// key 0: InitChickenBar drops key 0 (:2525-2546) and a pack entry with stack 0 is skipped by RefreshBackPack
    /// (:2350-2523). Every ItemBaseInfo carries itemInfos (ParseChickenItemInfo indexes it, LC_ItemProtoParse.lua:910-955).
    /// teamIndex is sent 0-based (ParseTeamMemberInfo adds 1, :3192-3250); battleAttr is paired unguarded.
    /// </summary>
    private RoundDungeonInfo Round(Match match, ChickenRun run, GameSession player)
    {
        var me = run.Players[player];
        long now = NowMs();
        var info = new RoundDungeonInfo
        {
            TeamId = match.Faction[player], CareerId = me.Career, FreeRelifeCountLeft = me.FreeRelifeLeft,
            PackWeight = Weight(me),
        };
        foreach (var mate in Team(match, match.Faction[player]))
        {
            var state = run.Players[mate];
            long id = match.Participants[mate].Id;
            var member = new RoundTeamMemberInfo
            {
                MemberGuid = id, Career = state.Career, State = state.State, TeamIndex = state.TeamIndex, InAreaId = AreaMain,
                Pos = mate.Position?.Clone() ?? new Vec3(),
            };
            if (mate.Progress != null && mate.Role != null)
            {
                var stats = character.Stats(mate);
                member.BattleAttr[101] = stats[101];
                member.BattleAttr[2000] = mate.Dead ? 0 : stats[2000];
            }
            else member.BattleAttr[101] = 0;
            info.TeamMemberInfo[id] = member;
        }
        foreach (var (entry, until) in me.ItemCd) if (until > now) info.ItemCdExpireTime[entry] = until;
        var dungeon = new ChickenDungeonInfo { ChickenOpenTime = run.BattleStartMs };
        dungeon.Skills[0] = 0; dungeon.Items[0] = 0; dungeon.Equips[0] = 0;
        foreach (var (slot, guid) in me.SkillBar) dungeon.Skills[slot] = guid;
        foreach (var (slot, entry) in me.ItemBar) dungeon.Items[slot] = entry;
        foreach (var (slot, guid) in me.EquipBar) dungeon.Equips[slot] = guid;
        foreach (var (owner, guid) in me.IdentityCards) dungeon.IdentityCards[owner] = guid;
        info.ExtendInfo = new RoundDungeonExtendInfo { ChickenDungeonInfo = dungeon };
        info.PackInfos[0] = Wire(new ChickenItem(0, 0, 0));
        foreach (var item in me.Pack.Values) info.PackInfos[item.Guid] = Wire(item);
        return info;
    }

    private static ItemBaseInfo Wire(ChickenItem item, int? stack = null)
    {
        var info = new ItemBaseInfo { ItemGuid = item.Guid, ItemEntry = item.Entry, Stack = stack ?? item.Stack,
            ItemBindInfo = new ItemBindInfo() };
        // ItemAttrTypeChickenPlayerId / Name (MG_Pack.proto:145-146): the owner of an ID tag; 0 for other items.
        info.ItemInfos[AttrChickenPlayerId] = new ItemAttrValue { LongVal = item.OwnerId };
        if (item.OwnerName.Length > 0)
            info.ItemInfos[AttrChickenPlayerName] = new ItemAttrValue { StrVal = item.OwnerName };
        return info;
    }

    private Task SyncRoundAsync(Match match, ChickenRun run, GameSession player) =>
        player.Role == null || !InRoom(match).Contains(player) ? Task.CompletedTask
            : CharacterService.SyncAsync(player, RoundSync, Round(match, run, player));

    private Task SyncTeamAsync(Match match, int faction) => match.Chicken is { } run
        ? RoleFanout.EachAsync(Team(match, faction), player => SyncRoundAsync(match, run, player)) : Task.CompletedTask;

    // ---- 30401 / 30404 ----

    private DungeonBaseInfoNotify ChickenBaseInfo(Match match, ChickenRun run)
    {
        var init = new ChickenInitData { TeamCount = match.TeamCount, TeamMemberCount = match.TeamSize, MatchEntry = ChickenEntry };
        foreach (var player in match.Players) init.PlayerToTeamId[match.Participants[player].Id] = match.Faction[player];
        int stage = Math.Max(StageChooseCareer, run.Stage);
        var info = new DungeonBaseInfoNotify
        {
            DungeonEntry = match.Room!.SceneEntry, DungeonId = match.Room.Id, Stage = stage,
            InitData = new DungeonInitData { ChickenInitData = init },
            ChangeType = (DungeonBaseInfoNotify.Types.eChangeType)2,
            // LC_DunGeon_Base.lua:1492-1539 divides all four times by 1000 unguarded; L.OnDungeonStageInfoChanged
            // (LC_DunGeon_Chicken.lua:639-671) reads stage/stageEndTime.
            StageModuleData = new DungeonStageModuleData
            {
                MapState = stage, Stage = stage, StageStartTime = run.StageStartMs, StageEndTime = run.StageEndMs,
                MapStateStartTime = run.StageStartMs, MapStateEndTime = run.StageEndMs,
            },
        };
        info.Vars.AddRange(HudVars(match, run));
        info.Vars.AddRange(ZoneVars(run));
        return info;
    }

    private static DungeonVar Var(int id, params long[] values)
    { var variable = new DungeonVar { VarId = id }; variable.Vi64.Add(values); return variable; }

    /// <summary>LC_DunGeon_VarDefine.lua:448-482, read by UI_Sys_Chicken_TopWidgetCtrl (survivors, teams, free revive).</summary>
    private IEnumerable<DungeonVar> HudVars(Match match, ChickenRun run)
    {
        var alive = match.Players.Where(p => !match.Absent.Contains(p) && !match.Fallen.Contains(p)).ToArray();
        yield return Var(VarSurvivalPlayers, alive.Length);
        yield return Var(VarSurvivalTeams, alive.Select(p => match.Faction[p]).Distinct().Count());
        yield return Var(VarTotalPlayers, match.Players.Length);
        yield return Var(VarTotalTeams, match.TeamCount);
        // TimerManager.GetSystemTime is in seconds (UI_Sys_Chicken_TopWidgetCtrl.lua:455-480).
        if (run.BattleStartMs > 0) yield return Var(VarFreeRevivalTime, (run.BattleStartMs + FirstResurrectionMs) / 1000);
    }

    private async Task BroadcastVarsAsync(Match match, IEnumerable<DungeonVar> vars)
    {
        var notify = new DungeonEventNotify { DungeonEntry = match.Room!.SceneEntry, EventType = (DungeonEventType)4 };
        notify.Vars.AddRange(vars);
        if (notify.Vars.Count == 0) return;
        await RoleFanout.EachAsync(InRoom(match), p => p.SendGameAsync(DungeonService.Event, notify));
    }

    private Task BroadcastHudAsync(Match match) => match.Chicken is { } run
        ? BroadcastVarsAsync(match, HudVars(match, run)) : Task.CompletedTask;

    private Task BroadcastBaseInfoAsync(Match match, ChickenRun run)
    {
        var info = ChickenBaseInfo(match, run);
        return RoleFanout.EachAsync(InRoom(match), p => p.SendGameAsync(DungeonService.BaseInfo, info));
    }

    /// <summary>Scene load (also a reload): the whole 5036 first (it closes the ready panel and resets the match state,
    /// LC_DAL_Chicken.lua:1723-1761), then 30401 with the current stage.</summary>
    private async Task ChickenLoadedAsync(Match match, GameSession session)
    {
        var run = match.Chicken!;
        session.Synced.Remove(RoundSync);
        if (run.Stage >= StageShowCareer)
        {
            // The career build (a reload keeps the current HP).
            character.Refresh(session);
            await character.RefreshStatsAsync(session);
        }
        await SyncRoundAsync(match, run, session);
        await session.SendGameAsync(DungeonService.BaseInfo, ChickenBaseInfo(match, run));
        if (run.Stage == StageChoosePos) await SendTeamPicksAsync(match, run, match.Faction[session]);
        await SyncTeamAsync(match, match.Faction[session]);
    }

    // ---- the stage machine ----

    /// <summary>Once per second for every live match (Tick): the stage clock, then the battle's own work.</summary>
    private async Task ChickenTickAsync(Match match)
    {
        var damage = new List<(GameSession Player, long Damage)>();
        bool settle = false;
        await RunMatchTransitionAsync(match, "chicken tick", async () =>
        {
            if (match.Settled || match.Chicken is not { } run || match.Room == null) return;
            long now = NowMs();
            if (run.Stage < StageStartGame && now >= run.StageEndMs) await AdvanceStageAsync(match, run, now);
            if (run.Stage != StageStartGame || match.Settled) return;
            await PublishZoneAsync(match, run, now);
            await TickLifeAsync(match, run, now);
            if (now - run.LastTickMs >= PoisonTickMs)
            {
                run.LastTickMs = now;
                damage.AddRange(PoisonVictims(match, run, now));
                await TickHealsAsync(match, run, now);
            }
            settle = RemainingFactions(match).Count <= 1;
        });
        // Damage outside the match scope, as a monster hit runs: death owners reserve what they need themselves.
        foreach (var (player, amount) in damage)
        {
            if (Chicken(player) is not (_, _, var me) || player.Dead || me.State != RoundRoleStatus.InWar) continue;
            await buffs.AddAsync(player, PoisonBuff(), 1500, 0);
            await combat.EnvironmentDamageAsync(player, amount, () => Chicken(player) != null);
        }
        if (settle) await SettleMatchAsync(match);
    }

    private async Task AdvanceStageAsync(Match match, ChickenRun run, long now)
    {
        if (run.Stage == StageChooseCareer)
        {
            // Anyone still loading when the career pick closes is out of this match (inferred; the 60 s entry abort
            // stays for the two-player Dragon Coffin rooms).
            foreach (var player in match.Players.Where(p => !match.Loaded.Contains(p)).ToArray())
                await DropUnloadedAsync(match, player);
            if (match.Settled) return;
            if (match.Players.Where(p => !match.Absent.Contains(p)).Select(p => match.Faction[p]).Distinct().Count() < 2)
            { await AbortMatchAsync(match, null); return; }
            foreach (var player in match.Players)
                if (run.Players[player].Career == 0) run.Players[player].Career = DefaultCareer(match, run, player);
            SetStage(run, StageShowCareer, now, now + StageMs[StageShowCareer]);
            foreach (var player in InRoom(match).ToArray())
            {
                // The career build starts full (inferred).
                player.CurrentHp = null; player.CurrentSp = null;
                await character.RefreshStatsAsync(player);
            }
            foreach (int faction in match.Players.Select(p => match.Faction[p]).Distinct()) await SyncTeamAsync(match, faction);
        }
        else if (run.Stage == StageShowCareer) SetStage(run, StageChoosePos, now, now + StageMs[StageChoosePos]);
        else if (run.Stage == StageChoosePos)
        {
            run.BattleStartMs = now;
            run.BattleEndMs = now + StageMs[StageStartGame];
            run.LastTickMs = now;
            SetStage(run, StageStartGame, now, run.BattleEndMs);
            match.Deadline = (run.BattleEndMs + 999) / 1000;
            PlanZone(run, now);
            foreach (var player in InRoom(match).ToArray()) await LandAsync(match, run, player);
            await SpawnCratesAsync(match, run);
        }
        await BroadcastBaseInfoAsync(match, run);
        if (run.Stage == StageStartGame)
            foreach (int faction in match.Players.Select(p => match.Faction[p]).Distinct()) await SyncTeamAsync(match, faction);
    }

    private async Task DropUnloadedAsync(Match match, GameSession player)
    {
        match.Absent.Add(player);
        match.Fallen.Add(player);
        active.Remove(player);
        player.EventFaction = 0;
        if (match.Room != null) await instances.AbandonAsync(match.Room, player);
    }

    /// <summary>StartGame: the client sends nothing, so the server moves everyone to the landing it picked (inferred
    /// teleport). No pick: a teammate's pick, else a point inside the island circle (inferred).</summary>
    private async Task LandAsync(Match match, ChickenRun run, GameSession player)
    {
        var me = run.Players[player];
        var mate = Team(match, match.Faction[player]).Select(p => run.Players[p]).Where(p => p.LandChosen)
            .OrderBy(p => p.PickOrder).FirstOrDefault();
        float x, z;
        if (me.LandChosen) { x = me.LandX; z = me.LandZ; }
        else if (mate != null) { x = mate.LandX + me.TeamIndex * 2; z = mate.LandZ; }
        else (x, z) = RandomPoint(run, IslandX, IslandZ, IslandRadius * 0.8f);
        if (WalkablePoint(x, z) is { } at) await scene.TeleportInMapAsync(player, at);
    }

    /// <summary>A walkable point at or near (x, z) on 2240101's nav grid (a ring search up to 40 m).</summary>
    private MapPoint? WalkablePoint(float x, float z)
    {
        if (worldData.Map(ChickenScene) is not { } map) return null;
        if (map.Nav is not { } nav) return new MapPoint(x, 10, z, 0);
        for (int ring = 0; ring <= 20; ring++)
            for (int step = 0; step < Math.Max(1, ring * 8); step++)
            {
                double angle = ring == 0 ? 0 : step * Math.PI * 2 / (ring * 8);
                float px = x + (float)(Math.Cos(angle) * ring * 2), pz = z + (float)(Math.Sin(angle) * ring * 2);
                if (nav.WalkHeight(px, pz, 10) is { } y) return new MapPoint(px, y, pz, 0);
            }
        return null;
    }

    private static (float X, float Z) RandomPoint(ChickenRun run, float cx, float cz, float radius)
    {
        double angle = run.Rng.NextDouble() * Math.PI * 2, distance = Math.Sqrt(run.Rng.NextDouble()) * radius;
        return (cx + (float)(Math.Cos(angle) * distance), cz + (float)(Math.Sin(angle) * distance));
    }

    private const long ChickenScene = 2240101;

    private int PoisonBuff() => PublicValue("EatChickenPoisonBuffId") is var id and > 0 ? (int)id : DefaultPoisonBuff;
    private int BagLimit() => PublicValue("ChickenBagLimit") is var limit and > 0 ? (int)limit : DefaultBagLimit;

    // ---- test aids ----

    /// <summary>GM "!chicken skip" (test aid, not original behaviour): end the current pre-battle stage now.</summary>
    internal async Task<string> SkipStageAsync(GameSession session)
    {
        if (Chicken(session) is not (var match, var run, _)) return "Not in a Survivor Battle match.";
        if (run.Stage >= StageStartGame) return "The battle stage runs until the match ends.";
        await RunMatchTransitionAsync(match, "chicken skip", () => AdvanceStageAsync(match, run, NowMs()));
        return $"Stage {run.Stage} (test aid).";
    }

    /// <summary>
    /// GM "!chicken rank &lt;n&gt;" (test aid, not original behaviour): the Survivor Battle rank, its total points at the
    /// rank's floor; the item-7 wallet is left to the next settlement. Then 5003 tells the client.
    /// </summary>
    internal async Task<string> SetChickenRankAsync(GameSession session, int rank)
    {
        if (session.TimedBattle is not { } state) return "No Survivor Battle state.";
        if (data.Row("EatChickenLevelConfig", rank) == null) return $"No Survivor Battle rank {rank}.";
        var next = JsonSerializer.Deserialize<State>(JsonSerializer.SerializeToUtf8Bytes(state))!;
        next.ChickenRank = rank;
        next.ChickenScore = ChickenRankFloor(rank);
        await SaveAsync(session, next);
        await SyncBattleStateAsync(session);
        return $"Survivor Battle rank {rank} (test aid).";
    }
}
