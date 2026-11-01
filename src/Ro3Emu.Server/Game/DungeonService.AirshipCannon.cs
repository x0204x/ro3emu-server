using Romsg;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class DungeonService
{
    private const int AirshipCannonFireSkill = 5353001;
    private const int AirshipCannonImpactEffect = 6120103, AirshipCannonPopupEffect = 6032010;
    private const int AirshipImpactPoint = 4, AirshipImpactDamage = 2;

    /// <summary>
    /// G 31119/50942–50944 等十二組艦船命中：依序播放 6120103/type4、6120103/type2、6032010/type2。
    /// 另有一組命中炮彈 NPC，不計入三艦的命中次數。
    /// 6032010 指向原生 fx_ylmatarlively_pop-up_01.prefab，封包沒有九百多萬傷害欄位。
    /// 同次錄製沒有對三艦發 31101／31129；不可再疊加通用飄字或把特效數字當真實扣血。
    /// 僅替代本副本已授權、已真正結算的單艦命中，空瞄與其他戰鬥沿用既有處理。
    /// </summary>
    private async Task<bool> PresentAirshipCannonHitAsync(GameSession member, ActiveSkillCast cast,
        IReadOnlyList<MonsterUnit> targets, SkillHitInfo hit, IReadOnlyList<GameSession> viewers)
    {
        if (Active(member) is not { } attempt || attempt.Plan.SceneEntry != AirshipScene || attempt.Wave != 1
            || cast.Request.SkillId != AirshipCannonFireSkill || cast.Request.IsExtraSkill
            || cast.OwnerId != member.Role?.Id || cast.WorldScene != member.WorldScene
            || targets.Count != 1 || hit.Dsts.Count != 1) return false;
        var target = targets[0];
        var result = hit.Dsts[0];
        if (target.InstanceId != attempt.Room.Id || !attempt.Room.Monsters.Contains(target)
            || target.Template.NpcId is not (5300015 or 5300053) || result.DstID != target.Uid
            || result.DamageType != DamageType.Damage || result.HitResult == DamageHitResult.Miss
            || result.Damage <= 0) return false;
        var point = cast.Request.TargetPos ?? target.Pos;
        foreach (var (effect, type) in new[] { (AirshipCannonImpactEffect, AirshipImpactPoint),
            (AirshipCannonImpactEffect, AirshipImpactDamage), (AirshipCannonPopupEffect, AirshipImpactDamage) })
        {
            var notification = new SkillEffectDisplayNotify
            {
                EffectId = effect, EffectType = type, TargetPos = point.Clone(),
                MasterId = cast.OwnerId, SkillId = AirshipCannonFireSkill,
            };
            foreach (var viewer in viewers)
                await viewer.SendGameAsync(CombatService.SkillEffect, notification);
        }
        return true;
    }
}
