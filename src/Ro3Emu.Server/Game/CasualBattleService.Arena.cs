using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The Mock Battle arena's monsters, supplies and phases. The client has the pieces; the arena script
/// DungeonLua_2220104 that places and times them is not in the client, so the timeline, counts and respawns here are
/// (inferred).
/// <list type="bullet">
/// <item>NPCConfig (lv 50, camp 3): normal monsters Archer Skeleton 16300001, Skeleton 16300002, Megalodon 16300003,
/// Orc Skeleton 16300004 (_kNpcTag 16300001), the elites 16300009-16300011 (tag 16300003), the central boss Pharaoh
/// 16300006 (tag 16300002); Combat Supplies 16300007 and Large Combat Supplies 16300012 (interactions 1630000701 /
/// 1630001201, mode 2: picked up on reach with a 32700, refused while the player holds buff 16300001: conditions
/// 7000083 / 7000085, tip 280014 "You already have a Battlefield Skill and cannot Pick up another.").</item>
/// <item>Scene sc_pm_leaguematches_002_dg04, m_kGroups (94 spawn points, all npcId 0): 刷怪组-1..4 (spawn groups 24,
/// 44, 55, 65), 刷怪组-中央 (central, 76), 边缘精英小怪组-1 (edge elites, 41), 中央精英怪组 (central elite, 79),
/// 边缘高级物资组-1 / 中央高级物资组-1 (edge / central high supplies, 78 / 80), 边缘buff组1 (edge buff, 103; 109, 117
/// and 123 are empty), 中央buff组 (central buff, 131), BOSS (144).</item>
/// <item>BattleEventPointConfig game type 28, scopes 2 personal / 3 squad / 4 camp: 107 defeat a monster of a tag (5 /
/// 200 / 10), 148 a killing hit on an NPC (Pharaoh 200), 156 damage on an NPC (Pharaoh 2), 114 a streak (41007 [6,-1],
/// chat 5700), 3001 a spawn (41008 Pharaoh, chat 5701). Squads keep no score here.</item>
/// <item>DungeonQuestConfig 16300100-16300105, the objective (var 10001): 1 kill the boss, 2 edge monsters and
/// supplies, 3 central monsters and supplies, 4 waiting for the start, 5 crush them in the center, 6 "Final showdown!
/// Double points and Accelerate Revive!".</item>
/// <item>ChatConfig 5700-5712: the streak, boss respawned, Battle Starts, outer / central supplies spawned, the
/// 10-second countdowns of supplies, boss and deathmatch, "The battlefield closes in ${1} seconds.", and before a
/// match 5711 "Mock Battle begins in 10 minutes" / 5712 "Mock Battle has begun. Go there now?".</item>
/// </list>
/// (inferred) The timeline from the main stage's start: Battle Starts with the edge monsters, the outer supplies 10 s
/// later, the central monsters and supplies at 130 s, Pharaoh at 250 s and 120 s after each death until the death
/// fight; a monster's point refills 30 s after its death, a supply's 60 s after its pickup; one supply on a third of the
/// buff points and on every high supply point. A pickup grants one battlefield skill (the SkillConfig rows of
/// _kSkillCategory 16300101): Large Combat Supplies 16305501 "Summon Madogear Pilot", Combat Supplies one of the
/// others at random; it sits in slot 900 with buff 16300001 while held, and casting it or dying spends it. A killing
/// hit on a monster scores the NPC's own 148 rows, else its tag's 107 rows; every full 1% of Pharaoh's HP a player
/// deals scores the 156 rows. The death fight doubles every score and halves the 30 s revive countdown. The Adamantine
/// Madogear is in CasualBattleService.Madogear.cs.
/// </summary>
public sealed partial class CasualBattleService
{
    private const int TagNormal = 16300001, TagBoss = 16300002, TagElite = 16300003;
    private const int Pharaoh = 16300006, Supplies = 16300007, LargeSupplies = 16300012;
    private const int HoldBuff = 16300001, BattlefieldSlot = 900;
    private const int TypeDefeat = 107, TypeKillingHit = 148, TypeDamage = 156, TypeStreak = 114, TypeSpawn = 3001;
    private const int ObjectiveBoss = 1, ObjectiveEdge = 2, ObjectiveCenter = 3, ObjectiveWait = 4, ObjectiveCrush = 5,
        ObjectiveFinal = 6;
    private const int QuestVar = 10001;
    private const int StartChat = 5703, OuterChat = 5704, CentralChat = 5705, OuterSoonChat = 5706, CentralSoonChat = 5707,
        BossSoonChat = 5708, DeathmatchSoonChat = 5709, ClosingChat = 5710, SoonChat = 5711, BegunChat = 5712;
    private const uint SystemBroadcast = 1022;
    private const int LeisureForty = 28; // BattleEventPointConfig _kGameType, DungeonConfig _iGamePlayType
    private const long ReviveSeconds = 30, SoonSeconds = 10, MonsterRespawn = 30, SupplyRespawn = 60, BossRespawn = 120;
    private const long OuterAt = 10, CentralAt = 130, BossAt = 250;
    private const long SupplyUidBase = 1L << 53, SupplyUidEnd = 1L << 54;

