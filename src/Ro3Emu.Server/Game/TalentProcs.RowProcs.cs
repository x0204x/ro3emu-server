using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Hits a skill row or a buff names in its _kLogicData, as [chance in basis points, row] or just [row], and cooldowns a skill's text resets.
/// The rows themselves are plain hits (Poisoned's ZhongDu, Meteorite's FireStar); nothing else casts them.
/// </summary>
public sealed partial class TalentProcs
{
    private const int EnchantPoisonGroup = 15120, FearBreezeGroup = 13301, MeltDownGroup = 16203, ChargeUpGroup = 11604, WargAutoGroup = 13317;
    private const long MeteoriteRow = 1235001, FearBreezeRows = 13304, WargStrike = 13312, WargBite = 13325;
    private const int WargKingGroup = 13326, WargFollowers = 4;
    private const long FireElement = 4, UndeadElement = 10, BowWeapon = 11, WeaponCondition = 2001, CrescentWheel = 16308, AxeTornado = 16304;
    private const long WargAutoKey = -13317, ArrowBarrageKey = -13004;
    private const int WargAutoEveryMs = 2500;

    /// <summary>"Casting X resets this skill's Cooldown", by the cast's group: Warg Bite "resetting the Cooldown of Warg Hunt"; Swift Warg by Warg Charge or
    /// Warg Hunt; Flowing Shadow by Shadow Guard, Thousand Shadows or Looping Shadow; Piercing Shadow "Immediately refreshes Shadow Guard".</summary>
    private static readonly Dictionary<long, int[]> CastResets = new()
    {
        [13325] = [13323],
        [WargStrike] = [13326],
        [13323] = [13326],
        [15322] = [15321],
        [15323] = [15321],
        [15320] = [15321], // Looping Shadow, "chuanying"
        [15326] = [15323],
    };

    private static ActiveBuff? Holding(GameSession s, int group)
    {
        long now = MonsterService.NowMs;
        foreach (var buff in s.Buffs) if (buff.ExpireMs > now && buff.ConfigId / 100 == group) return buff;
        return null;
    }

    private async Task CastNamedRowAsync(GameSession s, long row, IReadOnlyList<MonsterUnit> targets)
    {
        if (SkillConfig.Get(_data, row) is { } hit && targets.Count > 0) await _combat.CastExtraAsync(s, hit.Row, targets);
    }

    /// <summary>A "targeted bow skill": one locked on its target, under the weapon condition its row holds (LC_ConditionList [2001, 11]).</summary>
    private bool TargetedBowSkill(SkillConfig cast) => cast.Release == SkillRelease.Lock && !cast.IsNormalAttack(_data)
        && _data.Row("LC_ConditionList", cast.SkillCondition) is { } condition && new LCConditionList(condition).HasSubCondition(WeaponCondition, BowWeapon);

