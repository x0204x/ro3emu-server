using System.Runtime.CompilerServices;
using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class DungeonService
{
    private const int AirshipCannonFireSkill = 5353001;
    private const int AirshipCannonImpactEffect = 6120103, AirshipCannonPopupEffect = 6032010;
    private const int AirshipImpactPoint = 4, AirshipImpactDamage = 2;
    private const int AirshipCannonHitsToSink = 5;
    private readonly ConditionalWeakTable<ActiveSkillCast, HashSet<long>> _airshipCannonResolved = new();
    private sealed record AirshipCannonAuthorization(long Room, long Target, Vec3 Aim);
    private readonly ConditionalWeakTable<ActiveSkillCast, AirshipCannonAuthorization> _airshipCannonAccepted = new();

    private int AirshipCannonAimError(GameSession member, CastSkillReq request) =>
        member.SceneEntry == AirshipScene && request.SkillId == AirshipCannonFireSkill
        && request.TargetPos is not null && !ValidAirshipAim(member, request) ? ErrorCodes.SkillTargetInvalid : 0;

    private bool PreserveAirshipCannonAim(GameSession member, CastSkillReq request) =>
        Active(member) is { Plan.SceneEntry: AirshipScene } attempt && attempt.Wave == 1
        && request.SkillId == AirshipCannonFireSkill && !request.IsExtraSkill
        && attempt.CannonUsers.ContainsKey(member) && ValidAirshipAim(member, request);

    private bool AirshipCannonTargetAllowed(GameSession member, ActiveSkillCast cast, MonsterUnit target) =>
        Active(member) is not { Plan.SceneEntry: AirshipScene, Wave: 1 } attempt
        || target.InstanceId != attempt.Room.Id || target.Template.NpcId is not (5300015 or 5300053)
        || cast.Request.TargetId == target.Uid && AcceptedAirshipCannonCast(member, cast);

    private bool AcceptedAirshipCannonCast(GameSession member, ActiveSkillCast cast) =>
        cast.Request.SkillId == AirshipCannonFireSkill && !cast.Request.IsExtraSkill
        && Active(member) is { } attempt && _airshipCannonAccepted.TryGetValue(cast, out var accepted)
        && accepted.Room == attempt.Room.Id && accepted.Target == cast.Request.TargetId
        && accepted.Aim.Equals(cast.Request.TargetPos);

    private bool ValidAirshipAim(GameSession member, CastSkillReq request)
    {
        if (request.TargetPos is not { } point || !float.IsFinite(point.X) || !float.IsFinite(point.Y)
            || !float.IsFinite(point.Z) || !_buffs.CanSeePosition(member, point)
            || SkillConfig.Get(_data, AirshipCannonFireSkill) is not { } config) return false;
        double dx = (double)point.X - member.Position.X, dy = (double)point.Y - member.Position.Y,
            dz = (double)point.Z - member.Position.Z;
        double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        return distance >= config.DistanceMin / 100f && distance <= config.DistanceMax / 100f + .5f;
    }

    /// <summary>
    /// 使用者指定每艘五次有效砲擊；這是任務規則，不宣稱解出原服 HP 公式，也不採用特效九百萬。
    /// 僅綁定本房已登砲的單艦施放；普通技能、額外效果及同一施放的重複回呼不能扣艦血。
    /// </summary>
    private (long Amount, DamageHitResult Result)? AirshipCannonDamage(GameSession member, ActiveSkillCast cast,
        MonsterUnit target, DamageHitResult result)
    {
        if (Active(member) is not { } attempt || attempt.Plan.SceneEntry != AirshipScene || attempt.Wave != 1
            || target.InstanceId != attempt.Room.Id || !attempt.Room.Monsters.Contains(target)
            || target.Template.NpcId is not (5300015 or 5300053)) return null;
        if (cast.Request.SkillId != AirshipCannonFireSkill || cast.Request.IsExtraSkill || cast.PositionOnly
            || cast.OwnerId != member.Role?.Id || cast.WorldScene != member.WorldScene
            || cast.Request.TargetId != target.Uid || !AcceptedAirshipCannonCast(member, cast)
            || !_airshipCannonResolved.GetOrCreateValue(cast).Add(target.Uid)) return (0, result);
        // TPS 已繫結合法目標，不能再因玩家命中率／攻擊力／暴擊變成不同擊沉次數。
        return (target.Template.MaxHp / AirshipCannonHitsToSink
            + (target.Template.MaxHp % AirshipCannonHitsToSink == 0 ? 0 : 1), DamageHitResult.Normal);
    }

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
