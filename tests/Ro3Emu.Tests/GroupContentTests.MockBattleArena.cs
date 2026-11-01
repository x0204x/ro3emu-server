using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// The Mock Battle arena (CasualBattleService.Arena.cs): the edge monsters at Battle Starts, the supply boxes and their
/// battlefield skill, monster and boss scores by BattleEventPointConfig, the objectives and the death fight's double
/// points. One player alone, with the "!casual open" test aid.
/// </summary>
public sealed partial class GroupContentTests
{
    private const int SystemBroadcast = 1022;

    private Task<WorldUnit[]> UnitsIn(long instance, int npc) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(
        Service<World>().AllUnits().Where(u => u.InstanceId == instance && (long)u.Entity().EntityTemplateID == npc
            && u is not MonsterUnit { Dead: true }).ToArray()));

    /// <summary>A live monster of the room of one of the kinds (the arena has many of each).</summary>
    private async Task<MonsterUnit> MonsterOf(long instance, params int[] npcs)
    {
        for (int i = 0; i < 150; i++)
        {
            var found = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(Service<World>().AllUnits().OfType<MonsterUnit>()
                .FirstOrDefault(m => m.InstanceId == instance && npcs.Contains(m.Template.NpcId) && !m.Dead)));
            if (found != null) return found;
            await Task.Delay(20);
        }
        throw new TimeoutException("no arena monster " + string.Join(',', npcs));
    }

    private static async Task Notice(BotClient bot, int chat) =>
        await Next(bot, SystemBroadcast, SystemBoardCastMsgNtf.Parser, n => n.ChatId == chat);

    private static Task<DungeonBaseInfoNotify> Objective(BotClient bot, long quest) =>
        Next(bot, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser,
            m => m.Vars.Any(v => v.VarId == 10001 && v.Vi64.SequenceEqual([quest])));

    private static Task<DungeonBaseInfoNotify> CampScore(BotClient bot, long score) =>
        Next(bot, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser,
            m => m.Vars.Any(v => v.VarId == 304 && v.Vi64[1] == score));

    /// <summary>Battle Starts brings the edge monsters, the supplies a battlefield skill, a kill its points.</summary>
    [ClientDataFact]
    public async Task MockBattleArenaSpawnsMonstersSuppliesAndScoresThem()
    {
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        await Gm(alice, "!casual open");
        Assert.Equal(0, (await SignUp(alice)).Result);
        await Gm(alice, "!casual start"); // one camp alone, as the test aid allows
        var (instance, info) = await Load(alice, CasualBattleService.Scene);
        Assert.Equal([4L], info.Vars.Single(v => v.VarId == 10001).Vi64); // "Waiting for the battle to start"

        _clock.Advance(TimeSpan.FromSeconds(61));
        await Tick();
        await Notice(alice, 5703); // Battle Starts!
        await Objective(alice, 2); // the edge monsters and supplies
        await MonsterOf(instance, 16300009, 16300010, 16300011); // an elite of the edge elite group

        _clock.Advance(TimeSpan.FromSeconds(10));
        await Tick();
        await Notice(alice, 5704); // Outer supplies have Spawned!
        var box = (await UnitsIn(instance, 16300007)).First();
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(alice), new MapPoint(box.X + 1, box.Y, box.Z, 0)));
        var pickup = new NpcInteractionReq { NpcUid = box.Uid, InteractionId = 1630000701 };
        await alice.SendGameAsync(SceneService.NpcInteraction, pickup);
        Assert.Equal(0, (await Next(alice, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
        var slot = await Next(alice, CombatService.SkillSlotModify, SkillSlotModifyNotify.Parser);
        Assert.Equal((2, 900), (slot.Type, slot.Slot)); // Add, in the interaction slot
        Assert.Contains(Session(alice).Buffs, b => b.ConfigId == 16300001);

        var monster = await MonsterOf(instance, 16300001, 16300002, 16300003, 16300004); // NpcTag 16300001: 41012 +5
        await Kill(alice, monster);
        await CampScore(alice, 5);
    }

    /// <summary>A Large Combat Supplies gives Summon Madogear Pilot; its cast raises a Madogear of the caster's camp
    /// (5702, 41031 +1) that the caster boards (the seat's skills granted) and leaves.</summary>
    [ClientDataFact]
    public async Task MockBattleMadogearIsSummonedBoardedAndLeft()
    {
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        await Gm(alice, "!casual open");
        Assert.Equal(0, (await SignUp(alice)).Result);
        await Gm(alice, "!casual start");
        var (instance, _) = await Load(alice, CasualBattleService.Scene);
        _clock.Advance(TimeSpan.FromSeconds(71));
        await Tick();
        await Notice(alice, 5704);

        var box = (await UnitsIn(instance, 16300012)).First(); // Large Combat Supplies on an edge high-supply point
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(alice), new MapPoint(box.X + 1, box.Y, box.Z, 0)));
        await alice.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq { NpcUid = box.Uid, InteractionId = 1630001201 });
        Assert.Equal(0, (await Next(alice, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
        Assert.Equal(16305501, (await Next(alice, CombatService.SkillSlotModify, SkillSlotModifyNotify.Parser)).SkillId);

        var at = Session(alice).Position;
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        { SkillId = 16305501, TargetPos = new Vec3 { X = at.X, Y = at.Y, Z = at.Z } });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Notice(alice, 5702); // "${1} Adamantine Madogear Pilot has respawned."
        await CampScore(alice, 1); // 41031
        var madogear = (await UnitsIn(instance, 16300008)).Single();

        await alice.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq { NpcUid = madogear.Uid, InteractionId = 1630000801 });
        Assert.Equal(0, (await Next(alice, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
        await Next(alice, WorldInteractionService.Change, InteractChangeNtf.Parser,
            n => n.DataInfo.EType == InteractInfo.Types.EState.Vehicle && n.DataInfo.ActiveObjId == madogear.Uid);
        Assert.True(await Service<GameLoop>().InvokeAsync(() => Task.FromResult(
            Service<CombatService>().GrantedSkill.Any(granted => granted(Session(alice), 16301001))))); // Hammer Strike

        await alice.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq { NpcUid = madogear.Uid, InteractionId = 4900025 });
        Assert.Equal(0, (await Next(alice, SceneService.NpcInteraction, NpcInteractionRsp.Parser,
            r => r.Req.InteractionId == 4900025)).Result);
        await Next(alice, WorldInteractionService.Change, InteractChangeNtf.Parser, n => n.DataInfo.EType == InteractInfo.Types.EState.Idle);
    }

    /// <summary>Pharaoh comes with 5701 and is worth its 148 rows; the death fight doubles every score.</summary>
    [ClientDataFact]
    public async Task MockBattleArenaBossAndDeathFight()
    {
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        await Gm(alice, "!casual open");
        Assert.Equal(0, (await SignUp(alice)).Result);
        await Gm(alice, "!casual start");
        var (instance, _) = await Load(alice, CasualBattleService.Scene);
        _clock.Advance(TimeSpan.FromSeconds(61));
        await Tick();
        await Objective(alice, 2);

        await Gm(alice, "!casual at 320"); // test aid: past the boss's spawn
        _clock.Advance(TimeSpan.FromSeconds(1)); // the tick runs once a second
        await Tick();
        await Notice(alice, 5701); // The central Boss has respawned!
        await Objective(alice, 1);
        await Kill(alice, await MonsterOf(instance, 16300006));
        await CampScore(alice, 200); // 41005: the killing hit on Pharaoh
        await Objective(alice, 3);

        await Gm(alice, "!casual at 601");
        _clock.Advance(TimeSpan.FromSeconds(1));
        await Tick();
        await Objective(alice, 6); // "Final showdown! Double points and Accelerate Revive!"
        await Kill(alice, await MonsterOf(instance, 16300001, 16300002, 16300003, 16300004)); // +5, doubled
        await CampScore(alice, 210);

        // Accelerate Revive: the death fight's countdown is 15 s, not 30.
        await OnLoop(() => Service<CombatService>().EnvironmentDamageAsync(Session(alice), long.MaxValue / 4));
        var dead = await Next(alice, CombatService.ReviveInfo, SyncClientPlayerReviveInfoNtf.Parser, n => n.BDead);
        long at = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(_clock.Now.ToUnixTimeSeconds()));
        Assert.Equal(at + 15, dead.ReviveTime);
        await alice.SendGameAsync(CombatService.Relive, new EntityReliveReq());
        Assert.Equal(ErrorCodes.FeatureUnavailable, (await Next(alice, CombatService.Relive, EntityReliveAck.Parser)).Result);
        _clock.Advance(TimeSpan.FromSeconds(15));
        await alice.SendGameAsync(CombatService.Relive, new EntityReliveReq());
        Assert.Equal(0, (await Next(alice, CombatService.Relive, EntityReliveAck.Parser)).Result);
    }
}
