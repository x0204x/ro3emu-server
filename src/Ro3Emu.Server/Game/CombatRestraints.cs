using System.Text.Json;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Job Resonance ("school" in the client code), JobResonanceConfig / JobResonanceActiveConfig /
/// JobResonanceRestraintConfig, as LC_System_Skill._getActiveSkillSchool (lua :835-955) and
/// UI_Sys_SkillPanelModel._checkSkillSchoolActive (:714-785) decide it:
/// <list type="bullet">
/// <item>The active row is the first JobResonanceActiveConfig row (by _iId) with _iJob = the role's career whose every
/// _iJobResonanceSkills entry e is met by the current skill plan: skill group e/100 learned to at least level e%100.
/// Those entries are requirements, not granted skills (UI_Sys_SkillSchoolItemWidgetCtrl.SetTargetData: current vs
/// target level; UIWord 33220 "Requirement"). The chosen profess (SkillPlan.skillGenre) does not decide it.</item>
/// <item>The function gate (1001 SkillSchool, condition 2000103 = job rank 5) holds for every _iJob in the table (all
/// rank-5 jobs), so the career match implies it.</item>
/// <item>No active row is resonance 1 "Inactive" (Lua_CFG_JobResonanceConfig.GetNullName: the row with _iPower 0).</item>
/// <item>JobResonanceConfig._kAttribute is a stat while active: 5 Gospel gives 207 (per_treat, Healing Done) +500
/// = +5% *(inferred: the client only displays it, UIWord 33212)*. _iPower 500 is only an "is a resonance" flag on the
/// client; the server's power score is outside this class (<see cref="Power"/>).</item>
/// <item>JobResonanceRestraintConfig: row _iID = the attacker's resonance, column _iJobProfessN = the target's,
/// per 10000 (Lua_CFG_JobResonanceRestraintConfig.GetAddDamageInfo / GetSubDamageInfo, UIWords 33210/33211/33219/
/// 33216/33237/33238): 2 Shrined beats 3 Blade Soul beats 4 Stellar Vortex beats 2, +10% / -10%; Inactive deals -10%
/// and takes +10%; 5 Gospel is neutral. Only players have a resonance, so it applies player against player
/// *(inferred: no client text names monsters)*.</item>
/// </list>
/// </summary>
public sealed class JobResonance(GameData data)
{
    public const int Inactive = 1;

    private sealed record ActiveRow(int ResonanceId, (int Group, int Level)[] Needs);

    private Dictionary<int, ActiveRow[]>? _byJob;
    private long[,]? _restraint;
    private Dictionary<int, (long Id, double Value)[]>? _attributes;

    /// <summary>The role's resonance id from its career and the current skill plan's levels; <see cref="Inactive"/> if none.</summary>
    public int ResonanceId(RoleProgress progress)
    {
        var byJob = _byJob ??= BuildActive();
        if (!byJob.TryGetValue(progress.Career, out var rows)) return Inactive;
        foreach (var row in rows)
        {
            bool met = true;
            foreach (var (group, level) in row.Needs)
                if (progress.Skills.GetValueOrDefault(group) < level) { met = false; break; }
            if (met) return row.ResonanceId;
        }
        return Inactive;
    }

    /// <summary>Damage per 10000 from an attacker of one resonance to a target of another (10000 when unlisted).</summary>
    public long Multiplier(int attacker, int target)
    {
        var table = _restraint ??= BuildRestraint();
        return attacker > 0 && target > 0 && attacker < table.GetLength(0) && target < table.GetLength(1)
            && table[attacker, target] > 0 ? table[attacker, target] : 10000;
    }

    /// <summary>JobResonanceConfig._kAttribute of a resonance (empty for none).</summary>
    public (long Id, double Value)[] Attributes(int resonanceId) =>
        (_attributes ??= BuildAttributes()).TryGetValue(resonanceId, out var list) ? list : [];

    /// <summary>JobResonanceConfig._iPower (500 for an active resonance, 0 for Inactive).</summary>
    public long Power(int resonanceId) => Row(resonanceId) is { } row ? GameData.Int(row, "_iPower") : 0;

    private JsonElement? Row(int resonanceId)
    {
        foreach (var row in data.Table("JobResonanceConfig").Values)
            if (GameData.Int(row, "_iJobResonanceId") == resonanceId) return row;
        return null;
    }

    private Dictionary<int, ActiveRow[]> BuildActive() => data.Table("JobResonanceActiveConfig").Values
        .OrderBy(r => GameData.Int(r, "_iId"))
        .GroupBy(r => (int)GameData.Int(r, "_iJob"))
        .ToDictionary(g => g.Key, g => g.Select(r => new ActiveRow((int)GameData.Int(r, "_iJobResonanceId"),
            r.TryGetProperty("_iJobResonanceSkills", out var skills) && skills.ValueKind == JsonValueKind.Array
                ? skills.EnumerateArray().Select(e => (long)(GameData.Number(e) ?? 0)).Where(e => e > 0)
                    .Select(e => ((int)(e / 100), (int)(e % 100))).ToArray()
                : [])).ToArray());