    private async Task RowProcsAsync(GameSession s, ActiveSkillCast action, SkillConfig cast, IReadOnlyList<MonsterUnit> processed)
    {
        if (CastResets.TryGetValue(cast.SkillId, out var resets))
            foreach (var group in resets) await CombatService.ResetCooldownAsync(s, group);
        // Crescent Wheel: "For every @{1} enemies hit, reduce Axe Tornado's Cooldown by ${4} seconds".
        if (cast.SkillId == CrescentWheel && processed.Count > 0) await CombatService.CutCooldownAsync(s, (int)AxeTornado, (long)(Value(cast.DescData, 3) * 1000) * processed.Count);
        if ((processed.FirstOrDefault(m => m.Uid == action.Request.TargetId) ?? (processed.Count > 0 ? processed[0] : null)) is not { Hp: > 0 } target) return;
        // King of the Wargs: "casting [Warg Charge] causes ${2} wolf followers to charge alongside you, dealing ${2} Extra hits", "Casting [Warg Bite] causes ${4} wolf followers to bite":
        // its buff holds the two rows, the second already three words.
        if (Holding(s, WargKingGroup) is { } king && BuffConfig.Get(_data, king.ConfigId)?.LogicData is [var chargeRow, var biteRow, ..])
        {
            if (cast.SkillId == WargStrike)
                for (int follower = 0; follower < WargFollowers && target.Hp > 0; follower++) await CastNamedRowAsync(s, chargeRow, [target]);
            else if (cast.SkillId == WargBite && target.Hp > 0) await CastNamedRowAsync(s, biteRow, [target]);
        }
        var logic = cast.LogicData;
        switch (cast.ScriptLogic)
        {
            case "Envenom" or "VenomDust" when logic is [var poison, ..]: // "applies Poisoned damage", "inflicts Poisoned damage on the target"
                await CastNamedRowAsync(s, poison, [target]);
                break;
            // "If the target is a Normal Undead Monster, there is a ${4}% Chance to kill it instantly", [1101, chance].
            case "TurnUndead" when logic is [_, var chance, ..] && !target.Template.IsBoss && target.Template.Element == UndeadElement && _combat.Rolls.Proc(10000) < chance:
                await _combat.CastExtraAsync(s, cast.AsFixedHit(target.Hp), [target]);
                break;
            // Arrow Barrage on Arrow Shower: "40% chance to cast 2 times, cooldown ${5} seconds", [group, chance]; its row's _iCD is the same 5 s.
            case "ArrowShowerBD" when logic is [_, var chance, ..] && _combat.Rolls.Proc(10000) < chance && Ready(s, ArrowBarrageKey, cast.Cd):
                await _combat.CastExtraAsync(s, cast.Row, [.. processed.Where(m => m.Hp > 0)], parametersApplied: true);
                break;
        }
        if (target.Hp <= 0) return;
        // Enchant Poison: "When your Basic Attack deals damage, there is a ${2}% Chance to inflict Poisoned", [chance, row].
        if (cast.IsNormalAttack(_data) && Holding(s, EnchantPoisonGroup) is { } enchant && BuffConfig.Get(_data, enchant.ConfigId)?.LogicData is [var odds, var poisoned, ..]
            && _combat.Rolls.Proc(10000) < odds) await CastNamedRowAsync(s, poisoned, [target]);
        // Fear Breeze: "when a targeted bow skill hits, the target has a ${1}% Chance to be struck by piercing arrows": [chance] and its level's row of 13304.
        if (Holding(s, FearBreezeGroup) is { } breeze && TargetedBowSkill(cast) && BuffConfig.Get(_data, breeze.ConfigId)?.LogicData is [var chanceOfArrows, ..]
            && _combat.Rolls.Proc(10000) < chanceOfArrows) await CastNamedRowAsync(s, FearBreezeRows * 100 + breeze.ConfigId % 100, [target]);
        // Warg Strike: "every ${7} seconds, using Focused Arrow Strike casts this skill on the target": 13317's [10000, row] at the level learned.
        if (cast.ScriptLogic.StartsWith("AimedBolt", StringComparison.Ordinal) && _skills.Level(s.Progress!, (int)WargStrike) is > 0 and var level
            && BuffConfig.Get(_data, WargAutoGroup * 100 + level)?.LogicData is [_, var strike, ..] && Ready(s, WargAutoKey, WargAutoEveryMs))
            await CastNamedRowAsync(s, strike, [target]);
    }

    private async Task RowProcsOnDealtAsync(GameSession s, MonsterUnit target, JsonElement row, long amount)
    {
        if (amount <= 0) return;
        // Charge Up: "The next time you Deals DMG, removes Charge Up and triggers Charge Up Strike against the primary target": [11604, 11622, the level's id].
        if (Holding(s, ChargeUpGroup) is { } charge && BuffConfig.Get(_data, charge.ConfigId)?.LogicData is [_, var strikes, var levelled, ..])
        {
            await _buffs.RemoveAsync(s, charge);
            if (target.Hp > 0) await CastNamedRowAsync(s, strikes * 100 + levelled % 100, [target]);
        }
        var skill = new SkillConfig(row);
        // Crimson Rock: "a ${5}% Chance to trigger Meteorite"; Tetra Vortex the same on its Fire hit, each [chance], and the text links skill/1235001.
        if (skill.LogicData is [var meteorite, ..] && target.Hp > 0 && (skill.ScriptLogic.StartsWith("CrimsonRock", StringComparison.Ordinal)
                || skill.ScriptLogic.StartsWith("TetraVortex", StringComparison.Ordinal) && skill.EleType == FireElement)
            && _combat.Rolls.Proc(10000) < meteorite) await CastNamedRowAsync(s, MeteoriteRow, [target]);
        // Melt Down: "there is a ${4}% Chance to reduce its PATK and DEF": [PvP chance, its time, the debuff, the debuff's time].
        if (Holding(s, MeltDownGroup) is { } melt && BuffConfig.Get(_data, melt.ConfigId) is { LogicData: [_, _, var debuff, var ms, ..] } meltRow
            && target.Hp > 0 && _combat.Rolls.Proc(100) < Value(meltRow.DescData, 3)) await _buffs.AddSupportAsync(target, (int)debuff, (int)ms, s.Role!.Id);
    }
}
