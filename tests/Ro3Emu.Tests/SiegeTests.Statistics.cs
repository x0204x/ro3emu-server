using Romsg;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SiegeTests
{
    /// <summary>
    /// The siege HUD's kill and assist counters read only 30123 (UI_Sys_Siege_BattleReportBtnWidgetBaseCtrl ->
    /// SelfDungeonStatisticsInfo): each player gets its own on entry, and the killer gets the new count at a kill.
    /// </summary>
    [ClientDataFact]
    public async Task EntryAndAKillPushTheKillersOwnCounters()
    {
        await using var attack = await Bot("StatAttacker");
        await using var defense = await Bot("StatDefender");
        long ga = await Guild(attack, "StAtk"), gd = await Guild(defense, "StDef");
        long attackRole = Session(attack).Role!.Id, defenseRole = Session(defense).Role!.Id;
        await OnLoop(async () =>
        {
            var twClock = Service<TerritoryWarsClock>();
            var seeded = new TerritoryWarsService.WorldState { Epoch = $"{twClock.SeasonStart}:{twClock.PlayBook}" };
            seeded.Owners[1] = gd;
            seeded.Battles[301] = new TerritoryWarsService.BattleState
            {
                Zone = 1, Round = 3, Settled = true,
                Guilds = [ga, gd], Scores = new() { [ga] = 10, [gd] = 5 }
            };
            await SeedWarsOnLoopAsync(seeded);
        });
        _clock.Set(_siege.Start);
        await OnLoop(Service<TerritoryWarsService>().ReconcileAsync);
        await Enter(attack, 1); await Enter(defense, 2);
        static bool Own(DungeonPlayerStatisticsNtf n, long role) => n.PlayerId == role && n.StatisticsInfo.BaseInfo.Id == role;
        var entry = await Next(attack, DungeonService.PlayerStatistics, DungeonPlayerStatisticsNtf.Parser, n => Own(n, attackRole));
        Assert.Equal(0, entry.StatisticsInfo.ExtraData[1001004].LongValue); // KillPlayer
        await Next(defense, DungeonService.PlayerStatistics, DungeonPlayerStatisticsNtf.Parser, n => Own(n, defenseRole));

        await OnLoop(async () =>
        {
            await Service<SceneService>().TeleportInMapAsync(Session(attack), new MapPoint(153.5f, 27.15f, 119.5f, 0));
            await Service<SceneService>().TeleportInMapAsync(Session(defense), new MapPoint(154.5f, 27.15f, 119.5f, 0));
            await Service<CharacterService>().SetHpSpAsync(Session(defense), 1, null);
        });
        await attack.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = defenseRole });
        Assert.Equal(0, (await Next(attack, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var kill = await Next(attack, DungeonService.PlayerStatistics, DungeonPlayerStatisticsNtf.Parser,
            n => Own(n, attackRole) && n.StatisticsInfo.ExtraData[1001004].LongValue == 1);
        Assert.Equal(0, kill.StatisticsInfo.ExtraData[1001005].LongValue); // AssistKill
    }
}
