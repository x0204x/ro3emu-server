using System.Text.Json;
using System.Text.Json.Nodes;
using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    private static bool IsGuardianEffect(JsonElement row) => GameData.Int(row, "_iSystemType") == GuardianPetRules.EffectType
        || row.TryGetProperty("_ro3GuardianProc", out _);

    /// <summary>Whether a point is inside a skill row's area: monster selection for every skill, and a guardian's player
    /// targets. Guardian rows alone may carry the forward rectangle, inferred for Eve's final throw from its "ahead"
    /// description.
    /// (inferred) A missing _kRange is 3 m and a fan without an angle 90 degrees; a rectangle is centred on its anchor and,
    /// without a length, reaches its width ahead and behind.</summary>
    private static bool SkillAreaContains(JsonElement row, Vec3 origin, Vec3 requested, float face, Vec3 point)
    {
        int release = (int)GameData.Int(row, "_iRelease");
        Vec3 center = release is SkillRelease.Lock or SkillRelease.DragRect or SkillRelease.DragCircle ? requested : origin;
        float height = GameData.Int(row, "_iAreaHeightRange") / 100f;
        if (height > 0 && MathF.Abs(point.Y - center.Y) > height) return false;
        var range = Numbers(row, "_kRange");
        double radius = range.FirstOrDefault(300) / 100;
        var (forward, side) = FacingOffset(point.X - center.X, point.Z - center.Z, face);
        if (row.TryGetProperty("_ro3GuardianForwardRectangle", out _))
            return Math.Abs(side) <= radius / 2 && forward >= 0 && forward <= range.ElementAtOrDefault(1) / 100;
        if (release is SkillRelease.FixRect or SkillRelease.Arrow or SkillRelease.Rect360 or SkillRelease.DragRect)
            return Math.Abs(side) <= radius / 2 && Math.Abs(forward) <= (range.Length > 1 ? range[1] / 200 : radius);
        return Distance(center, point) <= radius + .1 && (release != SkillRelease.Fan
            || Math.Atan2(Math.Abs(side), forward) <= (range.Length > 1 ? range[1] : 90) * Math.PI / 360);
    }

    private GameSession[] GuardianPlayerTargets(GameSession source, ActiveSkillCast cast, Vec3 center) => [.. _sessions.All()
        .Where(p => CanPlayerHit(source, p) && SkillAreaContains(cast.Row, cast.Origin, center, cast.Request.Face, p.Position))];

    private long GuardianOwlTarget(GameSession source, GuardianPetRules.Skill pet, ActiveSkillCast cast)
    {
        var desc = Numbers(pet.Active, "_kDescData");
        if (desc.Length < 4) return 0;
        Vec3 center = FindMonster(source.WorldScene, cast.Request.TargetId)?.Pos
            ?? EventTarget(source, cast.Request.TargetId)?.Position ?? cast.Request.TargetPos ?? cast.Origin;
        return GuardianRandomEnemy(source, pet, cast, center, desc[3]);
    }

    private long GuardianRandomEnemy(GameSession source, GuardianPetRules.Skill pet, ActiveSkillCast cast, Vec3 center, double radius)
    {
        var node = GuardianPetRules.CopyObject(pet.Effect);
        node["_iRangeType"] = SkillRelease.Area; node["_iRelease"] = SkillRelease.DragCircle; node["_iTargetMax"] = int.MaxValue;
        node["_kRange"] = new JsonArray(radius * 100);
        var area = GuardianProcAction(source, pet, cast, JsonSerializer.SerializeToElement(node));
        var targets = SelectEnemies(source, area, center).Select(m => m.Uid)
            .Concat(GuardianPlayerTargets(source, area, center).Select(p => p.Role!.Id)).Order().ToArray();
        return targets.Length > 0 ? targets[Rolls.Proc(targets.Length)] : 0;
    }
}
