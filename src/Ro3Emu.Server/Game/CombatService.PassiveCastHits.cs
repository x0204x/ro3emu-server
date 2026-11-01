using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

internal sealed record PublishedPlayerCastHit(GameSession Target, long TargetId, long EffectiveDamage,
    DamageHitResult Result, Func<bool> Current);

public sealed partial class CombatService
{
    private static void RecordPassivePlayerHit(ActiveSkillCast cast, GameSession target, SkillHitInfo hit, long effective)
    {
        if (cast.Request.IsExtraSkill || target.Role is not { } role) return;
        var dst = hit.Dsts.FirstOrDefault(d => d.DstID == role.Id);
        if (dst == null) return;
        cast.PublishedPlayerHits.Add(new(target, role.Id, effective, dst.HitResult, CapturePlayerSkillTarget(target)));
    }

    internal bool PassiveCastCurrent(GameSession source, ActiveSkillCast cast) => CurrentCastMap(source, cast)
        && ReferenceEquals(source.SkillProtocol, cast.PassiveSourceEpoch)
        && _sessions.All().Contains(source);

    internal Task CastPassiveExtraOnPlayerAsync(GameSession source, JsonElement row, GameSession target,
        Func<bool> current, bool parametersApplied) => CastExtraOnPlayerAsync(source,
            new SkillConfig(parametersApplied ? row : ExtraRow(source, row)), target, sourceEligible: current);
}
