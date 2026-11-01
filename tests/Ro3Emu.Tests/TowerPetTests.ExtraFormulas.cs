using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class TowerPetTests
{
    private sealed class FormulaPetRolls : CombatRolls
    {
        public override double Hit() => 0;
        public override double Crit() => 1;
        public override double DamageFloat() => 0.5;
    }

    [ClientDataTheory]
    [InlineData(1, 2308401, 1000, 2000)]
    [InlineData(1, 2308401, 1_000_000, 2000)]
    [InlineData(6, 2308406, 1000, 4000)]
    [InlineData(6, 2308406, 1_000_000, 4000)]
    public async Task TowerPetHealthFormulaUsesAuthoredStarRowAndMagicAttackCap(int star, int skillId, long maximum, int coefficient)
    {
        await using var bot = await InWorld();
        await Eligible();
        Assert.Equal(0, (await Set(bot, 10001, 11001, 0, 0)).Result);
        var attempt = await Enter(bot);
        var owner = Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);
        var data = Service<GameData>(); var combat = Service<CombatService>();
        combat.Rolls = new FormulaPetRolls();
        var roster = new DBPetInfo();
        roster.ListPet[31003] = new PetData { PetId = 31003, Level = 1, Star = star, StarProgress = 0 };
        var pet = TowerPetUnit.Create(data, roster, owner, Service<World>(), Service<MonsterService>(), combat,
            TowerPetUnit.UidBase + 9000, 31003, attempt.InstanceId, owner.Position.X, owner.Position.Y, owner.Position.Z)
            ?? throw new InvalidOperationException("Paired pet 31003 is required.");
        Assert.Contains(pet.AttackSkills, s => s.Id == skillId);
        Assert.True(pet.Matk > 0);
        MonsterUnit? target = null;
        await Service<GameLoop>().InvokeAsync(async () =>
        {
            foreach (var old in attempt.Pets) old.Dead = true;
            attempt.Pets.Clear(); attempt.Pets.Add(pet);
            foreach (var enemy in attempt.Monsters) enemy.Dead = true;
            var source = attempt.Monsters[0];
            target = new MonsterUnit
            {
                Uid = (1L << 49) + 9000, SceneEntry = pet.SceneEntry, InstanceId = attempt.InstanceId,
                RuntimeMap = pet.RuntimeMap, X = pet.X + 1, Y = pet.Y, Z = pet.Z,
                Hp = maximum, Template = source.Template with { MaxHp = maximum, Def = 0, Mdef = 0, Element = 1, Skills = null },
                Group = source.Group, Home = source.Home, StaysOutOfBattle = true, KeepsDamage = true,
            };
            await Service<World>().AddUnitAsync(target);
            await combat.TowerPetAttackAsync(owner, pet, target, skillId);
        });
        Assert.NotNull(target);
        var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SrcID == pet.Uid && h.SkillId == skillId);
        long expected = (long)Math.Round(Math.Min(maximum * coefficient / 10000d, pet.Matk * 15d));
        Assert.Equal(expected, Assert.Single(hit.Dsts).Damage);
        Assert.Equal(maximum - expected, target.Hp);
        Assert.True(maximum == 1000 ? expected < pet.Matk * 15 : expected == pet.Matk * 15);
        var buff = Assert.Single(Service<BuffService>().UnitBuffs(target), b => b.ConfigId == 2308500 + star);
        Assert.Equal(pet.Uid, buff.SourceId);
        Assert.Equal(5000, buff.DurationMs);
        Assert.Contains(Service<BuffService>().UnitAttributes(target), p => p.Id == 301 && p.Value == -(star == 1 ? 500 : 1000));
    }
}
