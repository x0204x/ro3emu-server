using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class DungeonTests
{
    /// <summary>
    /// 保留三艦原始滿血，核對真正傷害、原生三連特效、沒有第二組一般飄字及不追逐玩家。
    /// 原服每砲實際扣血仍 UNKNOWN；這個測試不把畫面九百萬或 HP=1 快速測試當成擊沉速度驗收。
    /// </summary>
    [ClientDataFact]
    public async Task AirshipCannonFullHealthHitsUseNativeEffectsWithoutForgingNineMillionDamage()
    {
        await using var alice = await InWorld("Alice");
        AirshipStoryTestPace();
        await Gm(alice, "!lvl 99");
        await Gm(alice, "!quest accept 10103210");
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = LifecycleAirship }));
        long instance = await Load(alice, LifecycleAirship);
        await Stage(alice, 2, quest: 2);
        var cannon = (await ActorsIn(alice, 1, actor => actor.NpcId == 5300022))[0];
        var ships = await WaveIn(instance, 3);
        var visible = await ClientAirships(alice);
        await Beside(alice, cannon.X, cannon.Y, cannon.Z);
        Assert.Equal(0, await Interact(alice, cannon, 5300006));

        foreach (var view in visible)
        {
            var ship = ships.Single(unit => unit.Uid == (long)view.Uuid);
            long before = await OnLoopValue(() => ship.Hp);
            Assert.Equal(ship.Template.NpcId == 5300015 ? 24900 : 14081, before);
            int mark = alice.Inbox.Snapshot().Count;
            await AirshipTpsShotWithProjectile(alice, cannon, view);
            var launch = alice.Inbox.Snapshot().Skip(mark).Where(packet =>
                packet.IsGame(CombatService.SkillEffect) || packet.IsGame(CombatService.SkillBulletHigh)).ToArray();
            Assert.True(launch.Length >= 2);
            Assert.True(launch[0].IsGame(CombatService.SkillEffect));
            var muzzle = SkillEffectDisplayNotify.Parser.ParseFrom(launch[0].Body);
            Assert.Equal(5353201, muzzle.EffectId);
            Assert.Equal(3, muzzle.EffectType);
            Assert.Equal(Session(alice).Role!.Id, muzzle.SourceId);
            Assert.Equal(muzzle.SourceId, muzzle.MasterId);
            Assert.True(launch[1].IsGame(CombatService.SkillBulletHigh));
            await Next(alice, CombatService.SkillEffect, SkillEffectDisplayNotify.Parser,
                effect => effect.SkillId == 5353001 && effect.EffectId == 6032010
                    && Equals(effect.TargetPos, view.MovementInfo.Pos));
            var effects = alice.Inbox.Snapshot().Skip(mark)
                .Where(packet => packet.IsGame(CombatService.SkillEffect))
                .Select(packet => SkillEffectDisplayNotify.Parser.ParseFrom(packet.Body))
                .Where(effect => effect.SkillId == 5353001 && effect.EffectType is 2 or 4).ToArray();
            Assert.Equal([(6120103, 4), (6120103, 2), (6032010, 2)],
                effects.Select(effect => (effect.EffectId, effect.EffectType)));
            Assert.All(effects, effect =>
            {
                Assert.Equal(Session(alice).Role!.Id, effect.MasterId);
                Assert.Equal(0, effect.SourceId);
                Assert.Equal(0, effect.TargetId);
                Assert.Equal(view.MovementInfo.Pos, effect.TargetPos);
            });
            await OnLoop(() =>
            {
                Assert.InRange(ship.Hp, 1, before - 1);
                Assert.False(ship.Dead);
                Assert.False(ship.Moving);
                Assert.False(ship.Returning);
                Assert.Null(ship.Target);
                Assert.Equal(view.MovementInfo.Pos, ship.Pos);
                Assert.Equal(1, Session(alice).DungeonAttempt!.Wave);
                return Task.CompletedTask;
            });
            Assert.DoesNotContain(alice.Inbox.Snapshot().Skip(mark), packet => packet.IsGame(CombatService.SkillHit)
                && SkillHitInfo.Parser.ParseFrom(packet.Body).Dsts.Any(hit => hit.DstID == ship.Uid));
        }
    }
}
