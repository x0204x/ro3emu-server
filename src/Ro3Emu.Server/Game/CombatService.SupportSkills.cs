using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>Skills whose text does more than their row's damage and buffs: Hymn of Devotion, Ganbantein, who a heal reaches.</summary>
public sealed partial class CombatService
{
    /// <summary>Same party, for "Party Members" and Harmony Heal's party value (set by the host: TeamService.TeamOf, inferred for its 5-slot groups).</summary>
    public Func<GameSession, GameSession, bool>? SameParty { get; set; }

    /// <summary>Rows whose text names the caster as well as the ally they lock: Heal "for you and the designated ST ally", Highness Heal
    /// "to You and one designated ally", Kyrie Eleison "yourself and a designated ST ally". The data has no flag.</summary>
    private static bool ReachesCasterToo(string script) => script is "Heal" or "HighHeal" or "S1_Poem" or "Kyrie_Eleison" or "Aspersio";

    /// <summary>"you and up to ${2} allies within a ${1}m radius", on a row whose TargetMax 10 and radius say so while its selector [2,2,-1] says self only.
    /// The text and the row's two numbers against the selector: they reach allies (inferred).</summary>
    private static bool ReachesAlliesDespiteSelector(string script) => script is "Magnificat" or "ImpositioManus" or "Assumptio";

    /// <summary>"You and Party Members within ...": Hymn of the Lamb, Sacred Lily, Prayer of the Holy Spirit, Hymn of Devotion.</summary>
    private static bool PartyOnly(string script) => script is "Praefatio" or "Cantocandidus" or "Epiclesis" or "Offertorium";

    /// <summary>Radiant Holy Light: "up to ${2} allies ... prioritizing Party Members".</summary>
    private bool OutsideThePartyAsLast(GameSession s, GameSession p, string script) =>
        script == "ColuseoHeal" && p != s && SameParty?.Invoke(s, p) != true;

    /// <summary>Harmony Heal [3000,6000]: "restores Life equal to Magic ATK*30%. If the unit is a party Member, ... 60%".</summary>
    private double HealRatio(GameSession healer, GameSession recipient, JsonElement row, string script)
    {
        var ratios = Numbers(row, "_kDamageParam1");
        return script == "Harmony_Heal" && ratios.Length > 1 && SameParty?.Invoke(healer, recipient) == true ? ratios[1] : ratios.FirstOrDefault();
    }

    private Task SupportSkillOnCastAsync(GameSession s, ActiveSkillCast action, IReadOnlyList<MonsterUnit> processed, SkillHitInfo hit) => Script(action.Row) switch
    {
        "Offertorium" => HymnOfDevotionAsync(s, action),
        "GaBanTaYin" => GanbanteinAsync(s, action),
        "Envenom" => EnvenomPoisonAsync(s, action, processed),
        _ => Task.CompletedTask,
    };

    /// <summary>Envenom on a player, class 1500501.. and Card: Poison Spore's 2004201: "Deals Physical ATK * ... Poison melee Physical DMG to a designated ST enemy
    /// and applies [Poisoned] damage." _kLogicData names the row 15116xx "Poisoned". A monster gets it from TalentProcs.RowProcs. (inferred) One hit of that row
    /// right after, on the player the cast reached, if still alive; an extra cast pays nothing and sets off nothing of its own.</summary>
    private async Task EnvenomPoisonAsync(GameSession s, ActiveSkillCast action, IReadOnlyList<MonsterUnit> processed)
    {
        if (processed.Count > 0 || new SkillConfig(action.Row).LogicData is not [var id, ..] || SkillConfig.Get(_data, id) is not { } poison) return;
        if (action.PlayersStruck.FirstOrDefault(p => !p.Dead) is { } player && CanPlayerHit(s, player)) await CastExtraOnPlayerAsync(s, poison, player);
    }

    /// <summary>Hymn of Devotion 1431501, ["30","20","20","10"]: "HP equal to your MATK*${2}%+${3} every second to You and Party Members within
    /// ${1} m for ${4} s". Its buff holds the same numbers and no periodic formula, so each second reselects the party. (inferred) The first heal comes a second in.</summary>
    private Task HymnOfDevotionAsync(GameSession s, ActiveSkillCast action)
    {
        if (new SkillConfig(action.Row) is not { DescData: [var range, var ratio, var flat, var seconds, ..] } hymn || s.Role is not { } role) return Task.CompletedTask;
        float metres = (float)ParseDesc(range);
        var heal = new SkillConfig(new SkillConfig(hymn.WithoutBuff1()).WithDamage([(long)(ParseDesc(ratio) * 100)], [(long)ParseDesc(flat)]));
        long generation = s.SceneGeneration, ends = Now + (long)(ParseDesc(seconds) * 1000);
        async Task TickAsync()
        {
            if (!ReferenceEquals(s.Role, role) || s.Dead || s.SceneLoading || s.SceneGeneration != generation || Now > ends + 100) return;
            var party = AlliesNear(s, s.Position, metres).Where(p => p == s || SameParty?.Invoke(s, p) == true).ToList();
            if (party.Count > 0) await CastExtraOnAlliesAsync(s, heal.Row, party);
            if (Now + 1000 <= ends) _loop.ScheduleLegacy(TimeSpan.FromSeconds(1), TickAsync, "hymn of devotion");
        }
        _loop.ScheduleLegacy(TimeSpan.FromSeconds(1), TickAsync, "hymn of devotion");
        return Task.CompletedTask;
    }

