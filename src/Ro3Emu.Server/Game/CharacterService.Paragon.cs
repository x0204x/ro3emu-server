using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Astral Level EXP (Paragon; ParagonService has the stars). The client says only "Complete quests and Kill monsters to
/// earn EXP" (16010/16118) and shows exp against PatronLevelConfig[lv]._iPatronExp, so the reconstruction (inferred) is:
/// Base EXP past the level cap (BaseLvLimitConfig, 99, where function 71 opens) goes to Astral EXP instead of being
/// dropped, with the PatronExpAddConfig catch-up bonus; a level needs its row's _iPatronExp and the last level (100,
/// no EXP) keeps what it gains. The bonus compares the Astral Level with the server's (PatronWordLvConfig of the open day,
/// the highest without a row, as Lua_CFG_PatronWordLvConfig falls back): the first listed gap at or above the role's
/// gives its _iPursueExpAdditionPercent, per million (5015 extraAdd, shown by the role tips as extraAdd / 10000 %).
/// </summary>
public sealed partial class CharacterService
{
    private (long[] Gaps, long[] Bonus)? _paragonPursue;

    private static long ParagonNeed(GameData data, int level) =>
        data.Row("PatronLevelConfig", level) is { } row ? new PatronLevelConfig(row).PatronExp : 0;

    /// <summary>The Astral points of the levels reached: PatronLevelConfig _iStatusPoint of levels 1..<paramref name="level"/>.</summary>
    internal long ParagonPointsEarned(int level)
    {
        long points = 0;
        for (int l = 1; l <= level; l++)
            if (data.Row("PatronLevelConfig", l) is { } row) points += new PatronLevelConfig(row).StatusPoint;
        return points;
    }

    /// <summary>The catch-up bonus at <paramref name="level"/>, per million (5015 extraAdd).</summary>
    internal long ParagonExpBonus(int level)
    {
        var (gaps, bonus) = _paragonPursue ??= BuildParagonPursue();
        int gap = level - ServerParagonLevel();
        for (int i = 0; i < gaps.Length; i++) if (gaps[i] >= gap) return bonus[i];
        return 0;
    }

    private (long[] Gaps, long[] Bonus) BuildParagonPursue()
    {
        var rows = data.Table("PatronExpAddConfig").Values
            .Select(r => new PatronExpAddConfig(r)).Select(r => (Gap: r.BaseLvGap, Bonus: r.PursueExpAdditionPercent))
            .OrderBy(r => r.Gap).ToArray();
        return ([.. rows.Select(r => r.Gap)], [.. rows.Select(r => r.Bonus)]);
    }

    private int ServerParagonLevel()
    {
        int day = ServerDay?.Invoke() ?? 1, highest = 0, today = 0;
        foreach (var row in data.Table("PatronWordLvConfig").Values)
        {
            var world = new PatronWordLvConfig(row);
            int level = (int)world.ServerPatronLv;
            highest = Math.Max(highest, level);
            if (world.Day == day) today = level;
        }
        return today > 0 ? today : highest;
    }

    /// <summary>Base EXP left over at the level cap becomes Astral EXP (see the summary).</summary>
    private void AddParagonExp(RoleProgress p, long exp)
    {
        int level = Math.Max(1, p.ParagonLevel);
        long bonus = ParagonExpBonus(level);
        exp = checked(exp + exp * bonus / 1_000_000);
        p.ParagonExp = checked(p.ParagonExp + exp);
        for (long need; (need = ParagonNeed(data, level)) > 0 && p.ParagonExp >= need;)
        {
            p.ParagonExp -= need;
            level++;
        }
        p.ParagonLevel = level;
    }

    /// <summary>GM: sets the Astral Level (its EXP restarts at 0).</summary>
    public async Task<string> SetParagonLevelAsync(GameSession session, int level)
    {
        using var _ = await LockAsync(session);
        var p = CloneProgress(session.Progress!);
        int max = data.Table("PatronLevelConfig").Values.Select(r => (int)new PatronLevelConfig(r).PatronLv).DefaultIfEmpty(1).Max();
        p.ParagonLevel = Math.Clamp(level, 1, max);
        p.ParagonExp = 0;
        await CommitCharacterAsync(session, "gm-paragon", progress: p);
        foreach (var handler in StateChanged) handler(session);
        return $"Astral Level {p.ParagonLevel}.";
    }
}
