namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    /// <summary>AttributeConfig 207/208 and 1207/1208: independent healing done/received inputs. Callers pass the
    /// guardian attributes (<see cref="BuffService.GuardianAttributes"/>), not the full stat sheets.
    /// (inferred) Multiply the two additive groups, floor once, then clamp to missing HP.</summary>
    internal static long ModifiedHealing(StatSheet source, StatSheet target, long amount) =>
        (long)Math.Clamp(Math.Floor(amount * Math.Max(0, 1 + (source[AttributeId.PerTreat] + source[AttributeId.PerTreat1]) / 10000.0)
            * Math.Max(0, 1 + (target[AttributeId.PerCure] + target[AttributeId.PerCure1]) / 10000.0)), 0, long.MaxValue);

    /// <summary>AttributeConfig sp_dedution/per_sp_dedution (1231/1232); Marionette increases the latter.
    /// (inferred) Apply the flat then percent change once to the summed SP cost, rounding up.</summary>
    internal static long ModifiedSpCost(StatSheet stats, long amount) => amount <= 0 ? 0 :
        (long)Math.Clamp(Math.Ceiling(Math.Max(0, amount + stats[AttributeId.SpDedution]) * Math.Max(0, 1 + stats[AttributeId.PerSpDedution] / 10000.0)), 0, long.MaxValue);
}
