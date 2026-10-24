using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Profession talents whose Buff(2) row carries a proc: BuffConfig._kLogicData names the SkillConfig row (or the
/// BuffConfig row) the proc applies, and the talent's SpecialEffectConfig._kDescData holds its chance, cooldown and area
/// in the order its description uses them. Which cast triggers a proc, and which description value is which, is written
/// only in the description, so each rule below quotes it (inferred). The proc row's own _kDamageParam1/2, _kShowDmg and
/// _kScriptLogic carry its damage, and the proc is an extra cast through the ordinary effect path.
/// <see cref="WhileActive"/> lists the talents whose _kLogicData buff applies while another buff lives ("While [Aura
/// Blade] is active ..."); SeasonTalentService applies those. A description's skill links (&lt;link="skill/1411801"&gt;)
/// name the same triggers the rules list. Rules exist for every job line's damage, buff and recast procs; the Thief and
/// Merchant lines have no proc rows, so their hits come from the descriptions. The base passives the talents build on
/// (Double Attack, Deadly Poison's Venom, Advanced Katar Mastery) are in TalentProcs.Passives, counters and next-cast
/// charges in TalentProcs.Charges, and the reactions to damage taken, kills, Gravitation Field's end, heals and the count of enemies
/// hit in TalentProcs.Reactions. Falcon's Might (op 20) is SeasonTalentService's, and Scouting (a scope-1 BuffLogicData
/// tuple on the detection category) is CombatService.Stealth's.
/// </summary>
public sealed partial class TalentProcs
{
    // SkillConfig._iSkillID of the triggering casts, named by SkillConfig._iName.
    private const int Pierce = 11120, BowlingBash = 11104, HolyCross = 11411, GrandCross = 11412, ShieldBoomerang = 11418,
        GloriaDomini = 11501, RapidSmiting = 11504,
        TurnUndead = 14118, MagnusExorcismus = 14109,
        FireBolt = 12004, LightningBolt = 12005, ColdBolt = 12006, FireBall = 12012, EarthSpike = 12100, JupitelThunder = 12102,
        FrostNova = 12103, MeteorStorm = 12106, StormGust = 12108, GravitationField = 12212,
        ArrowAttack = 13000, DoubleStrafe = 13003, ArrowShower = 13004, BlitzBeat = 13102, FalconAssault = 13202,
        SonicBlow = 15102, VirusSpread = 15106, Grimtooth = 15109, SoulDestroyer = 15210, MeteorAssault = 15229,
        CartRevolution = 16006, HammerFall = 16104, HighSpeedCartRam = 16202, Smite = 11409, Basilica = 14206;
    // The client's element enum (Lua_GameDefine): None 1, Wind 2, Earth 3, Fire 4, Water 5, Poison 6 ...
    private const long Neutral = 1, Fire = 4, Poison = 6;
    private const long Stun = 901601; // BuffConfig "Stun" (Momentum-Breaking Hammer's _kLogicData)
    // BuffConfig._iBuffID of the skills' own buffs, named by BuffConfig._iName.
    private const long AuraBlade = 11201, Parry = 11210, AutoGuard = 11403, Sacrifice = 11501, BattleChant = 11505,
        MysticalAmplification = 12201;
    // AttributeConfig id: Cast Speed 116; MATK as Mystical Amplification's own _kChangeAttri has it.
    private const long CastSpeed = 116, AmplifiedMatk = 1043;

    /// <summary>
    /// Talents whose buff carries no data but whose description names what a buff of the owner adds: "[Mystical
    /// Amplification] grants ${1} Extra MATK." Keyed by the talent's BuffConfig id: the buff group and its added attributes
    /// with the description value index. They join every buff of that group the owner applies (inferred).
    /// </summary>
    internal static readonly IReadOnlyDictionary<long, (long Group, (long Attr, int Value)[] Pairs)> BuffBonuses =
        new Dictionary<long, (long, (long, int)[])>
        {
            [1249201] = (MysticalAmplification, [(CastSpeed, 0)]), // Magic Strike Calibration: "${1} Extra Cast Speed."
            [1249301] = (MysticalAmplification, [(AmplifiedMatk, 0)]), // Magic Infusion: "${1} Extra MATK."
        };

    /// <summary>
    /// Skill buffs named after a talent ("Crazy Uproar - Mind and Body Renewal"): the skill's _kBuff1 lists them
    /// unconditionally, so the caster's talent gates them (inferred from the names). Keyed by the BuffConfig id: the
    /// talent's BuffConfig id, and whether the buff goes to the caster only ("Weapon Polishing: [Maximize Power] increases
    /// your PATK") or to the others only ("Weapon Enhance: ... increases nearby allies' PATK").
    /// </summary>
    private static readonly Dictionary<long, (long Talent, bool? Caster)> TalentBuffs = new()
    {
        [1601001] = (1643201, null), // Crazy Uproar - Mind and Body Renewal
        [1601101] = (1643301, null), // Crazy Uproar - Self-Motivation
        [1611001] = (1647201, true), // Maximize Power - Weapon Polishing
        [1611101] = (1647301, false), // Maximize Power - Weapon Enhance
        [1621001] = (1649201, null), // Maximum Over Thrust - Enhanced Power Thrust
        [1621101] = (1649301, null), // Maximum Over Thrust - Critical Strike
    };
    private const long TwoHandedSword = 3, Mace = 8; // ItemConfig._iSubType
    private const int BuffAttrType = 2; // EBuffAffectValueType.BuffAttr

    /// <summary>
    /// A proc rule. Indexes are into the talent's _kDescData; null means none (a sure proc, no cooldown).
    /// <paramref name="Radius"/>/<paramref name="Count"/>: the proc hits the target and up to Count - 1 more monsters
    /// within Radius meters of it; <paramref name="Others"/> instead counts the monsters besides the target, and
    /// <paramref name="OthersOnly"/> leaves the target out. <paramref name="RowArea"/> takes the area from the proc row
    /// (_kRange, _iTargetMax); <paramref name="AroundCaster"/> centres the area on the caster; <paramref name="HitTargets"/>
    /// takes the monsters the cast hit (up to Count). <paramref name="Logic"/> indexes _kLogicData for the proc's SkillConfig
    /// row (or skill group, or BuffConfig row); <paramref name="Recast"/> casts the triggering row again. <paramref name="Buff"/>:
    /// the BuffConfig row goes on the caster (<paramref name="TargetBuff"/>: on the target) for the _kLogicData value after
    /// it in ms, or <paramref name="BuffSeconds"/> of the description. <paramref name="Active"/>: a buff group that must live.
    /// </summary>
    /// <remarks><paramref name="Ratio"/>/<paramref name="Flat"/>: no proc row exists, the description gives the hit ("PATK *
    /// ${4}% + ${5}"), dealt as the triggering row with that damage (SkillConfig.AsDescribedHit) of <paramref name="Element"/>,
    /// <paramref name="Hits"/> times; <paramref name="HpBelow"/>: only on a target below that HP percentage;
    /// <paramref name="RandomHit"/> centres the area on a random monster the cast hit (without an area: hits that monster);
    /// <paramref name="Status"/> lands on each monster the described hit struck with the <paramref name="StatusChance"/>% of
    /// the description for its <paramref name="StatusSeconds"/>.</remarks>
    private sealed record Rule(int[] Groups, bool NormalAttack = false, long Weapon = 0, int? Chance = null, int? Cooldown = null,
        int? Radius = null, int? Count = null, int? Others = null, bool OthersOnly = false, bool RowArea = false, bool Buff = false,
        long Active = 0, bool AroundCaster = false, int Logic = 0, bool Recast = false, bool TargetBuff = false, int? BuffSeconds = null,
        bool HitTargets = false, int? Ratio = null, int? Flat = null, int? Hits = null, long Element = Neutral, int? HpBelow = null,
        bool RandomHit = false, long Status = 0, int? StatusChance = null, int? StatusSeconds = null, int ResetCooldown = 0);

    /// <summary>Talent BuffConfig id -> the buff group that must live for its _kLogicData buff to apply.</summary>
    internal static readonly IReadOnlyDictionary<long, long> WhileActive = new Dictionary<long, long>
    {
        [1123301] = AuraBlade, // Aura Blade Enhance: "While [Aura Blade] is active, ... Normal Attack-type skills ..."
        [1123501] = AuraBlade, // Aura Insight - Swiftness: "While [Aura Blade] is active, ASPD increases by ${1}% ..."
        [1123701] = AuraBlade, // Aura Insight - Technique: "While [Aura Blade] is active, [Bash]'s ..."
        [1153701] = AutoGuard, // Auto Guard Enhance: "While [Auto Guard] is active, PATK +${1} and Max HP +${2}."
        [1153901] = AutoGuard, // Strong Shield: "While [Auto Guard] is active, Smite and Rapid Smiting's ..."
        [1155001] = BattleChant, // Battle Cry: "While [Battle Chant] is active, your [Shield Boomerang]'s ..."
        [1154801] = BattleChant, // Fortifying Cry: "While [Battle Chant] is active, your Max HP increases by ${1}."
        [1153501] = Sacrifice, // Martyr's Reckoning Enhance: "While [Sacrifice] is active, [Basic Attack]'s ..."
    };

    /// <summary>Keyed by the talent's BuffConfig id (its SpecialEffectConfig op [2, buff]).</summary>
    private static readonly Dictionary<long, Rule> Rules = new()
    {
        // Rapid Pierce: "When casting [Pierce], there is a ${1}% Chance to Deals DMG to the target an Extra @{1} times,
        // dealing a total of PATK * ${2}% + ${3} ... Cooldown: ${4} seconds."
        [1112101] = new([Pierce], Chance: 0, Cooldown: 3),
        // Piercing Blast: "When casting [Pierce], there is a ${1}% Chance to trigger an Extra Shatter, which Deals DMG ...
        // to the target and ${3} Other enemies within ${2} meters. Cooldown: ${6} seconds."
        [1112201] = new([Pierce], Chance: 0, Cooldown: 5, Radius: 1, Others: 2),
        // Double Counter: "When casting [Bowling Bash], there is a ${1}% Chance to cast it one Extra time."
        [1112301] = new([BowlingBash], Chance: 0, RowArea: true),
        // Sword Attack Speed Increase: "When a Two-Handed Sword is equipped, casting [Basic Attack] has a ${1}% Chance to
        // increase ASPD by ${2}% for ${3} seconds. Cooldown: ${4} seconds."
        [1112401] = new([], NormalAttack: true, Weapon: TwoHandedSword, Chance: 0, Cooldown: 3, Buff: true),
        // Power Slash: "Casting [Basic Attack] triggers Slash, which Deals DMG ... to the target. Internal Cooldown: ${3} seconds."
        [1112601] = new([], NormalAttack: true, Cooldown: 2),
        // Heavy Blow: "Using [Basic Attack] triggers Splash damage, dealing ... to ${3} other enemies within ${2} meters of
        // the target."
        [1112701] = new([], NormalAttack: true, Radius: 1, Others: 2, OthersOnly: true),
        // Cross Blast: "When [Holy Cross] hits an enemy, it additionally casts [Cross Blast] on them, dealing ... to up to
        // ${2} enemies within ${1} meters of the target. Internal Cooldown: ${5} seconds."
        [1151201] = new([HolyCross], Cooldown: 4, Radius: 0, Count: 1),
        // Absolute Judex: "[Grand Cross] and [Holy Cross] additionally cast [Absolute Judex] on the target, dealing ... to
        // the target and a total of ${2} enemies within ${1} meters. Internal Cooldown: ${5} seconds."
        [1151401] = new([GrandCross, HolyCross], Cooldown: 4, Radius: 0, Count: 1),
        // Shield Boomerang Combo: "[Shield Boomerang] deals an Extra @{1} hits of ... to the locked-on target."
        [1151501] = new([ShieldBoomerang]),
        // Shield Bash Echo: "When casting [Rapid Smiting], there is a ${1}% Chance to change the Cooldown of your next Shield
        // Boomerang to @{1} seconds. This effect cannot stack and can trigger up to @{2} times every ${2} seconds." Its effect
        // description passes @{1} 0 and @{2} 1, and its buff is named "Rapid Smiting Resets Shield Boomerang CD": the running
        // cooldown ends (inferred).
        [1155401] = new([RapidSmiting], Chance: 0, Cooldown: 1, ResetCooldown: ShieldBoomerang),
        // Triple Slash: "While [Parry] is active, casting [Basic Attack] triggers Triple Slash, which Deals DMG to the
        // target over @{1} hits ... Cooldown: ${3} seconds."
        [1123901] = new([], NormalAttack: true, Active: Parry, Cooldown: 2),
        // Greatsword Sweep Attack: "While [Parry] is active, casting [Basic Attack] triggers Sweep, which Deals DMG ... to
        // up to ${2} enemies within ${1} meters of you. Cooldown: ${5} seconds."
        [1124001] = new([], NormalAttack: true, Active: Parry, Cooldown: 4, Radius: 0, Count: 1, AroundCaster: true),
        // Double Pressure: "When casting [Gloria Domini], Deals DMG ... to the target and up to ${2} enemies within ${1}
        // meters of the target."
        [1151601] = new([GloriaDomini], Radius: 0, Others: 1),
        // Divine Punishment: "[Gloria Domini] Deals DMG to the locked target an Extra @{1} times ..."
        [1151701] = new([GloriaDomini]),
        // Shield Pursuit: "[Rapid Smiting] has a ${1}% Chance to Deals DMG to the target an Extra @{1} times ..."
        [1155301] = new([RapidSmiting], Chance: 0),
        // Concussive Shield Bash: "When [Smite] is cast, it additionally deals PATK*${3}%+${4} Neutral melee Physical damage
        // to a total of ${2} Other enemies within ${1} meters of the target. Internal Cooldown: ${5} seconds." (Its
        // _kLogicData buff switches Smite to SmiteBd, whose second damage pair is that 20% + 50.)
        [1153001] = new([Smite], Radius: 0, Others: 1, OthersOnly: true, Ratio: 2, Flat: 3, Cooldown: 4),
        // Cross Thrust: "When [Grand Cross] is cast, triggers [Cross Thrust] on @{1} random targets within the area"; the
        // runtime count is read as one.
        [1151301] = new([GrandCross], RandomHit: true),

        // Priest line: _kLogicData is [chance in basis points, proc row]; the description's chance, when it has one, is
        // the same value.
        // Holy Light Resurrection: "When [Turn Undead] hits an enemy, it deals ${1} Extra hit(s) ... Internal Cooldown: ${4} sec."
        [1421101] = new([TurnUndead], Logic: 1, Cooldown: 3),
        // Radiant Resurrection: "[Turn Undead] ... dealing ... to the enemy and up to ${2} enemies within a ${1}-meter
        // radius. Internal Cooldown: ${5} seconds."
        [1421201] = new([TurnUndead], Logic: 1, Radius: 0, Others: 1, Cooldown: 4),
        // Holy Light Strike: "When [Basic Attack] hits an enemy, it triggers Holy Light Strike ... Internal Cooldown: ${4} seconds."
        [1421601] = new([], NormalAttack: true, Logic: 1, Cooldown: 3),
        // Holy Cross Judgment: "When [Magnus Exorcismus] is cast, it also unleashes ... to up to ${1} enemies within the area."
        [1422401] = new([MagnusExorcismus], Logic: 1, HitTargets: true, Count: 0),
        // Fated Resurrection: "After casting [Turn Undead], there is a ${1}% Chance to trigger Turn Undead one Extra time.
        // Internal Cooldown: ${2} seconds."
        [1422201] = new([TurnUndead], Chance: 0, Cooldown: 1, Recast: true),
        // Momentum-Breaking Hammer: "When using [Basic Attack] with a Mace, inflicts [Stun] on the target for ${1} seconds.
        // Internal Cooldown: ${2} seconds." (_kLogicData [Stun 901601, 1000 ms])
        [1422601] = new([], NormalAttack: true, Weapon: Mace, Cooldown: 1, Buff: true, TargetBuff: true),
        // Holy Light Bludgeon: "When using [Basic Attack] with a Mace, ... to the enemy and up to ${2} enemies within a
        // ${1}-meter radius. Internal Cooldown: ${5} seconds."
        [1422701] = new([], NormalAttack: true, Weapon: Mace, Logic: 1, Radius: 0, Others: 1, Cooldown: 4),
        // Divine Judgment, Shrined Judex: "While [Basilica] is active, [Holy Light] and [Turn Undead]'s percentage damage
        // multipliers increase ..."; _kLogicData [Basilica [Triggered Effect], 60000 ms] (its "Cast Completion Detection").
        [1423401] = new([Basilica], Buff: true),
        [1423601] = new([Basilica], Buff: true),

        // Mage line (the descriptions are the _kOtherDesc buffs').
        // Elemental Pursuit: "After [Fire Bolt], [Cold Bolt], [Lightning Bolt], or [Earth Spike] hits, it triggers
        // [Elemental Pursuit] @{1} times ... to the target. Cooldown: ${3} seconds."
        [1240301] = new([FireBolt, ColdBolt, LightningBolt, EarthSpike], Cooldown: 2),
        // Elemental Shock: "... to up to ${2} enemies within a ${1}-meter radius. Cooldown: ${5} seconds."
        [1240401] = new([FireBolt, ColdBolt, LightningBolt, EarthSpike], Radius: 0, Count: 1, Cooldown: 4),
        // Flame Chain: "After [Fire Ball] hits the primary target, it has a ${1}% Chance to trigger [Fire Bolt] Lv. ${2} @{1} times."
        [1242201] = new([FireBall], Chance: 0),
        // Fireball Burst: "After [Fire Ball] hits the primary target, it deals ... to up to ${2} enemies within a ${1}-meter radius."
        [1242301] = new([FireBall], Radius: 0, Count: 1),
        // Ice Spike: "[Frost Nova] ... triggers [Ice Spike] @{1} times on all targets hit ..."
        [1243201] = new([FrostNova], HitTargets: true),
        // Thunderbolt: "When [Jupitel Thunder] hits, it triggers [Thunderbolt] ... to the target."
        [1243301] = new([JupitelThunder]),
        // Flame Burst: "After using [Meteor Storm], immediately triggers [Fire Explosion] ... to up to ${2} enemies within
        // a ${1}-meter radius."
        [1244201] = new([MeteorStorm], Radius: 0, Count: 1),
        // Frost Burst: "After using [Storm Gust], immediately triggers [Frost Explosion] ... to up to ${2} enemies within
        // a ${1}-meter radius ..."
        [1245201] = new([StormGust], Radius: 0, Count: 1),
        // Spatial Distortion: "After using [Gravitation Field], trigger [Spatial Distortion] ... to up to ${2} enemies
        // within a ${1}-meter radius ..."
        [1248201] = new([GravitationField], Radius: 0, Count: 1),

        // Archer line: _kLogicData is [chance in basis points, proc row] (Lightning Blitz: [chance, buff]).
        // Rapid Combo: "When [Arrow Attack] hits an enemy, it deals ${1} Extra hits ... Internal Cooldown: ${4} seconds."
        [1321101] = new([ArrowAttack], Logic: 1, Cooldown: 3),
        // Chain Lightning: "When [Arrow Attack] hits an enemy, it deals ... to up to ${2} Other enemies within a ${1}-meter
        // radius of the target. Internal Cooldown: ${5} seconds."
        [1321201] = new([ArrowAttack], Logic: 1, Radius: 0, Others: 1, OthersOnly: true, Cooldown: 4),
        // Triple Strafe: "When [Double Strafe] hits an enemy, it deals ${1} Extra hits ... Internal Cooldown: ${4} seconds."
        [1321401] = new([DoubleStrafe], Logic: 1, Cooldown: 3),
        // Explosive Arrow: "When [Double Strafe] hits an enemy, it has a ${1}% Chance to deal ... to up to ${3} enemies
        // within a ${2}-meter radius of the target. Internal Cooldown: ${6} seconds."
        [1321501] = new([DoubleStrafe], Logic: 1, Chance: 0, Radius: 1, Count: 2, Cooldown: 5),
        // Falcon Strike: "When [Blitz Beat] hits an enemy, it has a ${1}% Chance to Summon the Falcon ... Internal Cooldown: ${4} seconds."
        [1321701] = new([BlitzBeat], Logic: 1, Chance: 0, Cooldown: 3),
        // Meteor Arrow Shower: "When [Arrow Shower] hits enemies, it deals ... to one of them. Internal Cooldown: ${3} seconds."
        [1322001] = new([ArrowShower], Logic: 1, Cooldown: 2),
        // Lightning Blitz: "After casting [Blitz Beat], ASPD increases by ${1}% for ${2} seconds. Internal Cooldown: ${3} seconds."
        [1322401] = new([BlitzBeat], Logic: 1, Buff: true, BuffSeconds: 1, Cooldown: 2),
        // Double Assault: "Casts [Falcon Assault] ${1} times. Internal Cooldown: ${2} seconds."
        [1323501] = new([FalconAssault], Recast: true, Cooldown: 1),
        // Falcon Rush: "When [Falcon Assault] hits an enemy, it deals ... to up to ${2} enemies within a ${1}-meter radius of
        // the target. Internal Cooldown: ${5} seconds."
        [1323601] = new([FalconAssault], Logic: 1, Radius: 0, Count: 1, Cooldown: 4),

        // Thief line: no proc row; the description gives the hit ("Neutral Melee Physical" unless named).
        // Blade Dance: "After casting [Basic Attack], there is a ${1}% Chance to Deals DMG equal to PATK * ${4}% + ${5} ...
        // to up to ${3} enemies within a ${2}-meter radius of you. Cooldown: ${6} seconds."
        [1522301] = new([], NormalAttack: true, Chance: 0, Radius: 1, Count: 2, AroundCaster: true, Ratio: 3, Flat: 4, Cooldown: 5),
        // Opening: "After casting [Basic Attack], there is a ${1}% Chance to Deals DMG to the enemy an Extra ${2} times, each
        // dealing PATK * ${3}% + ${4} ... Cooldown: ${5} seconds."
        [1522401] = new([], NormalAttack: true, Chance: 0, Hits: 1, Ratio: 2, Flat: 3, Cooldown: 4),
        // Chain Reaction: "After casting [Sonic Blow], there is a ${1}% Chance to Deals DMG equal to PATK * ${4}% + ${5} ...
        // to up to ${3} enemies within a ${2}-meter radius of you. Cooldown: ${6} seconds."
        [1522601] = new([SonicBlow], Chance: 0, Radius: 1, Count: 2, AroundCaster: true, Ratio: 3, Flat: 4, Cooldown: 5),
        // Shadow Fang: "After casting [Grimtooth], there is a ${1}% Chance to Deals DMG equal to PATK * ${4}% + ${5} ... to
        // up to ${3} enemies within a ${2}-meter radius of the target. Cooldown: ${6} seconds."
        [1523201] = new([Grimtooth], Chance: 0, Radius: 1, Count: 2, Ratio: 3, Flat: 4, Cooldown: 5),
        // Poisoned to the Core: "After casting [Virus Spread], deals Extra Neutral melee Physical damage equal to PATK *
        // ${1}% + ${2} to a single enemy."
        [1524101] = new([VirusSpread], Ratio: 0, Flat: 1),
        // Toxic Rampage: "After casting [Virus Spread], deals Poison melee Physical damage equal to PATK * ${3}% + ${4} to
        // up to ${2} enemies within a ${1}-meter radius of a random target hit."
        [1524201] = new([VirusSpread], Radius: 0, Count: 1, RandomHit: true, Ratio: 2, Flat: 3, Element: Poison),
        // Spreading Shockwave: "[Soul Destroyer] ... After casting, deals Neutral melee Physical damage equal to PATK *
        // ${4}% + ${5} to up to ${3} enemies within a ${2}-meter radius of the target."
        [1524801] = new([SoulDestroyer], Radius: 1, Count: 2, Ratio: 3, Flat: 4),
        // Hidden Blade: "After casting [Meteor Assault], deals Neutral melee Physical damage equal to PATK * ${3}% + ${4} to
        // up to ${2} enemies within a ${1}-meter radius of you."
        [1525101] = new([MeteorAssault], Radius: 0, Count: 1, AroundCaster: true, Ratio: 2, Flat: 3),

        // Merchant line: no proc row either; "@{1}" counts are runtime values, read as one.
        // Mammonite: "[Cart Revolution] hits its primary target, triggering [Money Throw] @{1} Extra times and dealing PATK *
        // ${1}% + ${2} Neutral melee Physical damage. This effect has a ${3}-second Cooldown."
        [1640201] = new([CartRevolution], Ratio: 0, Flat: 1, Cooldown: 2),
        // Gunpowder Smash: "After using [Cart Revolution], immediately trigger @{1} instances of [Gunpowder Throw], dealing
        // Fire melee Physical damage equal to PATK * ${3}% + ${4} to up to ${2} enemies within ${1} meters. This effect has
        // a ${5}-second Cooldown."
        [1640301] = new([CartRevolution], Radius: 0, Count: 1, AroundCaster: true, Ratio: 2, Flat: 3, Element: Fire, Cooldown: 4),
        // Earth Spike: "[Hammer Fall] strikes ${1} enemies within range with [Earth Spike], dealing ... PATK * ${2}% + ${3},
        // with a ${4}% Chance to inflict [Stun] for ${5} seconds. This effect has a ${6}-second Cooldown."
        [1642201] = new([HammerFall], HitTargets: true, Count: 0, Ratio: 1, Flat: 2, Cooldown: 5, Status: Stun, StatusChance: 3,
            StatusSeconds: 4),
        // Stunning Smash: "[High Speed Cart Ram] performs @{1} Extra [Cart Clash] attacks against targets below ${1}% HP,
        // dealing PATK*${2}%+${3} ... This effect has a ${4}-second Cooldown."
        [1646201] = new([HighSpeedCartRam], HpBelow: 0, Ratio: 1, Flat: 2, Cooldown: 3),
        // Terminate Blast: "[High Speed Cart Ram] triggers [Explosive Strike] an Extra @{1} times when cast, dealing ... PATK
        // * ${3}% + ${4} to up to ${2} enemies within ${1} meters. This effect has a ${5}-second Cooldown."
        [1646301] = new([HighSpeedCartRam], Radius: 0, Count: 1, Ratio: 2, Flat: 3, Cooldown: 4),
    };

    private readonly GameData _data;
    private readonly CombatService _combat;
    private readonly BuffService _buffs;
    private readonly CharacterService _character;
    private readonly SessionRegistry _sessions;
    private readonly SkillService _skills;
    private readonly GameLoop _loop;
    private readonly ConditionalWeakTable<GameSession, Dictionary<long, long>> _readyAt = [];
    private readonly Dictionary<long, SkillConfig?> _procRows = [];
    private readonly Dictionary<(long Row, long Buff), JsonElement> _describedHits = [];

    // A test can fix each ten-thousandth roll without changing the production probability.
    internal Func<int, int> Roll { get; set; } = bound => Random.Shared.Next(bound);

    public TalentProcs(GameData data, CombatService combat, BuffService buffs, CharacterService character, SessionRegistry sessions,
        SkillService skills, GameLoop loop)
    {
        _data = data;
        _combat = combat;
        _buffs = buffs;
        _character = character;
        _sessions = sessions;
        _skills = skills;
        _loop = loop;
        buffs.SourceAttributes = BuffAttributes;
        combat.SkillBuffAllowed = SkillBuffAllowed;
        combat.CastResolved.Add(OnCastResolvedAsync);
        buffs.Changed.Add(OnBuffsChangedAsync);
        combat.TargetCountRow = TargetCountRow;
        combat.TakenAsync.Add(OnTakenAsync);
        combat.Killed.Add(OnKilledAsync);
        combat.PlayerDied.Add(OnDiedAsync);
        combat.PlayerKilled.Add(OnPlayerKilledAsync);
        character.ExtraAttributes.Add(SoulReaperAttributes);
    }

    /// <summary>
    /// The applier's talents change a buff it applies: "【Crazy Uproar】 Extra PATK +${1}" is a BuffAttr (type 2) tuple on
    /// Crazy Uproar's group. It counts for every recipient of the applier's buff ("Maximize Power increases nearby
    /// allies' PATK") (inferred).
    /// </summary>
    private IReadOnlyList<(long Id, double Value)> BuffAttributes(long sourceId, JsonElement row, IReadOnlyList<(long Id, double Value)> pairs)
    {
        GameSession? source = null;
        foreach (var s in _sessions.All())
            if (s.Role?.Id == sourceId) { source = s; break; }
        if (source == null || source.TalentBuffAttributes.Count == 0 && source.TalentProcBuffs.Count == 0) return pairs;
        long group = new BuffConfig(row).BuffId;
        List<(long Id, double Value)>? changed = null;
        foreach (var (buff, description) in source.TalentProcBuffs)
            if (BuffBonuses.TryGetValue(buff, out var bonus) && bonus.Group == group)
            {
                changed ??= [.. pairs];
                foreach (var (attr, index) in bonus.Pairs) changed.Add((attr, Value(description, index)));
            }
        foreach (var attribute in source.TalentBuffAttributes)
        {
            if (attribute.ByCategory || attribute.Group != group || attribute.Type != BuffAttrType || attribute.Index < 0 || attribute.Index >= pairs.Count) continue;
            changed ??= [.. pairs];
            var (id, value) = changed[attribute.Index];
            changed[attribute.Index] = (id, attribute.Operation switch
            {
                1 => value + attribute.Value,
                2 => attribute.Value,
                5 => value * (1 + attribute.Value / 10000),
                _ => value,
            });
        }
        return changed ?? pairs;
    }

    private static bool SkillBuffAllowed(GameSession caster, GameSession recipient, int buff) =>
        !TalentBuffs.TryGetValue(buff, out var gate)
        || caster.TalentProcBuffs.Any(t => t.Buff == gate.Talent) && (gate.Caster is not { } self || ReferenceEquals(caster, recipient) == self);

    /// <summary>A "while a buff is active" talent's attributes follow that buff (SeasonTalentService.Attributes).</summary>
    private async Task OnBuffsChangedAsync(GameSession s)
    {
        if (s.Role == null || !s.TalentProcBuffs.Any(b => WhileActive.ContainsKey(b.Buff))) return;
        if (s.SceneLoading) _character.Refresh(s);
        else await _character.RefreshStatsAsync(s);
    }

    private static double Value(string[] description, int index) =>
        index < description.Length && double.TryParse(description[index], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;

    private bool Ready(GameSession s, long buff, long cooldownMs)
    {
        var ready = _readyAt.GetOrCreateValue(s);
        long now = MonsterService.NowMs;
        if (ready.GetValueOrDefault(buff) > now) return false;
        if (cooldownMs > 0) ready[buff] = now + cooldownMs;
        return true;
    }

    /// <summary>
    /// After a player's cast: <paramref name="processed"/> are the monsters it hit. A field cast (Meteor Storm, Storm Gust)
    /// resolves later, so it comes with none and its procs centre on the cast's target point.
    /// </summary>
    private async Task OnCastResolvedAsync(GameSession s, ActiveSkillCast action, IReadOnlyList<MonsterUnit> processed, SkillHitInfo hit)
    {
        if (s.Role == null) return;
        var cast = new SkillConfig(action.Row);
        await PassiveProcsAsync(s, action, cast, processed, hit);
        if (s.TalentProcBuffs.Count == 0) return;
        await ChargesAsync(s, action, cast, processed);
        await HealProcsAsync(s, action, cast, hit);
        GravityTrap(s, action, cast);
        foreach (var (buff, description) in s.TalentProcBuffs)
        {
            if (!Rules.TryGetValue(buff, out var rule)) continue;
            if (rule.NormalAttack ? cast.IsNormalAtk != 1 : Array.IndexOf(rule.Groups, (int)cast.SkillId) < 0) continue;
            var target = processed.FirstOrDefault(m => m.Uid == action.Request.TargetId && m.Hp > 0) ?? processed.FirstOrDefault(m => m.Hp > 0)
                ?? (processed.Count == 0 && rule.Radius is int radius
                    ? _combat.MonstersNear(s, action.Request.TargetPos ?? s.Position, (float)Value(description, radius)).FirstOrDefault()
                    : null)
                // "... within a ${2}-meter radius of you" needs no living target: the cast may have killed it.
                ?? (rule.AroundCaster && rule.Radius is int around
                    ? _combat.MonstersNear(s, s.Position, (float)Value(description, around)).FirstOrDefault() : null);
            if (rule.RandomHit && processed.Count > 0) target = processed[Roll(processed.Count)];
            // A buff on the caster needs no living target: Basilica hits no monster.
            bool selfBuff = rule is { Buff: true, TargetBuff: false } || rule.ResetCooldown != 0;
            if (!selfBuff && target is not { Hp: > 0 }) continue;
            if (rule.HpBelow is int below && target is { } weak && weak.Hp * 100 >= weak.Template.MaxHp * Value(description, below)) continue;
            if (rule.Weapon != 0 && !SkillParameterRules.Wields(s, _data, rule.Weapon)) continue;
            if (rule.Active != 0 && !SkillParameterRules.HasBuffGroup(s, _data, rule.Active)) continue;
            if (rule.Chance is int chance && Roll(10000) >= Value(description, chance) * 100) continue;
            if (!Ready(s, buff, rule.Cooldown is int cooldown ? (long)(Value(description, cooldown) * 1000) : 0)) continue;
            if (rule.ResetCooldown != 0)
            {
                await CombatService.ResetCooldownAsync(s, rule.ResetCooldown);
                continue;
            }
            if (target == null || rule.Buff)
            {
                await AddProcBuffAsync(s, buff, rule, description, target);
                continue;
            }
            if (rule.Ratio is int ratio && SkillConfig.Get(_data, cast.Id) is { } trigger)
            {
                var described = DescribedHit(trigger, buff, rule, description, ratio);
                var targets = Targets(s, rule, description, new SkillConfig(described), target, processed);
                int hits = rule.Hits is int count ? Math.Max(1, (int)Value(description, count)) : 1;
                for (int i = 0; i < hits && targets.Any(m => m.Hp > 0); i++) await _combat.CastExtraAsync(s, described, targets);
                if (rule.Status != 0 && rule.StatusChance is int statusChance && rule.StatusSeconds is int statusSeconds)
                    foreach (var struck in targets)
                        if (struck.Hp > 0 && Roll(10000) < Value(description, statusChance) * 100)
                            await _buffs.ShowOnUnitAsync(struck, (int)rule.Status, (int)(Value(description, statusSeconds) * 1000), s.Role.Id);
                continue;
            }
            if (rule.Recast)
            {
                await _combat.CastExtraAsync(s, action.Row, Targets(s, rule with { RowArea = true }, description, cast, target, processed));
                continue;
            }
            if (BuffConfig.Get(_data, buff)?.LogicData is not { } logic || rule.Logic >= logic.Length) continue;
            if (ProcRow(logic[rule.Logic]) is not { } row || target.Hp <= 0) continue;
            await _combat.CastExtraAsync(s, row.Row, Targets(s, rule, description, row, target, processed));
        }
    }

    /// <summary>A Buff rule: the _kLogicData BuffConfig row on the caster (or the target) for the value after it in ms, or
    /// the description's seconds.</summary>
    private async Task AddProcBuffAsync(GameSession s, long buff, Rule rule, string[] description, MonsterUnit? target)
    {
        if (!rule.Buff || BuffConfig.Get(_data, buff)?.LogicData is not { } logic || rule.Logic >= logic.Length) return;
        long id = logic[rule.Logic];
        if (BuffConfig.Get(_data, id) == null) return;
        int ms = rule.BuffSeconds is int seconds ? (int)(Value(description, seconds) * 1000)
            : rule.Logic + 1 < logic.Length ? (int)logic[rule.Logic + 1] : 0;
        if (!rule.TargetBuff) await _buffs.AddAsync(s, (int)id, ms, s.Role!.Id);
        else if (target != null) await _buffs.ShowOnUnitAsync(target, (int)id, ms, s.Role!.Id);
    }

    /// <summary>The triggering row with the description's damage, once per (row, talent).</summary>
    private JsonElement DescribedHit(SkillConfig trigger, long buff, Rule rule, string[] description, int ratio) =>
        DescribedHit(trigger, buff, (long)(Value(description, ratio) * 100), rule.Flat is int flat ? (long)Value(description, flat) : 0,
            rule.Element);

    /// <summary>_kLogicData's first value: a SkillConfig row, or a skill group (Double Counter's 11127) at its level 1.</summary>
    private SkillConfig? ProcRow(long id)
    {
        if (_procRows.TryGetValue(id, out var cached)) return cached;
        return _procRows[id] = SkillConfig.Get(_data, id) is { } row ? row
            : SkillConfig.All(_data).Where(r => r.SkillId == id && r.Mode == 0).OrderBy(r => r.Level).Cast<SkillConfig?>().FirstOrDefault();
    }

    private List<MonsterUnit> Targets(GameSession s, Rule rule, string[] description, SkillConfig row, MonsterUnit target,
        IReadOnlyList<MonsterUnit> processed)
    {
        float radius;
        int others;
        if (rule.HitTargets)
        {
            var hit = processed.Where(m => m.Hp > 0);
            return [.. rule.Count is int count ? hit.Take((int)Value(description, count)) : hit];
        }
        if (rule.RowArea)
        {
            radius = (row.Range is [var range, ..] ? range : 0) / 100f;
            others = (int)Math.Max(0, row.TargetMax - 1);
        }
        else if (rule.Radius is int r)
        {
            radius = (float)Value(description, r);
            others = rule.Others is int o ? (int)Value(description, o) : rule.Count is int c ? (int)Value(description, c) - 1 : 0;
        }
        else return [target];
        var near = _combat.MonstersNear(s, rule.AroundCaster ? s.Position : target.Pos, radius).Where(m => m != target)
            .Take(Math.Max(0, others));
        return rule.OthersOnly ? [.. near] : [target, .. near];
    }
}
