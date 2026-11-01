using Romsg;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
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
