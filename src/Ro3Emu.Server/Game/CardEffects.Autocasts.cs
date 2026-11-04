using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class CardEffects
{
    private const int BashProc = 20190, ThunderProc = 20200, DoubleAttackProc = 20210;

    // CastResolved is sent once for the player's own cast, after publication, and never for CastExtraAsync.
    private void InitializeAutocasts() => _combat.CastResolved.Add(AutocastOnCastResolvedAsync);

    private Task AutocastOnCastResolvedAsync(GameSession source, ActiveSkillCast action,
        IReadOnlyList<MonsterUnit> processed, SkillHitInfo hit)
    {
        if (source.Role == null || source.Dead || RuntimeFor(source).ResolvingAutocast) return Task.CompletedTask;
        var passives = Passives(source).ToList();
        return passives.Count == 0 ? Task.CompletedTask
            : AutocastsAsync(source, action, new SkillConfig(action.Row), processed, hit, passives);
    }
}
