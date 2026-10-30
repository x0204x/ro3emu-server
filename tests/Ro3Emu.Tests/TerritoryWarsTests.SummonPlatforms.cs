using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>The stronghold battle 3030101's SummonPlatforms: a guild fills a platform's ring and takes its vehicle.</summary>
public sealed partial class TerritoryWarsTests
{
    private const int PlatformVehicle = 4900402, OpenRing = 4905011, GuildRing = 4905014;

    /// <summary>
    /// Four guilds bid for state 2 in round 1; the point phase deals the ranked qualifiers round-robin over its three
    /// strongholds, so the first and the fourth fight in 3030101. Returns them in the started battle.
    /// </summary>
    private async Task<(BotClient A, BotClient B, BotClient[] Others, long Ga, long Gb)> StrongholdBattle(string tag)
    {
        var a = await Bot("TwPa" + tag);
        var b = await Bot("TwPb" + tag);
        BotClient[] others = [await Bot("TwPc" + tag), await Bot("TwPd" + tag)];
        long ga = await Guild(a, "TpA" + tag), gb = await Guild(b, "TpB" + tag);
        await Guild(others[0], "TpC" + tag); await Guild(others[1], "TpD" + tag);
        var wars = Service<TerritoryWarsService>();
        _clock.Set(1779276700); await OnLoop(wars.ReconcileAsync); // TerritoryWarStageConfig 5: round 1 auction.
        await Bid(a, 1400); await Bid(others[0], 1300); await Bid(others[1], 1200); await Bid(b, 1100);
        _clock.Set(1779362200); await OnLoop(wars.ReconcileAsync); // stage 7: the auction settled.
        await Enter(a, 3030101); await Enter(b, 3030101);
        _clock.Set(Session(a).TerritoryBattle!.State.Start + 1); await OnLoop(wars.ReconcileAsync);
        return (a, b, others, ga, gb);
    }

    private async Task<TerritoryWarsService.PlatformRing> Ring(TerritoryWarsService.Battle battle, int point)
    {
        TerritoryWarsService.PlatformRing? ring = null;
        await OnLoop(() => { ring = battle.PlatformRings.GetValueOrDefault(point); return Task.CompletedTask; });
        return ring ?? throw new InvalidOperationException($"No ring at platform {point}");
    }

