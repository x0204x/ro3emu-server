using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Xunit.Abstractions;

namespace Ro3Emu.Tests;

/// <summary>
/// Data-driven coverage for every active skill timeline exported from the client. This is deliberately one table scan,
/// rather than one slow server fixture per skill: the wire tests prove the normal/high route implementations, while
/// this contract proves that every exported row belongs to one route and has a deterministic animation packet plan.
/// </summary>
public sealed class SkillAnimationRouteCoverageTests(ITestOutputHelper output)
{
    private static readonly GameData Data = new(
        Options.Create(new ServerOptions { DataDir = Path.Combine(ClientData.RepoRoot, "data") }),
        NullLogger<GameData>.Instance);

    [ClientDataFact]
    public void ExportedCastRoutesHaveDeterministicTimingAndAnimationPacketPlans()
    {
        // CombatService.Casting rejects _iBasicType != 1 as SkillPassive, and SkillService uses the same field for
        // hotbar-active skills. ScriptShow 0 is therefore still active, but has no independently exported timeline;
        // keep that scope visible as UNKNOWN instead of pretending it was scanned.
        var active = SkillConfig.All(Data).Where(row => row.BasicType == 1).ToArray();
        var noTimeline = active.Where(row => row.ScriptShow <= 0)
            .GroupBy(row => (row.SkillId, row.ScriptShow)).ToArray();
        var groups = active.Where(row => row.ScriptShow > 0)
            .GroupBy(row => (row.SkillId, row.ScriptShow))
            .ToArray();
        var playerSkillGroups = PlayerSkillGroups();
        var playerWithoutTimeline = noTimeline.Where(group => playerSkillGroups.Contains(group.Key.SkillId)).ToArray();
        var effects = Data.SkillTemplateTable("Lua_Config_EffectData");
        var failures = new List<string>();
        var missingTemplateGroups = new List<string>();
        int playerMissingTemplates = 0;
        int normal = 0, high = 0, templates = 0, missingTemplates = 0, effectReferences = 0;
        int actionDamage = 0, bullet = 0, separated = 0, clientBullet = 0, serverBullet = 0;
        int castHitPlans = 0, castBulletHitPlans = 0;

        Assert.NotEmpty(groups);
        foreach (var group in playerWithoutTimeline)
            failures.Add($"player skill {group.Key.SkillId}: active row has no ScriptShow timeline");
        foreach (var group in groups)
        {
            var rows = group.ToArray();
            var (skillId, scriptShow) = group.Key;
            bool highLevel = rows[0].SkillEffectLevel > 0;
            if (rows.Any(row => row.SkillEffectLevel > 0 != highLevel))
                failures.Add($"skill {skillId}, show {scriptShow}: levels disagree on normal/high cast route");
            if (highLevel) high++; else normal++;

            // Player StartCastAsync sends PlayAct=true. At neutral attack speed SkillMs is max(1, ActionTime), so every
            // authored row must yield a positive animation window even when its action time is zero. Monster/boss
            // PlayAct propagation is an independent wire-route regression covered by SkillCombatTests.
            foreach (var row in rows)
            {
                if (row.ActionTime < 0)
                    failures.Add($"skill {row.Id}: negative action time {row.ActionTime}");
                int neutralSkillMs = (int)Math.Max(1, row.ActionTime);
                if (neutralSkillMs <= 0)
                    failures.Add($"skill {row.Id}: PlayAct has no positive SkillMs");
            }

            if (Data.SkillTemplate(scriptShow) is not { } template)
            {
                missingTemplates++;
                missingTemplateGroups.Add($"{skillId}:{scriptShow}");
                // SkillTreeConfig and JobAttackConfig are direct player-cast evidence. Server-internal pet/tower/raid
                // rows without an exported template stay an explicit UNKNOWN instead of becoming an invented failure.
                if (playerSkillGroups.Contains(skillId))
                {
                    playerMissingTemplates++;
                    failures.Add($"player skill {skillId}, show {scriptShow}: missing display template");
                }
                continue;
            }
            templates++;

            var stateTimes = StateTimes(template);
            if (stateTimes.Length < 3 || stateTimes.Zip(stateTimes.Skip(1)).Any(pair => pair.First > pair.Second))
                failures.Add($"skill {skillId}, show {scriptShow}: malformed timeline state times");

            foreach (long effectId in EffectReferences(template).Distinct())
            {
                effectReferences++;
                if (!effects.ContainsKey(effectId.ToString()))
                    failures.Add($"skill {skillId}, show {scriptShow}: missing EffectData {effectId}");
            }

            var authored = DamageEffects(scriptShow, effects).ToArray();
            var action = authored.FirstOrDefault(effect => effect.Damage is { DamageType: 3, HasActionDamage: true });
            var projectile = authored.FirstOrDefault(effect => effect.Damage is
            { DamageType: 2, Bullet: { } value } && !string.IsNullOrWhiteSpace(value.Name));
            bool hasAction = action.Damage is not null, hasBullet = projectile.Damage is not null;
            if (hasAction) actionDamage++;
            if (hasBullet) bullet++;

            // A projectile effect starts travel; a distinct ActionDamage effect is the target hit. Mixing the two was
            // the source of the missing/odd fire and Double Strafe presentation, so keep the split exhaustive.
            if (hasAction && hasBullet)
            {
                separated++;
                if (action.Id == projectile.Id)
                    failures.Add($"skill {skillId}, show {scriptShow}: Bullet and ActionDamage share {action.Id}");
            }

            bool hasClientPoints = new SkillTimelineData(template).HasClientPoints;
            if (hasBullet && hasClientPoints) clientBullet++;
            int travelMs = hasBullet && !hasClientPoints ? ReferenceTravelMs(projectile.Damage!.Value.Bullet!.Value) : 0;
            double point = AttackPoints(template).FirstOrDefault(0.2);
            foreach (var row in rows)
            {
                int skillMs = (int)Math.Max(1, row.ActionTime);
                int impactMs = CombatService.ImpactOffset(template, point, chantMs: 0, actionMs: skillMs);
                if (impactMs < 0)
                    failures.Add($"skill {row.Id}: negative impact offset {impactMs}");
                if (travelMs > 0)
                {
                    serverBullet++;
                    castBulletHitPlans++;
                }
                else castHitPlans++;
            }
        }

        Assert.True(normal > 0 && high > 0, "client data must exercise both normal and high-level cast routes");
        Assert.True(actionDamage > 0 && bullet > 0 && separated > 0,
            "client data must exercise ActionDamage, Bullet and their separated route");
        Assert.Equal(groups.Length, templates + missingTemplates);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));

        output.WriteLine(
            $"active groups={groups.Length + noTimeline.Length}; timeline groups={groups.Length}; " +
            $"no ScriptShow (UNKNOWN)={noTimeline.Length}; player no ScriptShow={playerWithoutTimeline.Length}; " +
            $"normal={normal}; high={high}; templates={templates}; missing templates={missingTemplates}; " +
            $"player missing templates={playerMissingTemplates}; EffectData references={effectReferences}");
        if (missingTemplateGroups.Count > 0)
            output.WriteLine($"missing template groups (UNKNOWN unless player-authored): {string.Join(", ", missingTemplateGroups)}");
        output.WriteLine(
            $"ActionDamage={actionDamage}; Bullet={bullet}; separated={separated}; client bullet={clientBullet}; " +
            $"server bullet rows={serverBullet}; Cast->Hit rows={castHitPlans}; " +
            $"Cast->Bullet->Hit rows={castBulletHitPlans}");
    }

    private static HashSet<long> PlayerSkillGroups()
    {
        var result = Data.Table("SkillTreeConfig").Values.SelectMany(SkillTreeGroups).ToHashSet();
        foreach (var attack in JobAttackConfig.All(Data))
            if (SkillConfig.Get(Data, attack.SkillId) is { } row) result.Add(row.SkillId);
        return result;
    }

    private static IEnumerable<long> SkillTreeGroups(JsonElement row) => row.EnumerateObject()
        .Where(field => field.Name.StartsWith("_iX", StringComparison.Ordinal)
            && field.Value.ValueKind == JsonValueKind.Number && field.Value.TryGetInt64(out _))
        .Select(field => field.Value.GetInt64()).Where(id => id > 0);

    private static IEnumerable<(long Id, SkillEffectDamage? Damage)> DamageEffects(
        long scriptShow, IReadOnlyDictionary<string, JsonElement> effects)
    {
        for (long id = scriptShow * 100; id < scriptShow * 100 + 100; id++)
            if (effects.TryGetValue(id.ToString(), out var effect))
                yield return (id, new SkillEffectData(effect).Damage);
    }

    private static IEnumerable<long> EffectReferences(JsonElement template)
    {
        if (!template.TryGetProperty("m_kShows", out var shows) || shows.ValueKind != JsonValueKind.Object) yield break;
        foreach (var point in shows.EnumerateObject())
            if (point.Value.ValueKind == JsonValueKind.Array)
                foreach (var reference in point.Value.EnumerateArray())
                    if (reference.ValueKind == JsonValueKind.Array && reference.GetArrayLength() >= 1
                        && reference[0].TryGetInt64(out long id) && id > 0)
                        yield return id;
    }

    private static double[] StateTimes(JsonElement template)
    {
        if (!template.TryGetProperty("m_kState", out var state)
            || !state.TryGetProperty("Time", out var times) || times.ValueKind != JsonValueKind.Array) return [];
        return times.EnumerateArray().Select(value => value.GetDouble()).ToArray();
    }

    private static IEnumerable<double> AttackPoints(JsonElement template)
    {
        if (!template.TryGetProperty("m_kAtkPoints", out var points) || points.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var point in points.EnumerateArray())
            if (point.TryGetDouble(out double value)) yield return value;
    }

    private static int ReferenceTravelMs(SkillBulletData bullet)
    {
        double duration = bullet.ControlType == 0
            ? bullet.LifeTime
            : bullet.Speed > 0 ? 4.0 / bullet.Speed * 1000 : 0;
        return duration > 0 ? (int)Math.Clamp(Math.Round(duration), 1, 5000) : 0;
    }
}
