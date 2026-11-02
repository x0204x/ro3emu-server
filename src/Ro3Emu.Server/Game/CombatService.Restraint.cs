namespace Ro3Emu.Server.Game;

/// <summary>
/// Player-against-player counters: Job Resonance (stats and damage), the totem type and the title level
/// (CombatRestraints.cs).
/// </summary>
public sealed partial class CombatService
{
    private JobResonance? _resonance;
    private TotemRestraint? _totemRestraint;
    private TitleDamage? _titleDamage;

    internal JobResonance Resonance => _resonance ??= new JobResonance(_data);
    internal TotemRestraint TotemRestraints => _totemRestraint ??= new TotemRestraint(_data);
    internal TitleDamage TitleCounter => _titleDamage ??= new TitleDamage(_data);

    /// <summary>The role's active Job Resonance id (<see cref="JobResonance.Inactive"/> for none).</summary>
    public int ResonanceOf(GameSession s) => s.Progress is { } p ? Resonance.ResonanceId(p) : JobResonance.Inactive;

    /// <summary>CharacterService.ExtraAttributes: the active resonance's JobResonanceConfig._kAttribute.</summary>
    private IEnumerable<(long Id, double Value)> ResonanceAttributes(GameSession s) => Resonance.Attributes(ResonanceOf(s));

    /// <summary>A player's hit on a player after the formula: resonance, totem and title counters (each per 10000).</summary>
    internal long ApplyPlayerRestraints(GameSession attacker, GameSession target, long damage)
    {
        if (damage <= 0 || attacker.Progress is not { } a || target.Progress is not { } t) return damage;
        damage = PlayerRestraints.Apply(Resonance, TotemRestraints, a, t, damage);
        return TitleCounter.Apply(_character.TitleOf?.Invoke(attacker) ?? 0, _character.TitleOf?.Invoke(target) ?? 0, damage);
    }
}
