using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataTheory]
    [InlineData(1, 12)]
    [InlineData(5, 20)]
    public async Task DeathBoundLowAndHighestReflectRealMeleeHpLossWithOneSharedFiveTargetCap(int level, int percent)
    {
        await using var alice = await InWorld("BoundAtk");
        await using var bob = await InWorld("BoundDef");
        var attacker = Session("boundatk"); var defender = Session("bounddef");
        await Gm("boundatk", "!job 1100"); await Teach("boundatk", 11001);
        await Gm("bounddef", "!job 1400"); await Teach("bounddef", 11311, level);
        await CardOffenceRoom((alice, attacker), (bob, defender));
        var combat = _app.Services.GetRequiredService<CombatService>(); combat.Rolls = new CardOffenceRolls();
        var monsters = new List<MonsterUnit>();
        for (int i = 0; i < 6; i++)
        {
            var monster = await Target("bounddef", stationary: true);
            await CardOffenceDummyAsync(monster); monsters.Add(monster);
        }
        await Loop.InvokeAsync(() =>
        {
            attacker.Position = defender.Position.Clone();
            combat.SetEventPvPHandlers((a, b) => a == attacker && b == defender || a == defender && b == attacker,
                (_, _) => Task.CompletedTask);
            return Task.CompletedTask;
        });
        await bob.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1131100 + level });
        Assert.Equal(0, (await Next(bob, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(bob, BuffService.BuffUpdate, BuffUpdateNotify.Parser,
            update => update.Buffs.Any(b => b.ConfigId == 1131100 + level));
        long damage = await CardOffencePvpWireHit(alice, defender, 1100101);
        int reflectedSkill = 1138100 + level;
        var reflected = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SkillId == reflectedSkill && h.Dsts.Any(d => d.DstID == attacker.Role!.Id));
        long expected = damage * percent / 100;
        Assert.Equal(expected, Assert.Single(reflected.Dsts).Damage);
        Assert.Equal(1_000_000 - expected, attacker.CurrentHp);
        var monsterHit = await Next(bob, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SkillId == reflectedSkill && h.Dsts.Any(d => d.DstID == monsters[0].Uid));
        Assert.Equal(4, monsterHit.Dsts.Count); Assert.All(monsterHit.Dsts, h => Assert.Equal(expected, h.Damage));
        Assert.Equal(4, monsters.Count(m => m.Hp == 1_000_000 - expected));
        Assert.Equal(2, monsters.Count(m => m.Hp == 1_000_000));
        Assert.Equal(1_000_000 - damage, defender.CurrentHp); // 反射子技不得把自身選擇器誤解成回血。
        Assert.DoesNotContain(monsterHit.Dsts, h => h.DstID == defender.Role!.Id);
    }

    [ClientDataTheory]
    [InlineData("ranged")]
    [InlineData("zero")]
    [InlineData("reflection")]
    [InlineData("expired")]
    public async Task DeathBoundRejectsNonEligiblePublishedHits(string kind)
    {
        await using var alice = await InWorld("DeathBoundGateA");
        await using var bob = await InWorld("DeathBoundGateB");
        var attacker = Session("deathboundgatea"); var defender = Session("deathboundgateb");
        await CardOffenceRoom((alice, attacker), (bob, defender));
        var combat = _app.Services.GetRequiredService<CombatService>();
        var data = _app.Services.GetRequiredService<GameData>();
        await Loop.InvokeAsync(async () =>
        {
            attacker.Position = defender.Position.Clone();
            combat.SetEventPvPHandlers((a, b) => a != b, (_, _) => Task.CompletedTask);
            await _app.Services.GetRequiredService<BuffService>().AddSupportAsync(defender, 1131105, 10000, defender.Role!.Id);
            var skill = data.Row("SkillConfig", kind == "ranged" ? 1200401 : 1100101)
                ?? throw new InvalidOperationException("Paired attack is required.");
            var node = JsonNode.Parse(skill.GetRawText())?.AsObject() ?? throw new InvalidOperationException("Skill object required.");
            if (kind == "reflection") node["_ro3CardReflection"] = true;
            if (kind == "expired")
            {
                int index = defender.Buffs.FindIndex(b => b.ConfigId == 1131105);
                defender.Buffs[index] = defender.Buffs[index] with { ExpireMs = MonsterService.NowMs - 1 };
            }
            foreach (var callback in combat.PvpSkillHitPublished)
                await callback(attacker, defender, JsonSerializer.SerializeToElement(node), kind == "zero" ? 0 : 1000, DamageHitResult.Normal);
        });
        Assert.Equal(1_000_000, attacker.CurrentHp);
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body).SkillId == 1138105);
    }
}
