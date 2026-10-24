using System.Globalization;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Hiding and detection.
/// <list type="bullet">
/// <item>BuffConfig _kBuffCategory 31 hides its holder: Disguise ("While [Cloaking] is active, you cannot be seen or
/// targeted"), Enhance Cloaking, a guardian's Stealth and the monsters' Hiding. Ruwach's hit row 1400601 names the same
/// category in its _kLogicData ("Removes their [Disguise] Status Effect").</item>
/// <item>Category 109 detects within the radius of its _kLogicData[0] in cm: Steady Focus 350, Detecting 500 ("The Falcon
/// reveals enemies using Hiding within a ${3}-meter radius"), Keen Senses 1200. Sight (BuffConfig 12012 "Reveals hidden
/// targets within a @{1}-meter radius") has no _kLogicData: its skill's _kRange 350, the description's 3.5 m. Sight
/// Sprout: "You Discover hidden enemy targets within a ${1}-meter radius", its _kDescData[0] in metres (inferred: always,
/// while learned). A guardian's Scout detection keeps its own row's radius.</item>
/// <item>The client hides the unit itself (LC_UnitStealthManager, LC_Display_Agent.UnitStealthChanged): an enemy with a
/// Stealth attribute is hidden and an ally shown translucent, unless it stands within the local player's ShowStealth1
/// radius. The server refuses what the client would not offer: a hostile single target or a monster's aggro on a
/// hidden player its viewer does not detect. Area hits still land, and taking or dealing damage reveals
/// (StealthEffects).</item>
/// </list>
/// </summary>
public sealed partial class CombatService
{
    internal const int StealthCategory = 31, DetectionCategory = 109;
    private const int SightGroup = 12012, SightSkill = 1200201, SightSprout = 12110;

    internal static bool StealthBuff(BuffConfig row) => row.BuffCategory.Contains(StealthCategory);

    /// <summary>A player holding a live category 31 buff.</summary>
    internal bool Stealthed(GameSession player) => player.Buffs.Any(b => b.ExpireMs > Now
        && BuffConfig.Get(_data, b.ConfigId) is { } row && StealthBuff(row));

    /// <summary>The widest radius (cm) within which the player detects hidden units; 0 when none.</summary>
    internal int DetectionRadiusCm(GameSession player)
    {
        long radius = 0;
        foreach (var buff in player.Buffs)
        {
            if (buff.ExpireMs <= Now || BuffConfig.Get(_data, buff.ConfigId) is not { } row) continue;
            if (row.BuffCategory.Contains(DetectionCategory))
                radius = Math.Max(radius, DetectionLogic(player, row.LogicData.FirstOrDefault()));
            else if (buff.IsGuardianOwned && row.BuffId == GuardianGroup.ScoutDetection)
                radius = Math.Max(radius, row.LogicData.FirstOrDefault());
            else if (row.BuffId == SightGroup && SkillConfig.Get(_data, SightSkill) is { } sight)
                radius = Math.Max(radius, (long)sight.Range.FirstOrDefault());
        }
        if (LearnedPassive(player, SightSprout) is { DescData: [var metres, ..] }
            && double.TryParse(metres, NumberStyles.Float, CultureInfo.InvariantCulture, out double m))
            radius = Math.Max(radius, (long)Math.Round(m * 100));
        return (int)radius;
    }

    /// <summary>A detection row's radius after the talents' scope-1 BuffLogicData tuples on its category: Scouting's
    /// "[Detect Stealth] range +${1} meters" is ["1", "109", "4", "1", "300", "0"].</summary>
    private static long DetectionLogic(GameSession player, long radius)
    {
        double value = radius;
        foreach (var tuple in player.TalentBuffAttributes)
            if (tuple is { ByCategory: true, Group: DetectionCategory, Type: 4, Index: 0 })
                value = tuple.Operation switch { 1 => value + tuple.Value, 2 => tuple.Value, 5 => value * (1 + tuple.Value / 10000), _ => value };
        return (long)value;
    }

    /// <summary>Whether the viewer may single out the subject: itself, a visible unit, an ally, or a hidden enemy within
    /// the viewer's detection radius.</summary>
    internal bool CanSee(GameSession viewer, GameSession subject) => viewer == subject || !Stealthed(subject)
        || !CanPlayerHit(viewer, subject) && !CanPlayerHit(subject, viewer)
        || Distance(viewer.Position, subject.Position) * 100 <= DetectionRadiusCm(viewer);

    /// <summary>Players the source may hit (events, territory wars).</summary>
    internal bool Hostile(GameSession source, GameSession target) => CanPlayerHit(source, target);

    /// <summary>An extra hit of a SkillConfig row on one hostile player, through the ordinary player hit path.</summary>
    internal Task CastExtraOnPlayerAsync(GameSession source, SkillConfig row, GameSession target) =>
        HitEventPlayersAsync(source, [target], row.Row, (int)row.Id, (int)row.ScriptShow,
            ++_sessionIds, () => source.Role != null && !source.Dead);
}
