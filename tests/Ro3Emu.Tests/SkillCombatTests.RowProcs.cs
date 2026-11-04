using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private sealed class SureProcs : CombatRolls
    {
        public override double Hit() => 0;
        public override double Crit() => 1;
        public override int Proc(int max) => 0;
    }

    private async Task<(Ro3Emu.Bot.BotClient Bot, GameSession Session, MonsterUnit Dummy)> ReadyAsync(Ro3Emu.Bot.BotClient alice, int job, int group, int skill)
    {
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job " + job); await Teach("alice", group);
        var a = Session("alice");
        a.GmCastSkill = skill;
        _app.Services.GetRequiredService<CombatService>().Rolls = new SureProcs();
        var dummy = await Target("alice", stationary: true, npcId: 5000002);
        await Loop.InvokeAsync(() =>
        {
            dummy.StaysOutOfBattle = true; dummy.Hp = 1000000;
            dummy.X = a.Position.X + 1; dummy.Y = a.Position.Y; dummy.Z = a.Position.Z;
            return Task.CompletedTask;
        });
        return (alice, a, dummy);
    }

    private static async Task CastAtAsync(Ro3Emu.Bot.BotClient bot, int skill, MonsterUnit target) =>
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skill, TargetId = target.Uid, TargetPos = target.Pos, Face = 90 });

    [ClientDataFact] // Envenom: "applies Poisoned damage": a second hit, of its row 1511601
    public async Task EnvenomLandsItsPoisonedHit()
    {
        await using var alice = await InWorld("Alice");
        var (bot, _, dummy) = await ReadyAsync(alice, 1500, 15005, 1500501);
        await CastAtAsync(bot, 1500501, dummy);
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var poisoned = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1511601 && h.Dsts.Any(d => d.DstID == dummy.Uid));
        Assert.True(poisoned.Dsts.Single().Damage > 0);
    }

    [ClientDataFact] // Crimson Rock: "a 60% Chance to trigger Meteorite"
    public async Task CrimsonRockTriggersMeteorite()
    {
        await using var alice = await InWorld("Alice");
        var (bot, _, dummy) = await ReadyAsync(alice, 2300, 12312, 1231201);
        await CastAtAsync(bot, 1231201, dummy);
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.NotNull(await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1235001 && h.Dsts.Any(d => d.DstID == dummy.Uid)));
    }

    [ClientDataFact] // Turn Undead: "a Normal Undead Monster ... kill it instantly"; a dummy of another element only takes its hit
    public async Task TurnUndeadKillsOnlyANormalUndead()
    {
        await using var alice = await InWorld("Alice");
        var (bot, _, dummy) = await ReadyAsync(alice, 1400, 14118, 1411801);
        Assert.NotEqual(10, dummy.Template.Element);
        await CastAtAsync(bot, 1411801, dummy);
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.Dsts.Any(d => d.DstID == dummy.Uid));
        await Task.Delay(100);
        Assert.True(dummy.Hp > 0);
    }

    [ClientDataFact] // Heavy Cannon Shot: "The next time you Deals DMG, removes Charge Up and triggers Charge Up Strike"
    public async Task ChargeUpGoesOffOnTheNextHit()
    {
        await using var alice = await InWorld("Alice");
        var (bot, a, dummy) = await ReadyAsync(alice, 1100, 10010, 1001001);
        await Loop.InvokeAsync(() => _app.Services.GetRequiredService<BuffService>().AddAsync(a, 1160405, 8000, a.Role!.Id));
        await CastAtAsync(bot, 1001001, dummy);
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.NotNull(await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1162205 && h.Dsts.Any(d => d.DstID == dummy.Uid)));
        Assert.DoesNotContain(a.Buffs, b => b.ConfigId == 1160405);
    }

    [ClientDataFact] // Warg Bite: "resetting the Cooldown of Warg Hunt"
    public async Task WargBiteResetsWargHunt()
    {
        await using var alice = await InWorld("Alice");
        var (bot, a, dummy) = await ReadyAsync(alice, 1330, 13325, 1332501);
        await Loop.InvokeAsync(() => { a.SkillCds[13323] = MonsterService.NowMs + 60000; return Task.CompletedTask; });
        await CastAtAsync(bot, 1332501, dummy);
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var deadline = DateTime.UtcNow + Wait;
        while (a.SkillCds.ContainsKey(13323) && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.False(a.SkillCds.ContainsKey(13323));
    }

    [ClientDataFact] // Crescent Wheel: "For every 1 enemies hit, reduce Axe Tornado's Cooldown by 2 seconds"
    public async Task CutCooldownTakesTimeOffAndEndsPastItsEnd()
    {
        await using var alice = await InWorld("Alice");
        var a = Session("alice");
        long start = MonsterService.NowMs;
        await Loop.InvokeAsync(async () =>
        {
            a.SkillCds[16304] = start + 8000;
            await CombatService.CutCooldownAsync(a, 16304, 2000);
            Assert.Equal(start + 6000, a.SkillCds[16304]);
            await CombatService.CutCooldownAsync(a, 16304, 60000);
            Assert.False(a.SkillCds.ContainsKey(16304));
        });
    }
}
