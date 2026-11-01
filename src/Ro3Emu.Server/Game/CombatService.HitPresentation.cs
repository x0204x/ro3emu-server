using Romsg;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    /// <summary>場景砲台可保留已驗證的 TPS 瞄點；一般指定目標技能仍使用單位位置。</summary>
    internal List<Func<GameSession, CastSkillReq, bool>> PreserveTargetAim { get; } = [];
    internal List<Func<GameSession, CastSkillReq, int>> TargetAimErrors { get; } = [];
    /// <summary>場景已驗證的單一 TPS 目標，不再以根點落在一般範圍技內重新選敵。</summary>
    internal List<Func<GameSession, ActiveSkillCast, bool>> LockedTargetCasts { get; } = [];
    /// <summary>場景敵人允許哪些施放影響；拒絕時連同傷害、附帶 Buff 與位移一起排除。</summary>
    internal List<Func<GameSession, ActiveSkillCast, MonsterUnit, bool>> CastTargetAllowed { get; } = [];

    /// <summary>場景專用命中規則；null 沿用一般戰鬥。於實際扣血前套用，保留死亡交易。</summary>
    internal List<Func<GameSession, ActiveSkillCast, MonsterUnit, DamageHitResult,
        (long Amount, DamageHitResult Result)?>> CastDamageOverrides
    { get; } = [];

    /// <summary>已結算命中的場景專用呈現；回傳 true 只替代 31101／31129，仍保留 HP、擊殺與獎勵處理。</summary>
    internal List<Func<GameSession, ActiveSkillCast, IReadOnlyList<MonsterUnit>, SkillHitInfo,
        IReadOnlyList<GameSession>, Task<bool>>> CustomHitPresentations
    { get; } = [];

    private async Task<bool> TryPresentCustomHitAsync(GameSession caster, ActiveSkillCast action,
        IReadOnlyList<MonsterUnit> targets, SkillHitInfo hit, IReadOnlyList<GameSession> viewers)
    {
        foreach (var present in CustomHitPresentations)
            if (await present(caster, action, targets, hit, viewers)) return true;
        return false;
    }
}