    /// <summary>Ganbantein 1221301, ["12","3","15"]: "Removes up to ${3} ground effects from a designated area with a radius of ${2} meters". (inferred) All of them, as the text says, allies' too.</summary>
    private async Task GanbanteinAsync(GameSession s, ActiveSkillCast action)
    {
        if (new SkillConfig(action.Row) is not { DescData: [_, var radius, var count, ..] }) return;
        var center = action.Request.TargetPos ?? s.Position;
        foreach (var unit in _world.Map(s.WorldScene).Units.OfType<SkillObject>()
                     .Where(u => !u.Removing && Distance(u.Pos, center) <= ParseDesc(radius)).Take((int)ParseDesc(count)).ToArray())
            await RemoveSkillObjectAsync(unit);
    }

    /// <summary>Elements a live buff gives: Magnum Break 1100201 [11000,4,11400] "Basic Attack becomes Fire", Aspersio "becomes Holy",
    /// Enchant Poison "becomes Poison-property". Keyed by group, as only Magnum Break's row holds a number that matches its text. Card:
    /// Marine Sphere's Magnum Break puts on its own 2092101, with the same text and no number.</summary>
    private static int ElementFromBuff(GameSession s)
    {
        foreach (var buff in s.Buffs)
        {
            if (buff.ExpireMs <= Now) continue;
            switch (buff.ConfigId / 100)
            {
                case 11002 or 20921: return 4;
                case 14113: return 7;
                case 15120: return 6;
            }
        }
        return 0;
    }

    private const int PietyGroup = 11622, ArmorElementGroup = 9044, AngelingArmorGroup = 20515, HolyArmor = 7, NeutralArmor = 1;

    /// <summary>
    /// A player's armor: Neutral, or Holy under Piety 1162201 "Armor element Holy", whose row holds no number, or what a card gives. Group 9044
    /// "Change Armor to Ghost Property" holds the element in _kLogicData[0]: 904401 Earth 3, 904402 Water 5, 904403 Wind 2, 904404 Poison 6,
    /// 904405 Fire 4, 904406 Ghost 9, 904407 Holy 7. Sandman, Swordfish, Dokebi, Argiope, Pasana and Ghostring socket it as a passive beside a row
    /// named after the card, which is not read: Dokebi's 2011001 says 3 (Earth), while its text and 904403 say Wind. Card: Angeling has no
    /// 9044 row, its own 2051501 "Changes your armor's element to Holy" has [7]. Shadow Card: Angeling puts 904407 on for seconds (CardEffects).
    /// (inferred) A timed element beats a socketed one. LC_CardViewHelper lets in one card that changes armor elements (_iIsEleCard).
    /// </summary>
    internal int ArmorElement(GameSession p)
    {
        int element = NeutralArmor;
        foreach (var buff in p.Buffs)
        {
            if (buff.ExpireMs <= Now) continue;
            int group = buff.ConfigId / 100;
            if (group == PietyGroup) return HolyArmor;
            if (group is not (ArmorElementGroup or AngelingArmorGroup) || BuffConfig.Get(_data, buff.ConfigId)?.LogicData is not [var held, ..]) continue;
            if (!buff.IsPassive) return (int)held;
            element = (int)held;
        }
        return element;
    }

    private static DamageAttributes.Kind KindOf(MonsterTemplate t) => t.IsBoss ? DamageAttributes.Kind.Boss : DamageAttributes.Kind.Normal;

    /// <summary>Removals only a skill's text names: Dragon Howling "all status ailments"; King's Grace and Energy Release "all Control effects" (tag 37);
    /// Cart Boost "Removes Slow" (33); Hymn of the Lamb's _kLogicData [37,33]. A row _iDisperse 0 stays, as for a dispel.</summary>
    private async Task CleanseByTextAsync(GameSession recipient, JsonElement row, string script)
    {
        long[]? categories = script switch
        {
            "DragonHowling" => [],
            "KingsGrace" or "BombSuicide1" => [37],
            "CartBoost" => [33],
            "Praefatio" => [37, 33],
            _ => null,
        };
        if (categories == null) return;
        foreach (var buff in recipient.Buffs.Where(b => !b.IsPassive && BuffConfig.Get(_data, b.ConfigId) is { Type: 2, Disperse: not 0 } config
                     && (categories.Length == 0 || config.BuffCategory.Any(categories.Contains))).ToArray())
            await _buffs.RemoveAsync(recipient, buff);
    }

    /// <summary>Holy Light 1401101, _kLogicData [14117]: it "removes the target's Kyrie Eleison effect".</summary>
    private async Task HolyLightBreaksKyrieAsync(GameSession target, JsonElement row)
    {
        if (Script(row) != "HolyLight" || new SkillConfig(row).LogicData is not [var group, ..]) return;
        foreach (var buff in target.Buffs.Where(b => b.ConfigId / 100 == group).ToArray()) await _buffs.RemoveAsync(target, buff);
    }
}
