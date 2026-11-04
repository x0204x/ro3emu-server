using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private async Task<(MonsterUnit Target, GameSession Player, Func<int> Strikes)> ReactionSetup(string name, int buff)
    {
        var combat = _app.Services.GetRequiredService<CombatService>();
        combat.Rolls = new CritRolls(1);
        var target = await Target(name, stationary: true);
        var p = Session(name);
        int strikes = 0;
        combat.DealtAsync.Add((_, m, _, _) => { if (m == target) strikes++; return Task.CompletedTask; });
        await Loop.InvokeAsync(async () =>
        {
            target.Template = target.Template with { MaxHp = 100_000_000 };
            target.Hp = 100_000_000;
            target.X = p.Position.X + 1;
            target.Z = p.Position.Z;
            await _app.Services.GetRequiredService<BuffService>().AddAsync(p, buff, 60000, p.Role!.Id);
        });
        return (target, p, () => strikes);
    }

    private Task Hit(GameSession p, MonsterUnit from) => Loop.InvokeAsync(() =>
    {
        var row = _app.Services.GetRequiredService<GameData>().Row("SkillConfig", from.Template.NormalSkill);
        return _app.Services.GetRequiredService<CombatService>().ReactToHitAsync(p, from, null, row, 100);
    });

    [ClientDataTheory] // each buff names its row, and its text's cooldown holds the second hit back
    [InlineData(1121001)] // Parry: "casts [Auto Counter] in front of You"
    [InlineData(1140401)] // Shield Reflect
    [InlineData(1161001)] // Reflect Damage
    public async Task AClassBuffStrikesBackOnceWithinItsCooldown(int buff)
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1400");
        var (target, p, strikes) = await ReactionSetup("alice", buff);
        await Hit(p, target);
        await Hit(p, target);
        Assert.Equal(1, strikes());
    }

    [ClientDataFact] // Death Bound names enemy players; a monster's hit does nothing
    public async Task DeathBoundIgnoresAMonster()
    {
        await using var alice = await InWorld("Alice");
        var (target, p, strikes) = await ReactionSetup("alice", 1131101);
        await Hit(p, target);
        Assert.Equal(0, strikes());
    }

    [ClientDataFact] // "When Parry ends, perform an Auto Counter"; 1110501's parry lasts 1 s
    public async Task AutoCounterStrikesWhenItsParryEnds()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1100"); await Teach("alice", 11105);
        var combat = _app.Services.GetRequiredService<CombatService>();
        combat.Rolls = new CritRolls(1);
        var target = await Target("alice", stationary: true);
        var a = Session("alice");
        a.GmCombat |= GmCombatSwitches.Unlimited;
        var seen = new List<long>();
        combat.DealtAsync.Add((_, m, _, amount) => { if (m == target) seen.Add(amount); return Task.CompletedTask; });
        await Loop.InvokeAsync(() =>
        {
            target.Template = target.Template with { MaxHp = 100_000_000 };
            target.Hp = 100_000_000;
            float rad = a.Face * MathF.PI / 180;
            target.X = a.Position.X + MathF.Sin(rad);
            target.Z = a.Position.Z + MathF.Cos(rad);
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1110501, Face = a.Face });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Task.Delay(2500);
        Assert.Single(seen);
    }

    [ClientDataFact] // Devotion: "you take 12% of the damage they receive"
    public async Task DevotionMovesItsShareToTheGuardian()
    {
        await using var alice = await InWorld("Alice"); await using var bob = await InWorld("Bob");
        await Gm("bob", "!lvl 60 40"); await Gm("alice", "!lvl 60 40");
        await Task.Delay(300);
        var a = Session("alice"); var b = Session("bob"); a.NoRegenUntilMs = b.NoRegenUntilMs = long.MaxValue; // a recovery tick would change the sums
        var combat = _app.Services.GetRequiredService<CombatService>();
        long aliceHp = Character.Stats(a)[AttributeId.Hp], bobHp = Character.Stats(b)[AttributeId.Hp];
        await Loop.InvokeAsync(async () =>
        {
            await _app.Services.GetRequiredService<BuffService>().AddAsync(b, 1150401, 60000, a.Role!.Id);
            await combat.EnvironmentDamageAsync(b, 1000);
        });
        await Task.Delay(300);
        Assert.Equal(bobHp - 880, Character.Stats(b)[AttributeId.Hp]);
        Assert.Equal(aliceHp - 120, Character.Stats(a)[AttributeId.Hp]);
    }
}
