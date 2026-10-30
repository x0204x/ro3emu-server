using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// A monster's blow on another monster outside any player's cast: the siege's battering ram on the City Gate and an
/// archer on a siege ladder (TerritoryWarsService.SiegeUnits.cs). The gameplay decides when one strikes; this settles it.
/// </summary>
public sealed partial class CombatService
{
    /// <summary>
    /// With a normal attack (SkillConfig row of MonsterTemplate.NormalSkill) the cast shows first and the blow lands at
    /// the template's hit point, its damage from the shared formula; without one the blow lands at once for the
    /// attacker's ATK, as a monster's hit without a skill row on a companion. <paramref name="adjust"/> applies the
    /// gameplay's own rules (NPC tags); <paramref name="reach"/> is how far (m) the blow carries. Nobody is credited and
    /// nothing drops: the owner of the gameplay finds the death itself. True when the blow was struck (or scheduled).
    /// </summary>
    internal async Task<bool> MonsterStrikeAsync(MonsterUnit source, MonsterUnit target, float reach, Func<long, long> adjust)
    {
        if (!Strikes(source, target, reach)) return false;
        long castId = ++_sessionIds;
        source.Face = MonsterService.Angle(source.X, source.Z, target.X, target.Z);
        if (SkillConfig.Get(_data, source.Template.NormalSkill) is not { } skill)
        {
            await StrikeAsync(source, target, null, castId, adjust);
            return true;
        }
        await _world.ToUnitViewersAsync(source, UseInSkill, new UseInSkillInfo
        {
            SrcID = source.Uid, SkillID = source.Template.NormalSkill, TargetId = target.Uid, TargetPos = target.Pos,
            Face = source.Face, SessionId = castId, SkillMs = source.Template.SkillMs, StartTime = Now,
        });
        ScheduleRuntime(source.WorldScene, TimeSpan.FromSeconds(source.Template.HitPointSeconds), () =>
            Strikes(source, target, reach + 2f) ? StrikeAsync(source, target, skill, castId, adjust) : Task.CompletedTask);
        return true;
    }

    private static bool Strikes(MonsterUnit source, MonsterUnit target, float reach) =>
        !source.Dead && source.Hp > 0 && !target.Dead && target.Hp > 0 && source.InstanceId == target.InstanceId
        && source.WorldScene == target.WorldScene
        && MonsterService.Distance(source.X, source.Z, target.X, target.Z) <= reach;

    private async Task StrikeAsync(MonsterUnit source, MonsterUnit target, SkillConfig? skill, long castId, Func<long, long> adjust)
    {
        var (damage, result) = skill is { } row ? Damage(MonsterFighter(source), MonsterFighter(target), row.Row)
            : (Math.Max(1, source.Template.Atk), DamageHitResult.Normal);
        damage = Math.Max(0, adjust(MonsterBuffDamage(source, skill?.Row, ScaleMonsterDamage(damage, source.DamageRatio))));
        target.Hp = Math.Max(0, target.Hp - damage);
        bool lethal = target.Hp == 0;
        var hit = new SkillHitInfo
        {
            SrcID = source.Uid, SkillId = source.Template.NormalSkill,
            ShowPoint = skill is { } shown ? HitEffect(shown.ScriptShow) : DefaultHitEffect, SessionId = castId,
            SrcElement = source.Template.Element,
            Dsts = { new DamageInfo { DstID = target.Uid, Damage = damage, HitResult = result, DstElement = target.Template.Element,
                IsDead = lethal } },
        };
        foreach (var viewer in target.Viewers.Union(source.Viewers).ToArray()) await viewer.SendGameAsync(SkillHit, hit);
        if (!lethal)
        {
            await _world.ToUnitViewersAsync(target, AoiAttrChange, MonsterService.Hp(target));
            return;
        }
        await _monsters.KillAsync(target);
        await FinalizeLethalPublicationAsync(target);
    }
}