    private long[,] BuildRestraint()
    {
        var rows = data.Table("JobResonanceRestraintConfig").Values.ToArray();
        int size = rows.Select(r => (int)GameData.Int(r, "_iID")).DefaultIfEmpty(0).Max() + 1;
        var table = new long[size, size];
        foreach (var row in rows)
        {
            int attacker = (int)GameData.Int(row, "_iID");
            for (int target = 1; target < size; target++) table[attacker, target] = GameData.Int(row, $"_iJobProfess{target}");
        }
        return table;
    }

    private Dictionary<int, (long Id, double Value)[]> BuildAttributes() => data.Table("JobResonanceConfig").Values
        .ToDictionary(r => (int)GameData.Int(r, "_iJobResonanceId"), r => GameData.Pairs(r, "_kAttribute").ToArray());
}

/// <summary>A player's hit on a player: the resonance multiplier, then the totem bonus (both per 10000).</summary>
public static class PlayerRestraints
{
    public static long Apply(JobResonance resonance, TotemRestraint totems, RoleProgress attacker, RoleProgress target, long damage)
    {
        if (damage <= 0) return damage;
        long ratio = resonance.Multiplier(resonance.ResonanceId(attacker), resonance.ResonanceId(target));
        long totem = 10000 + totems.Bonus(attacker.Career, target.Career);
        return Math.Max(1, damage * ratio / 10000 * totem / 10000);
    }
}

/// <summary>
/// TotemRestraintConfig (4 rows, no client reader): row _iID = the attacker's totem type, column _iTotemN = the target
/// of type N+1, per 10000 extra damage. *(inferred)* The type is JobConfig._iTotem (1 Swordman/Thief lines and the
/// Novice, 2 Merchant lines, 3 Archer lines, 4 Mage/Acolyte lines), the only totem type field in the data; read so the
/// four rows form one counter cycle 1 &gt; 4 &gt; 3 &gt; 2 &gt; 1, each +20%. Monsters have no totem type, so it applies
/// player against player.
/// </summary>
public sealed class TotemRestraint(GameData data)
{
    private long[,]? _bonus;
    private Dictionary<int, int>? _jobTotem;

    /// <summary>JobConfig._iTotem of a career (0 when none).</summary>
    public int TotemType(int career) => (_jobTotem ??= data.Table("JobConfig").Values
        .ToDictionary(r => (int)GameData.Int(r, "_iID"), r => (int)GameData.Int(r, "_iTotem"))).GetValueOrDefault(career);

    /// <summary>Extra damage per 10000 from an attacker career to a target career.</summary>
    public long Bonus(int attackerCareer, int targetCareer)
    {
        var table = _bonus ??= Build();
        int a = TotemType(attackerCareer), t = TotemType(targetCareer);
        return a > 0 && t > 0 && a < table.GetLength(0) && t < table.GetLength(1) ? table[a, t] : 0;
    }

    private long[,] Build()
    {
        var rows = data.Table("TotemRestraintConfig").Values.ToArray();
        int size = Math.Max(5, rows.Select(r => (int)GameData.Int(r, "_iID")).DefaultIfEmpty(0).Max() + 1);
        var table = new long[size, size];
        foreach (var row in rows)
        {
            int attacker = (int)GameData.Int(row, "_iID");
            for (int column = 0; column + 1 < size; column++) table[attacker, column + 1] = GameData.Int(row, $"_iTotem{column}");
        }
        return table;
    }
}

/// <summary>
/// The title counter, Formula_ATitleDamageAdd (MGCommon's formula terms) from TitleBuffConfig: UIWords 31009 "When
/// fighting other players, you deal Extra damage to players with a lower Title level, but deal reduced damage to players
/// with a higher Title level." A title rank's level is TitleConfig._kTitleLev (1..5, the five emblem tiers), the key of
/// TitleBuffConfig._kIndex. *(inferred)* No client code reads the table: each side's term is its level's row (0 without a
/// title rank) and the hit changes by the attacker's term minus the target's, per 10000 like the other counters, so
/// equal levels change nothing and level 5 against level 1 deals +16% (the other way -16%).
/// </summary>
public sealed class TitleDamage(GameData data)
{
    private Dictionary<long, long>? _byLevel;

    /// <summary>TitleBuffConfig._kTitleName of a title rank (TitleConfig id), 0 for none.</summary>
    public long Term(long titleId) => TitleConfig.Get(data, titleId) is { } title
        ? (_byLevel ??= TitleBuffConfig.All(data).ToDictionary(r => r.Index, r => r.DamageAdd)).GetValueOrDefault(title.TitleLev)
        : 0;

    /// <summary>A player's hit on a player after the title counter.</summary>
    public long Apply(long attackerTitle, long targetTitle, long damage) => damage <= 0 ? damage
        : Math.Max(1, damage * Math.Max(0, 10000 + Term(attackerTitle) - Term(targetTitle)) / 10000);
}