    // Scene groups of sc_pm_leaguematches_002_dg04 (m_kGroups ids).
    private static readonly int[] EdgeGroups = [24, 44, 55, 65];
    private const int CentralGroup = 76, EdgeEliteGroup = 41, CentralEliteGroup = 79, EdgeHighGroup = 78,
        CentralHighGroup = 80, CentralBuffGroup = 131, BossGroup = 144;
    private static readonly int[] EdgeBuffGroups = [103, 109, 117, 123];
    private static readonly int[] NormalMonsters = [16300001, 16300002, 16300003, 16300004];
    private static readonly int[] EliteMonsters = [16300009, 16300010, 16300011];
    /// <summary>SkillConfig _kSkillCategory of the battlefield skills.</summary>
    private const int BattlefieldCategory = 16300101;
    private int[]? _battlefieldSkills;

    private long _supplySerial;
    private long _announcedSoon = -1, _announcedBegun = -1;

    /// <summary>The PvE side of one match.</summary>
    private sealed class Arena
    {
        public readonly HashSet<string> Fired = [];
        /// <summary>A live monster → its scene group and point.</summary>
        public readonly Dictionary<MonsterUnit, (int Group, SpawnPoint Point)> Monsters = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<long, SupplyUnit> Supplies = [];
        public readonly Dictionary<long, MadogearUnit> Madogears = [];
        /// <summary>Refills due: (elapsed second, group, point).</summary>
        public readonly List<(long Due, int Group, SpawnPoint Point)> Refills = [];
        public MonsterUnit? Boss;
        /// <summary>The elapsed second Pharaoh (re)spawns at; 0 none due.</summary>
        public long BossDue;
        public readonly Dictionary<GameSession, long> BossDamage = [];
        public int Objective = ObjectiveWait;
    }

    /// <summary>A supply box: an NPC entity of the room that a player picks up (not a combat unit).</summary>
    private sealed class SupplyUnit : WorldUnit
    {
        public required int NpcId { get; init; }
        public required int Group { get; init; }
        public required SpawnPoint Point { get; init; }
        public MonsterTemplate? Template { get; init; }
        public bool Taken { get; set; }

        public override EntityBase Entity()
        {
            var entity = new EntityBase
            {
                Uuid = (ulong)Uid, EntityTemplateID = (ulong)NpcId, EntityType = 1, Face = Face,
                State = EntityState.Alive, NotifyType = EntityBase.Types.NotifyType.All,
                MovementInfo = new MovementInfo
                {
                    Pos = new Vec3 { X = X, Y = Y, Z = Z }, Face = Face, ServerTime = MonsterService.NowMs,
                    MoveType = MovementType.MoveTypeNone,
                },
            };
            if (Template is { } t)
                foreach (var (idx, value) in new (long, long)[] { (101, t.MaxHp), (2000, t.MaxHp), (2002, t.Level) })
                    entity.Property.Add(new AoIProperty { Idx = idx, Data = value, BaseData = value });
            return entity;
        }
    }

