using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class DungeonTests
{
    /// <summary>其他流程測試的快速前置也必須合法登砲；只有前置可縮成 HP=1，五砲驗收另測原滿血。</summary>
    private async Task KillAirshipFixtureShip(Ro3Emu.Bot.BotClient bot, Ro3Emu.Server.Maps.MonsterUnit ship)
    {
        var cannon = await OnLoopValue(() => Session(bot).DungeonAttempt!.CannonUsers.GetValueOrDefault(Session(bot)));
        if (cannon == null)
        {
            cannon = (await ActorsIn(bot, 1, actor => actor.NpcId == 5300022))[0];
            await Beside(bot, cannon.X, cannon.Y, cannon.Z);
            Assert.Equal(0, await Interact(bot, cannon, 5300006));
        }
        await OnLoop(() => { Assert.True(ship.Hp > 1); ship.Hp = 1; return Task.CompletedTask; });
        await AirshipTpsShotWithProjectile(bot, cannon, ship.Entity());
        await AirshipNativeImpactAsync(bot, ship);
    }

    /// <summary>使用者指定的五砲規則；滿血與真正封包管線驗證，不把飄字當 HP，也不預先削血。</summary>
    [ClientDataFact]
    public async Task AirshipThreeFullHealthShipsNeedFiveHitsAndKeepTheRequestedBodyAim()
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
        var views = await ClientAirships(alice);
        await Beside(alice, cannon.X, cannon.Y, cannon.Z);
        Assert.Equal(0, await Interact(alice, cannon, 5300006));
        ActiveSkillCast? resolved = null;
        Service<CombatService>().CastDamageOverrides.Insert(0, (_, cast, _, _) => { resolved = cast; return null; });

        var forged = CannonShot(cannon, views[0]);
        forged.TargetPos.X += 10000;
        await alice.SendGameAsync(CombatService.UseSkill, forged);
        Assert.NotEqual(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);

        // 合法空瞄仍有彈道，不能從附近三艘船挑一艘補命中。
        var empty = cannon.Pos.Clone();
        empty.X += 45;
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        { SkillId = 5353001, OpUnitId = cannon.Uid, TargetPos = empty });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var emptyBullet = await Next(alice, CombatService.SkillBulletHigh, SkillFixTimeBulletNotify.Parser);
        Assert.Equal(empty, emptyBullet.TargetPosition);
        await Task.Delay(emptyBullet.FixedTime + 600);
        Assert.All(ships, ship => Assert.Equal(ship.Template.MaxHp, ship.Hp));

        foreach (var view in views)
        {
            var ship = ships.Single(unit => unit.Uid == (long)view.Uuid);
            await OnLoop(() =>
            {
                // 人造極高防禦／閃避，驗證 TPS 命中不依賴一般角色攻擊力與命中率。
                ship.Template = ship.Template with { Def = 10000000, Mdef = 10000000, Flee = 10000000 };
                return Task.CompletedTask;
            });
            for (int shot = 1; shot <= 5; shot++)
            {
                var request = CannonShot(cannon, view);
                // 人造船身點，刻意不等於根點；不同瞄點都必須完整保留至落點。
                request.TargetPos.Y += 10 + shot;
                request.TargetPos.Z += shot;
                await alice.SendGameAsync(CombatService.UseSkill, request);
                Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
                var use = await Next(alice, CombatService.UseInSkillHigh, UseInSkillInfo.Parser,
                    message => message.SkillID == 5353001 && message.TargetPos.Equals(request.TargetPos));
                var bullet = await Next(alice, CombatService.SkillBulletHigh, SkillFixTimeBulletNotify.Parser,
                    message => message.TargetPosition.Equals(request.TargetPos));
                Assert.Equal(use.TargetPos, bullet.TargetPosition);
                Assert.Equal(0, use.TargetId);
                Assert.Equal(MonsterService.Angle(bullet.SourcePosition.X, bullet.SourcePosition.Z,
                    bullet.TargetPosition.X, bullet.TargetPosition.Z), use.Face, 3);
                Assert.Equal(0, bullet.TargetId);
                // 同一時間重送不多放一砲，也不預先扣血。
                await alice.SendGameAsync(CombatService.UseSkill, request);
                Assert.NotEqual(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
                bool dismount = view.Uuid == views[0].Uuid && shot == 1;
                if (dismount)
                {
                    await alice.SendGameAsync(WorldInteractionService.Action, new UseInteractActionReq { ButtonId = 28 });
                    Assert.Equal(0, (await Next(alice, WorldInteractionService.Action, UseInteractActionRes.Parser)).Result);
                }
                await Next(alice, CombatService.SkillEffect, SkillEffectDisplayNotify.Parser,
                    effect => effect.EffectId == 6032010 && effect.TargetPos.Equals(request.TargetPos));
                await OnLoop(() =>
                {
                    if (shot < 5)
                    {
                        var actualCast = Assert.IsType<ActiveSkillCast>(resolved);
                        long after = ship.Hp;
                        Assert.Equal(0, Service<CombatService>().CastDamageOverrides.Skip(1)
                            .Select(rule => rule(Session(alice), actualCast, ship, DamageHitResult.Normal))
                            .First(value => value.HasValue)!.Value.Amount);
                        Assert.Equal(after, ship.Hp); // 重複回呼沒有第二次傷害。
                        var rule = Service<CombatService>().CastDamageOverrides[1];
                        int fire = actualCast.Request.SkillId;
                        actualCast.Request.SkillId = 1; // 人造普通技能，不得打掉任務敵艦。
                        Assert.Equal(0, rule(Session(alice), actualCast, ship, DamageHitResult.Normal)!.Value.Amount);
                        actualCast.Request.SkillId = fire;
                        var template = ship.Template;
                        ship.Template = template with { NpcId = 1 }; // 人造一般 NPC，不能套五砲規則。
                        Assert.Null(rule(Session(alice), actualCast, ship, DamageHitResult.Normal));
                        ship.Template = template;
                        Assert.InRange(ship.Hp, 1, ship.Template.MaxHp - 1);
                        Assert.False(ship.Dead);
                        Assert.Equal(1, Session(alice).DungeonAttempt!.Wave);
                    }
                    else Assert.Equal(0, ship.Hp);
                    return Task.CompletedTask;
                });
                if (dismount) Assert.Equal(0, await Interact(alice, cannon, 5300006));
                await Task.Delay(600);
            }
        }
        await Stage(alice, 3, quest: 3);
        Assert.All(ships, ship => Assert.True(ship.Dead));
    }
}
