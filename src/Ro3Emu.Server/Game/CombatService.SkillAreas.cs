using System.Globalization;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// What a skill reaches when its row's _kRange does not say it and its text does. A number the row holds stays the row's: Arm Cannon's
/// _iTargetMax 5 stands against its "1 selected ... and up to 5 Other", and Pile Bunker's [100,1,0,0] against its "within 4 meters".
/// </summary>
public sealed partial class CombatService
{
    private enum TextShape { AroundTarget, LineToTarget, FrontRectangle }

    private readonly record struct TextArea(TextShape Shape, double Size, double Width, int Count, bool Shuffled = false);

    /// <summary>(inferred) Focused Arrow Strike's row holds no width; Sharp Shooting's logic [3,150] does, as Arrow Flash's tuples name them.</summary>
    private const double ArrowLineWidth = 1.5;

    private static TextArea? AreaByText(JsonElement row)
    {
        var skill = new SkillConfig(row);
        string script = skill.ScriptLogic;
        var logic = skill.LogicData;
        double Desc(int i) => i < skill.DescData.Length && double.TryParse(skill.DescData[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
        int rowMax = (int)Math.Max(1, skill.TargetMax);
        if (script == "BlitzBeat") return new(TextShape.AroundTarget, Desc(1), 0, 1 + (int)Desc(2)); // "and up to ${3} Other enemies within ${2} meters of the target"
        if (script.StartsWith("CartRevolution", StringComparison.Ordinal)) return new(TextShape.AroundTarget, Desc(0), 0, 1 + (int)Desc(1)); // "up to ${2} other enemies within ${1} meters of them"
        if (script.StartsWith("ArmCannon", StringComparison.Ordinal)) return new(TextShape.AroundTarget, Desc(3), 0, rowMax); // "within ${4} meters of them"
        // "at a random enemy within ${4} meters of the target ... up to ${7} times": logic [4] is the target and those three, and Secondary Damage's "+1 bounce" edits it.
        if (script == "PoisonBlade" && logic is [var blades, ..]) return new(TextShape.AroundTarget, Desc(3), 0, (int)blades, Shuffled: true);
        if (script == "RuiLiSheJi" && logic is [var count, var width, ..]) return new(TextShape.LineToTarget, Desc(0), width / 100, 1 + (int)count); // "up to ${2} enemies in a line"
        if (script.StartsWith("AimedBolt", StringComparison.Ordinal)) return new(TextShape.LineToTarget, Desc(0), ArrowLineWidth, 1 + (int)Desc(1));
        if (script == "FlameLauncher") return new(TextShape.FrontRectangle, Desc(3), Desc(2), rowMax); // "up to ${5} targets in a ${3} * ${4}-meter rectangular area in front of you"
        return null;
    }

    /// <summary>Magnus [npc, targets, ..] and Sanctuary [targets]: "to up to ${6}", and a talent's "target count +1" edits this number. 0 for any other row.</summary>
    private static int FieldTargets(JsonElement row)
    {
        var skill = new SkillConfig(row);
        return skill.ScriptLogic switch
        {
            "Magnus_Exorcismus" or "Magnus_ExorcismusBD" when skill.LogicData is [_, var targets, ..] => (int)targets,
            "Sanctuary" when skill.LogicData is [var targets, ..] => (int)targets,
            _ => 0,
        };
    }

    /// <summary>"up to ${4} enemies within a ${3}m radius": a trap's row holds _iTargetMax 1.</summary>
    private static int TrapTargets(JsonElement row) => new SkillConfig(row).DescData is [_, _, _, var count, ..]
        && int.TryParse(count, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 1;

    /// <summary>Rolling Cutter, Evil Spirit Threat, Virus Spread and the like: _iRangeType 1 with a radius and several targets is an area, as their texts read.</summary>
    private static bool AreaDespiteRangeType(SkillConfig skill) =>
        skill.RangeType == SkillRelease.SingleTarget && skill.TargetMax > 1 && skill.Range is [> 0, ..];

    private List<MonsterUnit>? EnemiesByText(GameSession s, ActiveSkillCast action, Vec3 center)
    {
        var row = action.Row;
        if (AreaByText(row) is not { } area) return null;
        var aim = FindMonster(s.WorldScene, action.Request.TargetId) is { Hp: > 0 } locked && CanHitMonster(s, locked) ? locked : null;
        if (area.Shape != TextShape.FrontRectangle && aim == null && new SkillConfig(row).RangeType == SkillRelease.SingleTarget) return [];
        var around = aim?.Pos ?? center;
        float height = new SkillConfig(row).AreaHeightRange / 100f;
        float aimFace = aim == null ? action.Request.Face : MonsterService.Angle(action.Origin.X, action.Origin.Z, aim.X, aim.Z);
        bool Inside(MonsterUnit m)
        {
            if (height > 0 && MathF.Abs(m.Y - around.Y) > height) return false;
            if (area.Shape == TextShape.AroundTarget) return Distance(around, m.Pos) <= area.Size + 0.1f;
            var (forward, side) = FacingOffset(m.X - action.Origin.X, m.Z - action.Origin.Z, area.Shape == TextShape.LineToTarget ? aimFace : action.Request.Face);
            return Math.Abs(side) <= area.Width / 2 + .1 && forward >= -.5 && forward <= area.Size + .1;
        }
        var hit = _world.Map(s.WorldScene).Units.OfType<MonsterUnit>()
            .Where(m => m != aim && m.Hp > 0 && CanHitMonster(s, m) && Relation(row, false, race: m.Template.Race) && Inside(m));
        var others = area.Shuffled ? hit.OrderBy(_ => Rolls.Proc(10000)) : hit.OrderBy(m => Distance(around, m.Pos)).ThenBy(m => m.Uid);
        return [.. (aim != null ? others.Prepend(aim) : others).Take(Math.Max(1, area.Count))];
    }

    /// <summary>Adrenaline Rush: "If the target is a Blacksmith class, grants an Extra ...". Its enhanced row reads "regardless of Job Restriction".</summary>
    private const int BlacksmithOnlyBuffGroup = 16108;
    private static bool BlacksmithLine(int career) => career is 6200 or 6300 or 6400;
    private static bool JobAllowsBuff(string script, int buff, GameSession recipient) =>
        script != "AdrenalineRush" || buff / 100 != BlacksmithOnlyBuffGroup || BlacksmithLine(recipient.Role?.Career ?? 0);

    /// <summary>Venom Splasher: "Plants timed Dynamite ... After ${2} seconds, deals". The row marks the target with its buff, and the buff names the row that goes off.</summary>
    private async Task PlantDynamiteAsync(GameSession s, ActiveSkillCast action)
    {
        var skill = new SkillConfig(action.Row);
        if (FindMonster(s.WorldScene, action.Request.TargetId) is not { Hp: > 0 } target || !CanHitMonster(s, target)) return;
        await ApplyEffectsAsync(s, new ActiveSkillCast
        {
            Id = action.Id, OwnerId = action.OwnerId, WorldScene = action.WorldScene, SceneGeneration = action.SceneGeneration, Attachment = action.Attachment,
            Request = action.Request, Row = skill.WithDamage([], []), Origin = action.Origin, StartMs = action.StartMs, ChantMs = action.ChantMs, ActionMs = action.ActionMs,
            Operator = action.Operator,
        }, target.Pos.Clone(), chosenTargets: [target]);
        if (skill.Buff2 is not [[var buff, _, var fuse, ..], ..] || BuffConfig.Get(_data, buff) is not { } mark
            || mark.LogicData is not [var blast, ..] || SkillConfig.Get(_data, blast) is not { } explosion) return;
        ScheduleRuntime(action.WorldScene, TimeSpan.FromMilliseconds(fuse), () =>
        {
            if (s.Dead || s.Role?.Id != action.OwnerId || s.WorldScene != action.WorldScene || target.Hp <= 0) return Task.CompletedTask;
            var row = explosion.Row;
            var around = _world.Map(s.WorldScene).Units.OfType<MonsterUnit>()
                .Where(m => m.Hp > 0 && CanHitMonster(s, m) && Distance(target.Pos, m.Pos) <= explosion.Range.FirstOrDefault(300) / 100 + .1)
                .OrderBy(m => m == target ? 0 : 1).ThenBy(m => Distance(target.Pos, m.Pos)).Take((int)Math.Max(1, explosion.TargetMax)).ToList();
            return BlastAsync();
            async Task BlastAsync()
            {
                await CastExtraAsync(s, row, around);
                // "Inflicts Poisoned damage on the targets", its row's _kLogicData.
                if (explosion.LogicData is [var poison, ..] && SkillConfig.Get(_data, poison) is { } poisoned)
                    await CastExtraAsync(s, poisoned.Row, [.. around.Where(m => m.Hp > 0)]);
            }
        });
    }
}
