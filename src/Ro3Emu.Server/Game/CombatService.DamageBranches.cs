using System.Text.Json;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Which pair of _kDamageParam1 and _kDamageParam2 a hit takes. The client does not compute a hit and its config names no
/// condition, so each one is read from the skill's own text, where the second value is the first with that text's bonus:
/// Shield Press [12000,24000] "Against Monsters, the percentage DMG multiplier increases by 100%", Soul Strike [7000,10500]
/// "an Extra 50% ... to Undead enemies", Holy Cross [5100,5355] "with a two-handed spear ... an Extra 5%", Magnus Exorcismus
/// [500,650] "increased by 30%". (inferred) Ambush [4200,8400] "If cast while Hiding, deals 84% Extra" takes 84%, as its
/// second value holds it and the others' second values are totals. A row whose text names no condition stays on its first.
/// </summary>
public sealed partial class CombatService
{
    private const int DemonRace = 3, UndeadRace = 5, ShadowElement = 8, UndeadElement = 10;
    private const int SanctuaryNpc = 22000003, GravitationFieldNpc = 1200013;
    private static readonly long[] TwoHandedSpear = [5];

    /// <summary><paramref name="monster"/> is null for a player target: the rows' monster bonus does not reach one.</summary>
    internal int DamageBranch(GameSession s, MonsterUnit? monster, JsonElement row)
    {
        string script = Script(row);
        bool hasSecond = new SkillConfig(row).DamageParam1.Length > 1;
        return script switch
        {
            _ when !hasSecond => 0,
            "ShieldPress" or "ShieldPresss1" or "ShieldSpell" or "EarthDrive" or "Smite3" or "ProtectionAttack" or "ProtectionAttack2"
                => monster != null ? 1 : 0,
            "Soul_Strike" => monster?.Template is { Race: UndeadRace } or { Element: UndeadElement } ? 1 : 0,
            "Magnus_Exorcismus" or "Magnus_ExorcismusBD"
                => monster?.Template is { Race: UndeadRace or DemonRace } or { Element: ShadowElement or UndeadElement } ? 1 : 0,
            "HolyCross" => TwoHandedSpear.Any(w => SkillParameterRules.Wields(s, _data, w)) ? 1 : 0,
            "Ambush" => Stealthed(s) ? 1 : 0,
            _ => 0,
        };
    }
}