    private void HookArena(CombatService combat)
    {
        combat.Killed.Add(MonsterKilledAsync);
        combat.Dealt.Add(BossDealt);
        combat.PlayerDied.Add(DiedAsync);
        combat.CastLanded.Add(CastLandedAsync);
        combat.GrantedSkill.Add((s, skill) => Live(s) is { } m && m.Players[s].Battlefield == skill);
        combat.GrantedSkill.Add((s, skill) => Live(s) != null && MadogearRiders.SeatSkill(s, skill));
        combat.ReviveInfoOwners.Add(s => Live(s) != null);
        _scene.Leaving.Add(s => MadogearRiders.GetOffAsync(s, interrupt: true));
        _world.PlayerEntered.Add(MadogearRiderSeenAsync);
    }

    // ---- timeline ----

    /// <summary>One second of the arena (AdvanceAsync, after the stage moved): what is due fires once.</summary>
    private async Task ArenaTickAsync(Match m)
    {
        if (m.Ending || m.Stage == 0) return;
        long t = Now - m.Open - PrepareSeconds;
        if (Once(m, "start")) await StartArenaAsync(m);
        if (t >= OuterAt - SoonSeconds && Once(m, "outer-soon")) await ChatAsync(m, OuterSoonChat);
        if (t >= OuterAt && Once(m, "outer")) await SpawnOuterSuppliesAsync(m);
        if (t >= CentralAt - SoonSeconds && Once(m, "central-soon")) await ChatAsync(m, CentralSoonChat);
        if (t >= CentralAt && Once(m, "central")) await SpawnCenterAsync(m);
        await BossTickAsync(m, t);
        await RefillAsync(m, t);
        if (t >= MainSeconds - SoonSeconds && Once(m, "deathmatch-soon"))
        {
            await ChatAsync(m, DeathmatchSoonChat);
            await ObjectiveAsync(m, ObjectiveCrush);
        }
        if (m.Stage == 2 && Once(m, "final")) await ObjectiveAsync(m, ObjectiveFinal);
    }

    private static bool Once(Match m, string key) => m.Arena.Fired.Add(key);

    private async Task StartArenaAsync(Match m)
    {
        await ChatAsync(m, StartChat);
        foreach (int group in EdgeGroups) await SpawnGroupAsync(m, group, NormalMonsters);
        await SpawnGroupAsync(m, EdgeEliteGroup, EliteMonsters);
        m.Arena.BossDue = BossAt;
        await ObjectiveAsync(m, ObjectiveEdge);
    }

    private async Task SpawnOuterSuppliesAsync(Match m)
    {
        foreach (int group in EdgeBuffGroups) await SpawnSuppliesAsync(m, group, Supplies, third: true);
        await SpawnSuppliesAsync(m, EdgeHighGroup, LargeSupplies, third: false);
        await ChatAsync(m, OuterChat);
    }

    private async Task SpawnCenterAsync(Match m)
    {
        await SpawnGroupAsync(m, CentralGroup, NormalMonsters);
        await SpawnGroupAsync(m, CentralEliteGroup, EliteMonsters);
        await SpawnSuppliesAsync(m, CentralBuffGroup, Supplies, third: true);
        await SpawnSuppliesAsync(m, CentralHighGroup, LargeSupplies, third: false);
        await ChatAsync(m, CentralChat);
        if (m.Arena.Boss == null) await ObjectiveAsync(m, ObjectiveCenter);
    }

    /// <summary>Pharaoh: the countdown, the spawn with its 3001 notice, until the death fight.</summary>
    private async Task BossTickAsync(Match m, long t)
    {
        var arena = m.Arena;
        if (arena.Boss != null || arena.BossDue == 0 || arena.BossDue >= MainSeconds) return;
        if (t >= arena.BossDue - SoonSeconds && Once(m, $"boss-soon:{arena.BossDue}")) await ChatAsync(m, BossSoonChat);
        if (t < arena.BossDue || !Once(m, $"boss:{arena.BossDue}")) return;
        if (Points(BossGroup) is not [var point, ..]
            || await _instances.SpawnAsync(m.Room, Pharaoh, ToMapPoint(point)) is not { } boss) return;
        arena.Boss = boss;
        arena.BossDamage.Clear();
        foreach (var row in Rows(TypeSpawn, Pharaoh)) await ChatAsync(m, (int)row.ChatId);
        await ObjectiveAsync(m, ObjectiveBoss);
    }