    private Task Teleport(BotClient bot, float x, float y, float z) =>
        OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(bot), new MapPoint(x, y, z, 0)));

    private async Task Step(int seconds)
    {
        _clock.Set(_clock.GetUtcNow().ToUnixTimeSeconds() + seconds);
        await OnLoop(Service<TerritoryWarsService>().ReconcileAsync);
    }

    [ClientDataFact]
    public async Task AGuildFillsAPlatformRingAndTakesItsVehicleThatRechargesAfterItFalls()
    {
        var (a, b, others, ga, gb) = await StrongholdBattle("V");
        await using var _ = a;
        await using var __ = b;
        await using var c = others[0];
        await using var d = others[1];
        var wars = Service<TerritoryWarsService>();
        var battle = Session(a).TerritoryBattle!;
        Assert.Same(battle, Session(b).TerritoryBattle);
        long activator = Session(a).Role!.Id;
        Assert.Equal(0, await AssignSelf(a, WartimeToolType.Vehicle));

        // The southern platform's ring stands on its group's second point (50, where 49 is) and belongs to nobody.
        var ring = await Ring(battle, 49);
        var editor = Service<WorldData>().Map(3030101)!;
        Assert.Equal((OpenRing, editor.Points[50].X, editor.Points[50].Z), (ring.NpcId, ring.X, ring.Z));
        Assert.True(ring.Uid < 1L << 53);
        Assert.Equal(GuildRing, (await Ring(battle, 83)).NpcId);

        // Activate: the multi-person state with the ring as its object, the ring now the guild's.
        await Teleport(a, ring.X, ring.Y, ring.Z);
        Assert.Equal(0, await Interact(a, (ulong)ring.Uid, 4905011));
        var state = await Next(a, WorldInteractionService.Change, InteractChangeNtf.Parser,
            n => n.Uid == activator && n.DataInfo.EType == InteractInfo.Types.EState.MultiPersonProgress);
        Assert.Equal((22L, 4905011, ring.Uid), (state.DataInfo.StateId, state.DataInfo.InteractConfigId, state.DataInfo.ActiveObjId));
        // One side at a time.
        await Teleport(b, ring.X + 1, ring.Y, ring.Z);
        Assert.Equal(ErrorCodes.NpcParameter, await Interact(b, (ulong)ring.Uid, 4905011));

        // GVGFieldOccupySpeed 1 a second for one activator, up to GVGFieldOccupyAmount 30.
        await Step(5);
        var progress = await Next(a, WorldCoreService.CustomChange, CustomDataChange.Parser, n => n.EntityId == ring.Uid
            && n.CustomDatas.Any(x => x.Id == 10001 && x.CustomParam.LongParam == 5));
        Assert.Equal((ga, 30L, 1L), (progress.CustomDatas.Single(x => x.Id == 104).CustomParam.LongParam,
            progress.CustomDatas.Single(x => x.Id == 10000).CustomParam.LongParam, progress.CustomDatas.Single(x => x.Id == 10002).CustomParam.LongParam));
        for (int i = 0; i < 5; i++) await Step(5);

        // Activated: the activator is back on foot, the point is the guild's, the battle hears it and the vehicle stands.
        var done = await Next(a, WorldInteractionService.Change, InteractChangeNtf.Parser,
            n => n.Uid == activator && n.DataInfo.EType == InteractInfo.Types.EState.Idle);
        Assert.False(done.Interrupt);
        var point = await Next(a, TerritoryWarsService.ScenePointNotify, GlobalScenePointDataNotify.Parser,
            n => n.GlobalPoints.TryGetValue(49, out var p) && p.Status == EnumGlobalScenePoint.Complete);
        Assert.Equal((EnumGlobalScenePoint.Complete, ga), (point.GlobalPoints[49].Status, point.GlobalPoints[49].BelongGuildId));
        await Next(b, 1022, SystemBoardCastMsgNtf.Parser, n => n.ChatId == 4111018);
        var vehicle = await Appeared(a, PlatformVehicle);
        Assert.Equal((0L, 1L, ga), (vehicle.Owner, vehicle.EntityCamp, vehicle.GuildId));
        Assert.Contains(vehicle.CustomDatas, x => x.Id == 104 && x.CustomParam.LongParam == ga);

        // The guild's Vehicle operator boards it; the other guild cannot, but may break it.
        Assert.Equal(0, await AssignSelf(b, WartimeToolType.Vehicle));
        Assert.Equal(ErrorCodes.NpcParameter, await Interact(b, vehicle.Uuid, 4900022));
        Assert.Equal(0, await Interact(a, vehicle.Uuid, 4900022));
        await Next(a, WorldInteractionService.Change, InteractChangeNtf.Parser,
            n => n.Uid == activator && n.DataInfo.EType == InteractInfo.Types.EState.Vehicle);
        var unit = Machine(vehicle.Uuid);
        Assert.True(wars.CanHitObjective(Session(b), unit));
        Assert.False(wars.CanHitObjective(Session(a), unit));
        await OnLoop(() => { unit.Hp = 1; return Task.CompletedTask; });
        await b.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = (long)vehicle.Uuid });
        Assert.Equal(0, (await Next(b, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(a, WorldInteractionService.Change, InteractChangeNtf.Parser,
            n => n.Uid == activator && n.DataInfo.EType == InteractInfo.Types.EState.Idle);

        // The platform recharges (inferred 120 s) and is announced ready again with a new ring.
        await Step(1);
        point = await Next(a, TerritoryWarsService.ScenePointNotify, GlobalScenePointDataNotify.Parser,
            n => n.GlobalPoints.TryGetValue(49, out var p) && p.Status == EnumGlobalScenePoint.UnReady);
        Assert.Equal((_clock.GetUtcNow().ToUnixTimeSeconds() + 120) * 1000, point.GlobalPoints[49].ReadyTime);
        await Step(120);
        point = await Next(a, TerritoryWarsService.ScenePointNotify, GlobalScenePointDataNotify.Parser,
            n => n.GlobalPoints.TryGetValue(49, out var p) && p.Status == EnumGlobalScenePoint.Ready);
        Assert.Equal((0L, 0L), (point.GlobalPoints[49].ReadyTime, point.GlobalPoints[49].BelongGuildId));
        await Next(a, 1022, SystemBoardCastMsgNtf.Parser, n => n.ChatId == 4111013);
        await Step(1);
        Assert.NotEqual(ring.Uid, (await Ring(battle, 49)).Uid);
    }

    [ClientDataFact]
    public async Task TheDefendersPlatformAnswersOnlyToTheStrongholdHolderAndAWalkEndsTheActivation()
    {
        var (a, b, others, _, gb) = await StrongholdBattle("D");
        await using var _ = a;
        await using var __ = b;
        await using var c = others[0];
        await using var d = others[1];
        var battle = Session(b).TerritoryBattle!;
        long activator = Session(b).Role!.Id;
        var ring = await Ring(battle, 83);
        Assert.Equal(0L, ring.Guild); // nobody holds the stronghold yet
        await Teleport(b, ring.X, ring.Y, ring.Z);
        Assert.Equal(ErrorCodes.NpcParameter, await Interact(b, (ulong)ring.Uid, 4905012));

        // The guild that takes the stronghold's flag owns the defenders' ring (BelongGuild, CheckSameAtkGuild 1).
        await OnLoop(() => { battle.State.Points[0].Owner = gb; return Task.CompletedTask; });
        await Step(1);
        await Next(b, WorldCoreService.CustomChange, CustomDataChange.Parser, n => n.EntityId == ring.Uid
            && n.CustomDatas.Any(x => x.Id == 104 && x.CustomParam.LongParam == gb));
        await Teleport(a, ring.X + 1, ring.Y, ring.Z);
        Assert.Equal(ErrorCodes.NpcParameter, await Interact(a, (ulong)ring.Uid, 4905012));
        Assert.Equal(0, await Interact(b, (ulong)ring.Uid, 4905012));
        await Next(b, WorldInteractionService.Change, InteractChangeNtf.Parser,
            n => n.Uid == activator && n.DataInfo.EType == InteractInfo.Types.EState.MultiPersonProgress);

        // Walking off the ring ends the activation.
        await OnLoop(() =>
        {
            var s = Session(b);
            s.Position = new Vec3 { X = s.Position.X + 2, Y = s.Position.Y, Z = s.Position.Z };
            return Service<TerritoryWarsService>().PlatformMovedAsync(s);
        });
        var idle = await Next(b, WorldInteractionService.Change, InteractChangeNtf.Parser,
            n => n.Uid == activator && n.DataInfo.EType == InteractInfo.Types.EState.Idle);
        Assert.True(idle.Interrupt);

        // The settlement takes the rings away.
        _clock.Set(battle.State.End + 1); await OnLoop(Service<TerritoryWarsService>().ReconcileAsync);
        await Next(b, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser, v => v.LeaveAoi.Contains((ulong)ring.Uid));
    }
}
