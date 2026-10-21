namespace Ro3Emu.Server.Game;

/// <summary>Player-against-player counters: Job Resonance (stats and damage) and the totem type (CombatRestraints.cs).</summary>
public sealed partial class CombatService
{
    private JobResonance? _resonance;
    private TotemRestraint? _totemRestraint;

    internal JobResonance Resonance => _resonance ??= new JobResonance(_data);
    internal TotemRestraint TotemRestraints => _totemRestraint ??= new TotemRestraint(_data);

    /// <summary>The role's active Job Resonance id (<see cref="JobResonance.Inactive"/> for none).</summary>
    public int ResonanceOf(GameSession s) => s.Progress is { } p ? Resonance.ResonanceId(p) : JobResonance.Inactive;

    /// <summary>CharacterService.ExtraAttributes: the active resonance's JobResonanceConfig._kAttribute.</summary>
    private IEnumerable<(long Id, double Value)> ResonanceAttributes(GameSession s) => Resonance.Attributes(ResonanceOf(s));

    /// <summary>A player's hit on a player after the formula: resonance counter, then totem counter (both per 10000).</summary>
    internal long ApplyPlayerRestraints(GameSession attacker, GameSession target, long damage)
    {
        if (damage <= 0 || attacker.Progress is not { } a || target.Progress is not { } t) return damage;
        return PlayerRestraints.Apply(Resonance, TotemRestraints, a, t, damage);
    }
}
