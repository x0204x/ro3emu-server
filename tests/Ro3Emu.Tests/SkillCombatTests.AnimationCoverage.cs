using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataFact]
    public void EveryExportedTimelineUsesTheProductionActionHitAndSeparateBulletSelectors()
    {
        var data = _app.Services.GetRequiredService<GameData>();
        var combat = _app.Services.GetRequiredService<CombatService>();
        var hitSelector = typeof(CombatService).GetMethod("HitEffect", BindingFlags.Instance | BindingFlags.NonPublic);
        var bulletSelector = typeof(CombatService).GetMethod("BulletEffect", BindingFlags.Instance | BindingFlags.NonPublic);
        var bulletTravel = typeof(CombatService).GetMethod("BulletTravelMs", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(hitSelector);
        Assert.NotNull(bulletSelector);
        Assert.NotNull(bulletTravel);
        var effects = data.SkillTemplateTable("Lua_Config_EffectData");

        var timelines = SkillConfig.All(data)
            .Where(row => row.BasicType == 1 && row.ScriptShow > 0 && data.SkillTemplate(row.ScriptShow) is not null)
            .Select(row => row.ScriptShow).Distinct().ToArray();
        Assert.NotEmpty(timelines);

        foreach (long scriptShow in timelines)
        {
            var authored = DamageEffects(scriptShow, effects).ToArray();
            var firstDamage = authored.FirstOrDefault(effect => effect.Damage is not null);
            var action = authored.FirstOrDefault(effect => effect.Damage is { DamageType: 3, HasActionDamage: true });
            var projectile = authored.FirstOrDefault(effect => effect.Damage is
            { DamageType: 2, Bullet: { } value } && !string.IsNullOrWhiteSpace(value.Name));
            int expectedHit = (int)(action.Damage is not null ? action.Id
                : projectile.Damage is not null ? projectile.Id
                : firstDamage.Damage is not null ? firstDamage.Id : 1000002);
            int expectedBullet = (int)(projectile.Damage is not null ? projectile.Id : 0);

            int selectedHit = Assert.IsType<int>(hitSelector!.Invoke(combat, [scriptShow]));
            int selectedBullet = Assert.IsType<int>(bulletSelector!.Invoke(combat, [scriptShow]));
            Assert.Equal(expectedHit, selectedHit);
            Assert.Equal(expectedBullet, selectedBullet);

            if (action.Damage is not null && projectile.Damage is not null)
            {
                Assert.Equal((int)action.Id, selectedHit);
                Assert.Equal((int)projectile.Id, selectedBullet);
                Assert.NotEqual(selectedHit, selectedBullet);
            }

            if (selectedBullet > 0)
            {
                int travelMs = Assert.IsType<int>(bulletTravel!.Invoke(combat,
                    [selectedBullet, new Vec3(), new Vec3 { X = 4 }, false]));
                Assert.True(travelMs >= 0, $"show {scriptShow}: negative Bullet travel {travelMs}");
            }
        }
    }

    private static IEnumerable<(long Id, SkillEffectDamage? Damage)> DamageEffects(
        long scriptShow, IReadOnlyDictionary<string, System.Text.Json.JsonElement> effects)
    {
        for (long id = scriptShow * 100; id < scriptShow * 100 + 100; id++)
            if (effects.TryGetValue(id.ToString(), out var effect))
                yield return (id, new SkillEffectData(effect).Damage);
    }
}
