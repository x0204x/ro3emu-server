using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class DungeonTests
{
    /// <summary>
    /// Follow-up to the real Culvert entry death: genuine Normal/Hard config, wire queue/ready/entry,
    /// idle safety past the observed two-second death, explicit player pull, real retaliation and settlement.
    /// Only the Normal boss's final HP is controlled to finish the reward regression quickly.
    /// </summary>
    [ClientDataTheory]
    [InlineData(50101, 4300001, 30, 380963L, 603L, false)]
    [InlineData(50104, 4300000, 60, 10352920L, 12527L, true)]
    public async Task MatchedCulvertWaitsForPlayerEngagementBeforeBossDamage(int multi, int npc,
        int level, long maxHp, long attack, bool hard)
    {
        _clock.Advance(TimeSpan.FromDays(8)); // Hard's exported server-day gate remains enforced.
        await using var alice = await InWorld("CulvertTester");
        await Gm(alice, "!lvl 99");
        await Gm(alice, "!dungeonsolo on");
        long beforeQueue = _clock.GetUtcNow().ToUnixTimeSeconds();
        await alice.SendGameAsync(DungeonMatchService.StartMatch, new StartMatchReq
        {
            MatchEntryId = 100,
            MatchData = new MatchData
            {
                MultiDungeon = new MultiDungeonMatchData
                { DungeonId = Scene, MultiDungeonId = multi, MarkId = 1 }
            },
        });
        var queued = await Next(alice, DungeonMatchService.StartMatch, StartMatchResp.Parser);
        Assert.Equal(0, queued.Result);
        Assert.Equal(beforeQueue, queued.MatchStartTime);
        var ready = await Next(alice, DungeonMatchService.ReadyNotify, MatchReadyNotify.Parser);
        Assert.Equal(30, ready.EndTime - beforeQueue); // Same seconds arithmetic as the client ready widget.
        Assert.False(HasSwitch(alice));
        await alice.SendGameAsync(DungeonMatchService.Ready, new MatchReadyReq
        { MatchEntryId = 100, PlayerId = (long)alice.Self!.Uuid, IsReady = true });
        Assert.Equal(0, (await Next(alice, DungeonMatchService.Ready, MatchReadyResp.Parser)).Result);
        long instance = await Load(alice);
        var initial = alice.Inbox.Snapshot().Where(e => e.IsGame(DungeonService.BaseInfo))
            .Select(e => DungeonBaseInfoNotify.Parser.ParseFrom(e.Body)).First(n => n.DungeonId == instance);
        Assert.Equal(0, initial.StageModuleData.MapState);
        Assert.Equal(0, initial.StageModuleData.StageStartTime);
        Assert.Equal(0, initial.StageModuleData.StageEndTime); // No fake January 1970 deadline while loading.
        Assert.Equal(0, initial.StageModuleData.MapStateEndTime);
        var running = await Next(alice, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser,
            n => n.DungeonId == instance && n.StageModuleData.MapState == 1);
        Assert.True(running.StageModuleData.StageStartTime > 1_000_000_000_000);
        Assert.Equal(36_000_000, running.StageModuleData.StageEndTime - running.StageModuleData.StageStartTime);
        var boss = await BossIn(instance, npc);
        Assert.Equal((level, maxHp, attack), (boss.Template.Level, boss.Template.MaxHp, boss.Template.Atk));
        Assert.Equal(10000, boss.DamageRatio); // GM solo entry never scales the five-player encounter down.
        Assert.Equal((50f, 60f), (boss.Home.X, boss.Home.Z));
        Assert.Equal((50f, 75f), (Session(alice).Position.X, Session(alice).Position.Z));
        Assert.Equal(15f, MonsterService.Distance(boss, Session(alice)), 3);
        Assert.Equal(0f, boss.Template.RoamDistance);
        Assert.Equal((100f, 100f), (boss.Template.ViewRange, boss.Template.LeashDistance));
        long hpOnEntry = Service<CharacterService>().Stats(Session(alice))[2000];

        // Exercise real map ticks longer than the captured 1.94s chase-and-hit. Inspect both HP and wire hits,
        // since regeneration could conceal damage if only the final HP were checked.
        await Task.Delay(3000);
        await OnLoop(() =>
        {
            Assert.False(Session(alice).Dead);
            Assert.Equal(hpOnEntry, Service<CharacterService>().Stats(Session(alice))[2000]);
            Assert.Null(boss.Target);
            Assert.False(boss.Moving);
            Assert.Equal((50f, 60f), (boss.X, boss.Z));
            return Task.CompletedTask;
        });
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body).SrcID == boss.Uid);

        // Being close is not a substitute for the missing encounter script's activation rule.
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(alice),
            new MapPoint(boss.X + 1, boss.Y, boss.Z, 270)));
        await Task.Delay(500);
        await OnLoop(() => { Assert.Null(boss.Target); return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = boss.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SrcID == (long)alice.Self.Uuid
            && h.Dsts.Any(d => d.DstID == boss.Uid));
        var retaliation = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SrcID == boss.Uid && h.Dsts.Any(d => d.DstID == (long)alice.Self.Uuid));
        Assert.True(Assert.Single(retaliation.Dsts).Damage > 0);
        if (hard)
        {
            // This GM-levelled role is not a geared five-player tank. Preserve the configured difficulty:
            // the current fallback swing is lethal after an intentional pull, never on arrival.
            Assert.True(Assert.Single(retaliation.Dsts).IsDead);
            Assert.Equal((DungeonRes)2, (await Next(alice, DungeonService.Result, DungeonResNotify.Parser)).Res);
            Assert.Empty(Session(alice).Dungeons!.Records);
            Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(DungeonService.AwardNotify));
        }
        else
        {
            Assert.False(Assert.Single(retaliation.Dsts).IsDead);
            await Kill(alice, boss); // Controlled HP=1; the killing skill, rewards and persistence stay real.
            Assert.Equal((DungeonRes)1, (await Next(alice, DungeonService.Result, DungeonResNotify.Parser)).Res);
            var award = await Next(alice, DungeonService.AwardNotify, SyncMultiDungeonAwardNotify.Parser);
            Assert.InRange(award.PassTime, 3000, 30_000); // Duration remains milliseconds.
            Assert.Equal(100, Items(alice, 1250900));
            Assert.Single(Session(alice).Dungeons!.Records);
        }
        await alice.SendGameAsync(DungeonService.Leave, new LeaveDungeonReq());
        Assert.Equal(0, (await Next(alice, DungeonService.Leave, LeaveDungeonRsp.Parser)).Result);
        var outside = await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser,
            n => n.SceneEntry != Scene);
        Assert.Equal(RoleStore.SpawnSceneEntry, outside.SceneEntry);
        await alice.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = outside.SceneEntry });
        Assert.Equal(0, (await Next(alice, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        var self = await Next(alice, MsgId.EntitySelf, EntitySelf.Parser);
        Assert.Equal(EntityState.Alive, self.Base.State);
        if (hard)
        {
            Assert.Equal(self.Base.Property.Single(p => p.Idx == 101).Data,
                self.Base.Property.Single(p => p.Idx == 2000).Data);
            Assert.Equal(self.Base.Property.Single(p => p.Idx == 102).Data,
                self.Base.Property.Single(p => p.Idx == 2001).Data);
            await Next(alice, DungeonService.ReviveInfo, SyncClientPlayerReviveInfoNtf.Parser,
                n => !n.BDead && n.SceneEntry == outside.SceneEntry);
            Assert.Empty(Session(alice).Dungeons!.Records);
            Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(DungeonService.AwardNotify));
        }
        Assert.False(Session(alice).Dead);
        Assert.Null(Session(alice).DungeonAttempt);
        Assert.Null(Service<InstanceService>().Find(instance));
    }
}