    private async Task RefillAsync(Match m, long t)
    {
        foreach (var due in m.Arena.Refills.Where(r => r.Due <= t).ToArray())
        {
            m.Arena.Refills.Remove(due);
            if (due.Group is EdgeHighGroup or CentralHighGroup) await SpawnSupplyAsync(m, due.Group, due.Point, LargeSupplies);
            else if (due.Group == CentralBuffGroup || EdgeBuffGroups.Contains(due.Group))
                await SpawnSupplyAsync(m, due.Group, due.Point, Supplies);
            else await SpawnMonsterAsync(m, due.Group, due.Point,
                due.Group is EdgeEliteGroup or CentralEliteGroup ? EliteMonsters : NormalMonsters);
        }
    }

    // ---- spawns ----

    private IReadOnlyList<SpawnPoint> Points(int group) =>
        _worldData.Map(Scene)?.DungeonGroups.GetValueOrDefault(group) ?? [];

    private static MapPoint ToMapPoint(SpawnPoint p) => new(p.X, p.Y, p.Z, p.Face);

    /// <summary>One monster on each point of the group, the kinds in turn by point.</summary>
    private async Task SpawnGroupAsync(Match m, int group, int[] kinds)
    {
        foreach (var point in Points(group)) await SpawnMonsterAsync(m, group, point, kinds);
    }

    private async Task SpawnMonsterAsync(Match m, int group, SpawnPoint point, int[] kinds)
    {
        if (m.Ending || m.Room.Closed) return;
        int npcId = kinds[Math.Abs(point.Id) % kinds.Length];
        if (await _instances.SpawnAsync(m.Room, npcId, ToMapPoint(point)) is { } actor) m.Arena.Monsters[actor] = (group, point);
    }

    /// <summary>The supplies of a group: on every point, or (<paramref name="third"/>) on a random third of them.</summary>
    private async Task SpawnSuppliesAsync(Match m, int group, int npcId, bool third)
    {
        var points = Points(group);
        int count = third ? (points.Count + 2) / 3 : points.Count;
        foreach (var point in points.OrderBy(_ => Random.Shared.Next()).Take(count)) await SpawnSupplyAsync(m, group, point, npcId);
    }

    private async Task SpawnSupplyAsync(Match m, int group, SpawnPoint point, int npcId)
    {
        if (m.Ending || m.Room.Closed) return;
        var at = _worldData.Map(Scene)?.Grounded(ToMapPoint(point)) ?? ToMapPoint(point);
        var unit = new SupplyUnit
        {
            Uid = SupplyUidBase | Interlocked.Increment(ref _supplySerial), SceneEntry = m.Room.SceneEntry,
            InstanceId = m.Room.Id, X = at.X, Y = at.Y, Z = at.Z, Face = at.Face,
            NpcId = npcId, Group = group, Point = point, Template = _monsters.Template(npcId),
        };
        if (await _instances.AddPropAsync(m.Room, unit)) m.Arena.Supplies[unit.Uid] = unit;
    }

    // ---- notices and objective ----

    private static Task ChatAsync(Match m, int chatId, params string[] args)
    {
        if (chatId == 0) return Task.CompletedTask;
        var ntf = new SystemBoardCastMsgNtf { Source = MsgSource.System, ChatId = chatId };
        for (int i = 0; i < args.Length; i++)
            ntf.ParamList[i + 1] = new ChatParameter { Params = { args[i] }, Type = ChatParamType.ChatParamString };
        return RoleFanout.EachAsync(Present(m), s => s.SendGameAsync(SystemBroadcast, ntf));
    }

    private static IEnumerable<GameSession> Present(Match m) => m.Players.Values
        .Where(p => !p.Left && m.Loaded.Contains(p.Session) && p.Session.InstanceId == m.Room.Id).Select(p => p.Session);

