using Romsg;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    /// <summary>
    /// G 的兩種特殊技能分別在施放後 500／300 ms 送 31112，位移時間皆為 250 ms。
    /// 終點採既有同伴接敵方式推定，保留甲板高度；不將場景地面導航套到空中甲板。
    /// </summary>
    private void ScheduleAirshipSupportMove(CompanionUnit companion, MonsterUnit target, CompanionUnit.Skill skill)
    {
        if (companion.SceneEntry != 1000501) return;
        var rule = (companion.NpcId, skill.Id) switch
        {
            (5300086, 5353901) => (Effect: 5213506, Delay: 500),
            (5300087, 5354001) => (Effect: 4320902, Delay: 300),
            _ => (Effect: 0, Delay: 0),
        };
        if (rule.Effect == 0 || MovementEffectById(_data, rule.Effect) is not { Id: > 0 } move) return;
        float face = companion.Face;
        ScheduleRuntime(companion.WorldScene, TimeSpan.FromMilliseconds(rule.Delay),
            () => Reaches(companion, target, skill, 2f)
                ? MoveAirshipSupportAsync(companion, target, skill, move, face) : Task.CompletedTask);
    }

    private async Task MoveAirshipSupportAsync(CompanionUnit companion, MonsterUnit target, CompanionUnit.Skill skill,
        (int Id, int Ms) move, float face)
    {
        float dx = target.X - companion.X, dz = target.Z - companion.Z;
        float distance = MathF.Sqrt(dx * dx + dz * dz);
        float reach = companion.Spec.Skills.Where(s => s.Id != skill.Id).Select(s => s.Range).DefaultIfEmpty(skill.Range).Min();
        float go = Math.Max(0, distance - reach * .8f);
        if (go <= .1f && skill.Id != 5354001 || distance <= 0) return;
        var start = companion.Pos;
        var end = new Vec3 { X = start.X + dx / distance * go, Y = start.Y, Z = start.Z + dz / distance * go };
        if (skill.Id == 5354001)
        {
            // G 七次均沿施放朝向前進六公尺；距離取該技能配置，不套到其他同伴。
            double radians = face * Math.PI / 180;
            end.X = start.X + (float)Math.Sin(radians) * skill.Range;
            end.Z = start.Z + (float)Math.Cos(radians) * skill.Range;
        }
        companion.X = end.X; companion.Z = end.Z;
        _world.Map(companion.WorldScene).Moved(companion);
        await _world.ToUnitViewersAsync(companion, SkillMove, new SkillStartMoveNotify
        {
            EntityID = (ulong)companion.Uid, StartPos = start, EndPos = end, SkillConfigId = skill.Id,
            EffectId = move.Id, MoveTimeMs = move.Ms, MoveType = 1, Face = face, StartTimestampMs = Now,
        });
    }
}
