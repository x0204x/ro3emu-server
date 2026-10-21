using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private static readonly int[] NormalAndSpecialModes = [1, 0];
    private static readonly int[] DefenceZeroCoefficient = [0];
    private static readonly int[] DefenceSmallFlatDamage = [10];

    private sealed class GuardianStableDamageRolls : CombatRolls
    {
        public override double Hit() => 0;
        public override double Crit() => 1;
        public override double DamageFloat() => 0.5;
    }

    [ClientDataFact]
    public async Task GuardianSoheeReducesMonsterNormalDamageButNotSpecialDamage()
    {
        await using var alice = await InWorld("Alice");
        var player = Session("alice");
        var enemy = await Target("alice", stationary: true);
        var combat = _app.Services.GetRequiredService<CombatService>();
        var buffs = _app.Services.GetRequiredService<BuffService>();
        combat.Rolls = new GuardianStableDamageRolls();
        await Loop.InvokeAsync(async () =>
        {
            // The real BuffConfig payload supplies the percentage; invented attacks isolate its scope.
            await buffs.AddGuardianAsync(player, 2313401, 10000, player.Role!.Id);
            foreach (int normal in NormalAndSpecialModes)
            {
                await Character.SetHpSpAsync(player, long.MaxValue, null);
                var row = JsonSerializer.SerializeToElement(new
                {
                    _iID = 998850 + normal, _iSkillHit = 10000, _iDmgType = 1, _iIsNormalAtk = normal,
                    _kDamageParam1 = DefenceZeroCoefficient, _kDamageParam2 = DefenceSmallFlatDamage,
                });
                await combat.MonsterSkillHitAsync(enemy, row, [player], enemy.Uid);
            }
        });
        var normalHit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 998851);
        var specialHit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 998850);
        long ordinaryDamage = Assert.Single(specialHit.Dsts).Damage;
        Assert.True(ordinaryDamage > 0);
        Assert.Equal((long)Math.Floor(ordinaryDamage * 0.87), Assert.Single(normalHit.Dsts).Damage);
    }

    [ClientDataFact]
    public async Task GuardianBlindLimitsMonsterSelectionAndClearingRestoresIt()
    {
        await using var alice = await InWorld("Alice");
        var player = Session("alice");
        var enemy = await Target("alice", stationary: true);
        var buffs = _app.Services.GetRequiredService<BuffService>();
        var monsters = _app.Services.GetRequiredService<MonsterService>();
        await Loop.InvokeAsync(async () =>
        {
            enemy.X = player.Position.X + 6; enemy.Z = player.Position.Z;
            Assert.True(monsters.TargetVisible?.Invoke(enemy, player));
            await buffs.AddGuardianAsync(enemy, 902501, 10000, player.Role!.Id);
            Assert.False(monsters.TargetVisible?.Invoke(enemy, player));
            enemy.X = player.Position.X + 4;
            Assert.True(monsters.TargetVisible?.Invoke(enemy, player));
            enemy.X = player.Position.X + 6;
            await buffs.ClearUnitAsync(enemy);
            Assert.True(monsters.TargetVisible?.Invoke(enemy, player));
        });
    }

    [ClientDataFact]
    public async Task GuardianMarionetteChangesTheMonsterChantUsedByItsScheduler()
    {
        await using var alice = await InWorld("Alice");
        var player = Session("alice");
        var monsters = _app.Services.GetRequiredService<MonsterService>();
        var buffs = _app.Services.GetRequiredService<BuffService>();
        var combat = _app.Services.GetRequiredService<CombatService>();
        await Loop.InvokeAsync(async () =>
        {
            var template = monsters.Template(5000001) ?? throw new InvalidOperationException("Missing exported target");
            var enemy = await monsters.SpawnSingleAsync(player.RuntimeMap, 5000001,
                new MapPoint(player.Position.X, player.Position.Y, player.Position.Z, 0),
                customTemplate: template with { CastSpeed = 300, RoamDistance = 0 })
                ?? throw new InvalidOperationException("Cannot create test target");
            var skill = new BossSkill
            {
                Id = 998852, ChantMs = 2000,
                Row = JsonSerializer.SerializeToElement(new { _iChantVar = 2000, _iChantFix = 0 }),
            };
            int before = monsters.BossChant(enemy, skill);
            await buffs.AddGuardianAsync(enemy, 2339101, 10000, player.Role!.Id);
            Assert.Equal(275, combat.MonsterStats(enemy)[116]);
            Assert.True(monsters.BossChant(enemy, skill) > before);
            await buffs.ClearUnitAsync(enemy);
            Assert.Equal(before, monsters.BossChant(enemy, skill));
        });
    }
}