    private async Task ObjectiveAsync(Match m, int objective)
    {
        if (m.Arena.Objective == objective) return;
        m.Arena.Objective = objective;
        await BroadcastAsync(m);
    }

    /// <summary>Var 10001 PDungeonQuestIdList: the objective's DungeonQuestConfig _iQuestID.</summary>
    private static DungeonVar ObjectiveVar(Match m)
    {
        var v = new DungeonVar { VarId = QuestVar };
        v.Vi64.Add(m.Arena.Objective);
        return v;
    }

    /// <summary>5710 to the players still in the arena: the 30 s it stays open after the result.</summary>
    private static Task ClosingAsync(Match m) => ChatAsync(m, ClosingChat,
        ExitSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

    // ---- scoring ----

    /// <summary>The game type 28 rows of a BattleEventPointConfig type whose _kData names <paramref name="data"/>.</summary>
    private IEnumerable<BattleEventPointConfig> Rows(int type, long data) => BattleEventPointConfig.All(_data)
        .Where(r => r.Type == type && r.GameType.Contains(LeisureForty) && r.Data.FirstOrDefault() == data);

    /// <summary>A row's personal (2) and camp (4) score, doubled in the death fight.</summary>
    private static void Award(Match m, GameSession player, BattleEventPointConfig row, long times = 1)
    {
        long factor = m.Stage == 2 ? 2 : 1;
        var values = row.Score;
        for (int i = 0; i + 1 < values.Length; i += 2)
            if (values[i] == 4) m.CampScore[m.Players[player].Camp] += values[i + 1] * times * factor;
            else if (values[i] == 2) m.Players[player].Score += values[i + 1] * times * factor;
    }

    /// <summary>CombatService.Killed: a monster of a match, scored for the killer and refilled later.</summary>
    private async Task MonsterKilledAsync(GameSession killer, MonsterUnit monster)
    {
        if (_matches.FirstOrDefault(x => x.Room.Id == monster.InstanceId && monster.InstanceId != 0) is not { } m) return;
        var arena = m.Arena;
        long t = Now - m.Open - PrepareSeconds;
        if (arena.Monsters.Remove(monster, out var at)) arena.Refills.Add((t + MonsterRespawn, at.Group, at.Point));
        if (ReferenceEquals(arena.Boss, monster))
        {
            arena.Boss = null;
            arena.BossDue = t + BossRespawn;
            await ObjectiveAsync(m, ObjectiveCenter);
        }
        if (monster is MadogearUnit madogear) { await MadogearDestroyedAsync(m, madogear); return; }
        if (m.Ending || Live(killer) != m) return;
        var rows = Rows(TypeKillingHit, monster.Template.NpcId).ToArray();
        if (rows.Length == 0 && NPCConfig.Get(_data, monster.Template.NpcId) is { } npc)
            rows = [.. npc.NpcTag.SelectMany(tag => Rows(TypeDefeat, tag))];
        foreach (var row in rows) Award(m, killer, row);
        if (rows.Length > 0) await AfterScoreAsync(m);
    }

    /// <summary>CombatService.Dealt: every full 1% of Pharaoh's HP a player deals scores its 156 rows.</summary>
    private void BossDealt(GameSession s, MonsterUnit monster, long damage)
    {
        if (damage <= 0 || Live(s) is not { } m || !ReferenceEquals(m.Arena.Boss, monster) || monster.Template.MaxHp <= 0) return;
        long before = m.Arena.BossDamage.GetValueOrDefault(s), after = before + damage;
        m.Arena.BossDamage[s] = after;
        long steps = after * 100 / monster.Template.MaxHp - before * 100 / monster.Template.MaxHp;
        if (steps <= 0) return;
        foreach (var row in Rows(TypeDamage, monster.Template.NpcId)) Award(m, s, row, steps);
        _loop.PostLegacy(() => AfterScoreAsync(m), "casual battle boss damage");
    }

    private async Task AfterScoreAsync(Match m)
    {
        if (m.Ending) return;
        await BroadcastAsync(m);
        if (m.CampScore.Values.Any(score => score >= ScoreBar)) RequestSettle(m);
    }

    /// <summary>41007 [6,-1]: the 6th kill in a row without dying is announced (5700, once per streak, inferred).</summary>
    private Task StreakAsync(Match m, GameSession killer)
    {
        var me = m.Players[killer];
        me.Streak++;
        var row = BattleEventPointConfig.All(_data).FirstOrDefault(r => r.Type == TypeStreak && r.GameType.Contains(LeisureForty));
        if (row.Row.ValueKind != JsonValueKind.Object || me.Streak != row.Data.FirstOrDefault()) return Task.CompletedTask;
        return RoleFanout.EachAsync(Present(m), s => s.SendGameAsync(SystemBroadcast, new SystemBoardCastMsgNtf
        { Source = MsgSource.System, ChatId = (int)row.ChatId, PlayerId = me.Id }));
    }

    // ---- supplies and battlefield skills ----

    /// <summary>32700 on a supply box; anything else goes on to its usual owner.</summary>
    public async Task<bool> SupplySharedAsync(GameSession s, uint id, ReadOnlyMemory<byte> body)
    {
        if (id != SceneService.NpcInteraction) return false;
        var req = NpcInteractionReq.Parser.ParseFrom(body.Span);
        if (req.NpcUid < SupplyUidBase || req.NpcUid >= SupplyUidEnd) return false;
        var m = Live(s);
        if (m != null && m.Arena.Madogears.TryGetValue(req.NpcUid, out var madogear))
        {
            await MadogearInteractAsync(s, m, madogear, req);
            return true;
        }
        int code = m == null || !m.Arena.Supplies.TryGetValue(req.NpcUid, out var unit) ? ErrorCodes.NpcMissing
            : PickupError(s, m, unit, req);
        await s.SendGameAsync(id, new NpcInteractionRsp { Result = code, Req = req });
        if (code == 0) await PickUpAsync(s, m!, m!.Arena.Supplies[req.NpcUid]);
        return true;
    }

    private int PickupError(GameSession s, Match m, SupplyUnit unit, NpcInteractionReq req) =>
        unit.Taken || m.Stage == 0 || s.Dead ? ErrorCodes.NpcMissing
        : NPCConfig.Get(_data, unit.NpcId)?.InteractId.Contains(req.InteractionId) != true ? ErrorCodes.NpcParameter
        : MonsterService.Distance(s.Position.X, s.Position.Z, unit.X, unit.Z) > 5 ? ErrorCodes.NpcTooFar
        // Conditions 7000083 / 7000085: not while holding buff 16300001 (one battlefield skill at a time).
        : m.Players[s].Battlefield != 0 || s.Buffs.Any(b => b.ConfigId == HoldBuff) ? ErrorCodes.NpcParameter : 0;

    private async Task PickUpAsync(GameSession s, Match m, SupplyUnit unit)
    {
        unit.Taken = true;
        m.Arena.Supplies.Remove(unit.Uid);
        await _instances.RemovePropAsync(m.Room, unit);
        m.Arena.Refills.Add((Now - m.Open - PrepareSeconds + SupplyRespawn, unit.Group, unit.Point));
        _battlefieldSkills ??= [.. SkillConfig.All(_data).Where(r => r.SkillCategory.Contains(BattlefieldCategory)).Select(r => (int)r.Id)];
        int[] pool = unit.NpcId == LargeSupplies ? [SummonMadogear]
            : [.. _battlefieldSkills.Where(id => id != SummonMadogear)];
        int skill = pool[Random.Shared.Next(pool.Length)];
        m.Players[s].Battlefield = skill;
        await _buffs.AddAsync(s, HoldBuff, (int)Math.Max(1000, (m.Deadline - Now) * 1000), unit.Uid);
        await s.SendGameAsync(CombatService.SkillSlotModify, new SkillSlotModifyNotify
        { Type = 2, SkillId = skill, Slot = BattlefieldSlot, Enable = true }); // enum_SkillSlotModifyType.Add
    }

    /// <summary>The held battlefield skill is spent: its slot and the holding buff go.</summary>
    private async Task SpendAsync(GameSession s, Player me)
    {
        int skill = me.Battlefield;
        if (skill == 0) return;
        me.Battlefield = 0;
        await s.SendGameAsync(CombatService.SkillSlotModify, new SkillSlotModifyNotify
        { Type = 4, SkillId = skill, Slot = BattlefieldSlot }); // enum_SkillSlotModifyType.Remove
        if (s.Buffs.FirstOrDefault(b => b.ConfigId == HoldBuff) is { } buff) await _buffs.RemoveAsync(s, buff);
    }

    /// <summary>The held battlefield skill was cast: it is spent, and Summon Madogear Pilot brings the Madogear.</summary>
    private async Task CastLandedAsync(GameSession s, JsonElement skill)
    {
        if (!_active.TryGetValue(s, out var m) || m.Players[s] is not { Battlefield: > 0 } me
            || me.Battlefield != new SkillConfig(skill).Id) return;
        int cast = me.Battlefield;
        await SpendAsync(s, me);
        if (cast == SummonMadogear) await SummonMadogearAsync(s, m);
    }

    // ---- death and revive ----

    /// <summary>A death: the held skill and the streak are lost, the revive countdown starts (halved in the death fight).</summary>
    private async Task DiedAsync(GameSession s)
    {
        if (Live(s) is not { } m) return;
        var me = m.Players[s];
        me.Streak = 0;
        me.DeadUntil = Now + (m.Stage == 2 ? ReviveSeconds / 2 : ReviveSeconds);
        await SpendAsync(s, me);
        await MadogearRiders.GetOffAsync(s, interrupt: true);
        await s.SendGameAsync(CombatService.ReviveInfo, new SyncClientPlayerReviveInfoNtf
        { BDead = true, ReviveTime = me.DeadUntil, SceneEntry = s.SceneEntry });
    }

    /// <summary>31013 in a match: after the countdown, back at the camp's born point.</summary>
    public async Task<bool> ReviveAsync(GameSession s, EntityReliveReq req, CombatService combat)
    {
        if (Live(s) is not { } m) return false;
        var me = m.Players[s];
        if (!s.Dead || me.DeadUntil > Now)
        {
            await s.SendGameAsync(CombatService.Relive, new EntityReliveAck { Result = ErrorCodes.FeatureUnavailable, EntityID = (ulong)me.Id });
            return true;
        }
        if (await combat.ReviveAsync(s, req)
            && _worldData.Map(Scene)?.Points.TryGetValue(CampPoints[me.Camp - 1], out var start) == true)
            await _scene.TeleportInMapAsync(s, start);
        return true;
    }

    // ---- before the match ----

    /// <summary>5711 ten minutes before a Matching window, 5712 when it opens, to the online players it admits.</summary>
    private void Announce()
    {
        long now = Now;
        var windows = _activityClock.Segments(TimeLimit, now).Where(w => w.State == "Matching").ToArray();
        if (windows.FirstOrDefault(w => w.Start > now && w.Start - now <= 600) is { } soon && _announcedSoon != soon.Start)
        {
            _announcedSoon = soon.Start;
            _loop.PostLegacy(() => AnnounceAsync(SoonChat), "casual battle notice");
        }
        if (windows.FirstOrDefault(w => w.Contains(now)) is { } open && _announcedBegun != open.Start)
        {
            _announcedBegun = open.Start;
            _loop.PostLegacy(() => AnnounceAsync(BegunChat), "casual battle notice");
        }
    }

    private Task AnnounceAsync(int chatId)
    {
        long level = GameData.Int(_data.Row("DailyActivityConfig", ActivityId)!.Value, "_iiLv");
        var ntf = new SystemBoardCastMsgNtf { Source = MsgSource.System, ChatId = chatId };
        return RoleFanout.EachAsync(_sessions.All().Where(s => s.Role != null && s.Progress?.Level >= level
            && s.InstanceId == 0 && _functions.IsOpen(s, Function)), s => s.SendGameAsync(SystemBroadcast, ntf));
    }
}
