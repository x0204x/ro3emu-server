using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Battle Merit: points for kills, assists and deaths on a BattleGloryBase map, and the rank rewards (83400).</summary>
public sealed partial class TerritoryWorldTests
{
    [ClientDataFact]
    public async Task KillsAssistsAndDeathsEarnBattleMeritAndARankRewardPaysOnce()
    {
        await using var killer = await InWorld("GloryKiller");
        await using var helper = await InWorld("GloryHelper");
        await using var victim = await InWorld("GloryVictim");
        const long mapId = 30301; // BattleGloryBase: kill 150, assist 100, death 100
        foreach (var bot in new[] { killer, helper, victim })
        {
            await OnLoop(() => Service<CharacterService>().SetLevelAsync(Session(bot), 99, null));
            await bot.SendGameAsync(SceneService.SceneTeleport, new SceneTeleportReq { TargetMapId = mapId });
            Assert.Equal(0, (await Next(bot, SceneService.SceneTeleport, SceneTeleportRsp.Parser)).Result);
            Assert.Equal(mapId, (await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser)).SceneEntry);
            await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = mapId });
            Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
            await AwaitSceneReady(bot);
            await bot.SendGameAsync(TerritoryWarsService.Camp, new PvpSetRoleCampReq { Type = EPvpRoleCampNum.Free });
            Assert.Equal(0, (await Next(bot, TerritoryWarsService.Camp, PvpSetRoleCampResp.Parser)).Result);
        }
        // The level-up's power is published with the next state change: publish it now, so the three equal roles
        // meet at the same power (a stale one would put the difference past -10000, coefficient 0).
        foreach (var bot in new[] { killer, helper, victim })
            await OnLoop(async () =>
            {
                using var owner = await Service<GameLoop>().Owners!.EnterAsync("test/glory power",
                    Ro3Emu.Server.Game.Ownership.ReservationKey.Role(Session(bot).Role!.Id));
                await Service<PowerService>().PublishAsync(Session(bot), always: true);
            });
        await OnLoop(() =>
        {
            Assert.Single(new[] { killer, helper, victim }.Select(b => PowerService.Total(PowerService.Current(Session(b)))).Distinct());
            return Task.CompletedTask;
        });
        var combat = Service<CombatService>();
        Task Kill(bool assisted) => OnLoop(async () =>
        {
            if (assisted)
                foreach (var hit in combat.PvpDealtAsync) await hit(Session(helper), Session(victim), 100, false);
            foreach (var killed in combat.PlayerKilled) await killed(Session(killer), Session(victim));
        });
        // The purse applies after the receipt commits: wait for the expected balance.
        async Task Merit(BotClient bot, long expected)
        {
            long now = 0;
            for (int i = 0; i < 500 && now != expected; i++)
            {
                await OnLoop(() => { now = Session(bot).Progress!.Currency.GetValueOrDefault(TerritoryWarsService.GloryCurrency); return Task.CompletedTask; });
                if (now != expected) await Task.Delay(10);
            }
            Assert.Equal(expected, now);
        }

        // Equal fresh roles: the power difference is inside +-1000 (coefficient 1).
        await Kill(assisted: true);
        await Merit(killer, 150);
        await Merit(helper, 100);
        await Merit(victim, 100);
        // The same pair again today: KillDif / DeathDif count 2 (8000); no fresh hit, no assist.
        await Kill(assisted: false);
        await Merit(killer, 150 + 120);
        await Merit(helper, 100);
        await Merit(victim, 100 + 80);
        var synced = await Next(killer, TerritoryWarsService.GlorySync, DBBattleGloryInfoIncrement.Parser);
        Assert.NotNull(synced);

        // Rank 2 needs 1000 points: not reached yet.
        await killer.SendGameAsync(TerritoryWarsService.GloryRankReward, new BattleGloryRankRewardReq { RankReward = { [2] = 1 } });
        Assert.Equal(ErrorCodes.NoRankReward, (await Next(killer, TerritoryWarsService.GloryRankReward, BattleGloryRankRewardResp.Parser)).Result);
        await OnLoop(() => { Session(killer).TerritoryWars!.Glory.Rank = 3; return Task.CompletedTask; });
        await killer.SendGameAsync(TerritoryWarsService.GloryRankReward, new BattleGloryRankRewardReq { RankReward = { [2] = 1 } });
        var claimed = await Next(killer, TerritoryWarsService.GloryRankReward, BattleGloryRankRewardResp.Parser);
        Assert.Equal(0, claimed.Result);
        Assert.Equal(0, claimed.ReceiveResult[2]);
        await OnLoop(() =>
        {
            var info = (DBBattleGloryInfo)Session(killer).Synced[TerritoryWarsService.GlorySync];
            Assert.Equal(3, info.CurRankId);
            Assert.Equal([2L], info.RankRewardReceiveInfo.Keys);
            return Task.CompletedTask;
        });
        // Once only; rank 4 is not reached; rank 10 is the leaderboard's, not a claim.
        foreach (var (level, error) in new[] { (2, ErrorCodes.RewardClaimed), (4, ErrorCodes.NoRankReward), (10, ErrorCodes.RewardMissing) })
        {
            await killer.SendGameAsync(TerritoryWarsService.GloryRankReward, new BattleGloryRankRewardReq { RankReward = { [level] = 1 } });
            Assert.Equal(error, (await Next(killer, TerritoryWarsService.GloryRankReward, BattleGloryRankRewardResp.Parser,
                r => r.ReceiveResult.ContainsKey(level))).Result);
        }
    }
}
