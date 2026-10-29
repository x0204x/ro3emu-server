using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class MapTests
{
    /// <summary>Alice next to Angeling (cfg 1) with the boss engaged on her; its swings, chase and roam are frozen.</summary>
    private async Task<(BotClient Alice, EntityBase Boss)> EngagedAngelingAsync(bool stopSkills)
    {
        var alice = await InWorld("Alice");
        try
        {
            await Gm("alice", "!lvl 99");
            for (int attempt = 0; attempt < 20; attempt++)
            {
                await Gm("alice", "!mvp where 1");
                var reply = await alice.Inbox.WaitAsync(e => e.IsGame(GmCommands.BroadcastNotify)
                    && (System.Text.Encoding.UTF8.GetString(e.Body).Contains("MVP 1")
                        || System.Text.Encoding.UTF8.GetString(e.Body).StartsWith("10214 ")), Wait);
                if (System.Text.Encoding.UTF8.GetString(reply.Body).StartsWith("10214 ")) break;
                await Task.Delay(200);
            }
            await Gm("alice", "!mvp goto 1");
            var (_, _, npcs) = await FollowSceneChange(alice);
            var angeling = npcs.Bases.FirstOrDefault(b => b.EntityTemplateID == 5020001)
                ?? (await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
                    v => v.Bases.Any(b => b.EntityTemplateID == 5020001))).Bases.Single(b => b.EntityTemplateID == 5020001);
            await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(async () =>
            {
                var player = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == "alice");
                var boss = MergedBoss((long)angeling.Uuid);
                boss.Advance(MonsterService.NowMs);
                boss.Moving = false;
                boss.NextRoamMs = boss.NextSwingMs = boss.NextChaseMs = long.MaxValue;
                await _app.Services.GetRequiredService<SceneService>().TeleportInMapAsync(player,
                    new MapPoint(boss.X, boss.Y, boss.Z, boss.Face));
                if (stopSkills) _app.Services.GetRequiredService<MvpService>().StopSummonsAfterObservationForTest(boss);
                _app.Services.GetRequiredService<MonsterService>().Engage(boss, player);
                boss.NextSwingMs = boss.NextChaseMs = long.MaxValue;
            });
            return (alice, angeling);
        }
        catch { await alice.DisposeAsync(); throw; }
    }

    [ClientDataFact]
    public async Task AngelingCarriesBornBuffsItsPoringsExplodeAndItResetsOutOfCombat()
    {
        var (alice, angeling) = await EngagedAngelingAsync(stopSkills: false);
        await using var _ = alice;
        long uid = (long)angeling.Uuid;
        // _kBornEventBuff: the three permanent MVP buffs are in the boss's AOI snapshot.
        var born = angeling.BuffData.AddBuffs.Select(b => b.ConfigId).ToArray();
        Assert.Equal([900901, 904101, 904301], born.Order().ToArray());
        Assert.All(angeling.BuffData.AddBuffs, b => Assert.Equal(0L, b.ExpireTime));

        // Summon Self-Destruct Poring: each minion starts its 16 s Poring Explosion at once.
        var summon = UseInSkillInfo.Parser.ParseFrom((await alice.Inbox.WaitAsync(
            e => e.IsGame(CombatService.UseInSkill) && UseInSkillInfo.Parser.ParseFrom(e.Body) is var u
                && u.SrcID == uid && u.SkillID == 5202101, Wait)).Body);
        Assert.True(summon.PlayAct);
        Assert.True(summon.SkillMs > 0);
        Assert.True(summon.StartTime > 0);
        var blast = UseInSkillInfo.Parser.ParseFrom((await alice.Inbox.WaitAsync(e => e.IsGame(CombatService.UseInSkill)
            && UseInSkillInfo.Parser.ParseFrom(e.Body).SkillID == 5201501, Wait)).Body);
        Assert.Equal(16000, blast.SkillMs);
        Assert.True(blast.PlayAct);
        Assert.True(blast.StartTime > 0);
        var mvp = _app.Services.GetRequiredService<MvpService>();
        var loop = _app.Services.GetRequiredService<GameLoop>();
        bool ready = false;
        for (int attempt = 0; attempt < 50 && !ready; attempt++)
        {
            ready = await loop.InvokeAsync(() => Task.FromResult(_app.Services.GetRequiredService<World>().AllUnits()
                .OfType<MonsterUnit>().Count(m => m.Template.NpcId == 5000036 && !m.Dead) == 3));
            if (!ready) await Task.Delay(50);
        }
        Assert.True(ready, "Angeling's summon must create three porings.");
        await loop.InvokeAsync(async () =>
        {
            var boss = MergedBoss(uid);
            mvp.StopSummonsAfterObservationForTest(boss);
            // The blast skill holds them in place: they neither walk nor swing.
            Assert.All(_app.Services.GetRequiredService<World>().AllUnits().OfType<MonsterUnit>().Where(m => m.Template.NpcId == 5000036),
                m => Assert.Equal(0, m.Template.SpeedCm));
            await mvp.EndMinionLivesForTest(boss);
        });
        var hit = SkillHitInfo.Parser.ParseFrom((await alice.Inbox.WaitAsync(e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body).SkillId == 5201501, Wait)).Body);
        Assert.True(hit.Dsts.Single().Damage > 0);
        Assert.False(await loop.InvokeAsync(() => Task.FromResult(_app.Services.GetRequiredService<World>().AllUnits()
            .OfType<MonsterUnit>().Any(m => m.Template.NpcId == 5000036 && !m.Dead))));

        // Out of combat for AIConfig 20200001 _iUnFightParam 5000 ms: buff 901401, removed when it fights again.
        await loop.InvokeAsync(() => { MergedBoss(uid).Target = null; return Task.CompletedTask; });
        var reset = await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser,
            n => n.UnitId == uid && n.Buffs.Any(b => b.ConfigId == 901401));
        long guid = reset.Buffs.Single(b => b.ConfigId == 901401).BuffGuid;
        await loop.InvokeAsync(() =>
        {
            var player = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == "alice");
            if (player.Dead) return Task.CompletedTask;
            _app.Services.GetRequiredService<MonsterService>().Engage(MergedBoss(uid), player);
            MergedBoss(uid).NextSwingMs = long.MaxValue;
            return Task.CompletedTask;
        });
        if (await loop.InvokeAsync(() => Task.FromResult(MergedBoss(uid).Target != null)))
            await Next(alice, BuffService.BuffRemove, BuffRemoveNotify.Parser, n => n.UnitId == uid && n.BuffGuids.Contains(guid));
    }

    [ClientDataFact]
    public async Task RealmOfLightMarksPlayersThenBurnsTheGroundUnderThem()
    {
        var (alice, angeling) = await EngagedAngelingAsync(stopSkills: true);
        await using var _ = alice;
        long uid = (long)angeling.Uuid;
        var mvp = _app.Services.GetRequiredService<MvpService>();
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() => mvp.CastForTest(MergedBoss(uid), 5201701));
        await alice.Inbox.WaitAsync(e => e.IsGame(CombatService.UseInSkill) && UseInSkillInfo.Parser.ParseFrom(e.Body) is var u
            && u.SrcID == uid && u.SkillID == 5201701, Wait);
        // AngelingSelect's mark (5001901, 5 s), then a Sanctified Ground (NPC type 4) owned by the boss under Alice.
        var mark = await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser, n => n.Buffs.Any(b => b.ConfigId == 5001901));
        Assert.Equal(5000, mark.Buffs.Single().Duration);
        var field = (await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            v => v.Bases.Any(b => b.EntityTemplateID == 5000037))).Bases.Single(b => b.EntityTemplateID == 5000037);
        Assert.Equal(4, field.EntityType);
        Assert.Equal(uid, field.Owner);
        var burn = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SkillId == 5201701 && h.SrcID == (long)field.Uuid);
        Assert.True(burn.Dsts.Single().Damage > 0);
    }

    [ClientDataFact]
    public async Task GoldenThiefBugFrenzyRaisesItsDamageAndDamageTaken()
    {
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var mvp = _app.Services.GetRequiredService<MvpService>();
        MonsterUnit? bug = null;
        for (int attempt = 0; attempt < 100 && bug == null; attempt++)
        {
            bug = await loop.InvokeAsync(() => Task.FromResult(_app.Services.GetRequiredService<World>().AllUnits()
                .OfType<MonsterUnit>().FirstOrDefault(m => m.Template.NpcId == 5020002 && !m.Dead)));
            if (bug == null) await Task.Delay(100);
        }
        Assert.NotNull(bug);
        Assert.Equal(10000, await loop.InvokeAsync(() => Task.FromResult(mvp.FrenzyDealt(bug, 10000))));
        await loop.InvokeAsync(() => mvp.CastForTest(bug, 5202301));
        long dealt = 0, taken = 0;
        for (int attempt = 0; attempt < 50 && dealt != 13000; attempt++)
        {
            await Task.Delay(100);
            (dealt, taken) = await loop.InvokeAsync(() => Task.FromResult((mvp.FrenzyDealt(bug, 10000), mvp.FrenzyTaken(bug, 10000))));
        }
        Assert.Equal((13000L, 13000L), (dealt, taken));
    }
}
