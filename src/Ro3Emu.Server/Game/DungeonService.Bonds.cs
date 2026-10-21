using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class DungeonService
{
    /// <summary>(verified) LC_JobHelper:11-71 and JobConfig._iDungeonJobPositionID select the class default.</summary>
    public static long PositionFor(GameData data, GameSession s)
    {
        var choices = PositionsFor(data, s.Progress?.Career ?? s.Role?.Career ?? 0);
        return choices.Contains(s.Dungeons?.JobPosition ?? 0) ? s.Dungeons!.JobPosition : choices.FirstOrDefault();
    }

    private static long[] PositionsFor(GameData data, long job) => data.Row("JobConfig", job) is { } row
        && row.TryGetProperty("_iDungeonJobPositionID", out var values) && values.ValueKind == JsonValueKind.Array
        ? values.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0))
            .Where(id => data.Row("DungeonJobPositionConfig", id) != null).ToArray() : [];

    /// <summary>
    /// (verified) TenGameplayBondsConfig -> LC_ConditionList 36000000 [7003,2] and 36000001 [3036,5]:
    /// same-guild pairs receive Guild Resonance; five different classes receive Bonding Oath.
    /// Class identity uses JobConfig._iSecondaryRoots (inferred: promotions are not different base classes).
    /// Unknown conditions fail closed; no reward multiplier or matchmaking slot follows from a bond.
    /// </summary>
    private long[] BondIds(JsonElement boss, GameSession subject, GameSession[] roster, Dictionary<long, bool>? cache = null)
    {
        if (subject.Role == null || !boss.TryGetProperty("_kBondsID", out var ids) || ids.ValueKind != JsonValueKind.Array) return [];
        var result = new List<long>();
        foreach (var value in ids.EnumerateArray())
        {
            long id = (long)(GameData.Number(value) ?? 0);
            if (cache != null && cache.TryGetValue(id, out bool cached))
            {
                if (cached) result.Add(id);
                continue;
            }
            if (_data.Row("TenGameplayBondsConfig", id) is not { } bond
                || _data.Row("LC_ConditionList", GameData.Int(bond, "_kUnlock")) is not { } condition
                || !condition.TryGetProperty("m_kSubConditionList", out var conditions)
                || conditions.ValueKind != JsonValueKind.Array || conditions.GetArrayLength() != 1) continue;
            var sub = conditions[0];
            if (sub.TryGetProperty("m_bInverse", out var inverse) && inverse.ValueKind == JsonValueKind.True
                || !sub.TryGetProperty("m_kData", out var args) || args.ValueKind != JsonValueKind.Array || args.GetArrayLength() != 2) continue;
            long guild = _guilds.ForRole(subject.Role.Id)?.Id ?? 0;
            int needed = (int)(GameData.Number(args[1]) ?? 0);
            bool enabled = needed > 0 && (GameData.Number(args[0]) switch
            {
                7003 => guild != 0 && roster.Count(s => s.Role != null && _guilds.ForRole(s.Role.Id)?.Id == guild) >= needed,
                3036 => roster.Select(s => _data.Row("JobConfig", s.Progress?.Career ?? s.Role?.Career ?? 0) is { } job
                    ? GameData.Int(job, "_iSecondaryRoots") : 0).Where(root => root != 0).Distinct().Count() >= needed,
                _ => false,
            });
            if (cache != null) cache[id] = enabled;
            if (enabled) result.Add(id);
        }
        return result.ToArray();
    }

    /// <summary>(verified) LC_DAL_Team:278-395 decodes member.multiDungeonBytes for bonds, rewards and jobPosition.</summary>
    private TeamMultiDungeonInfo TeamDungeonInfo(GameSession subject, IReadOnlyList<long> members)
    {
        var result = new TeamMultiDungeonInfo { JobPosition = PositionFor(_data, subject) };
        if (subject.Synced.GetValueOrDefault(Sync) is DBMultiDungeonInfo synced)
            result.DungeonInfo.Add(synced.DungeonInfo.Values.Select(d => d.Clone()));
        var roster = _sessions.All().Where(s => s.Role != null && members.Contains(s.Role.Id)).ToArray();
        var eligibility = new Dictionary<long, bool>();
        foreach (var boss in _data.Table("TenGameplayBossConfig").Values)
        {
            var ids = BondIds(boss, subject, roster, eligibility);
            if (ids.Length == 0) continue;
            result.PlayerBonds.Add(new TeamMultiDungeonBossPlayerBond { BossId = GameData.Int(boss, "_iBoss"), PlayerBond = { ids } });
        }
        return result;
    }

    private IEnumerable<int> BondBuffs(JsonElement boss, GameSession subject, GameSession[] roster) => BondIds(boss, subject, roster)
        .SelectMany(id => _data.Row("TenGameplayBondsConfig", id) is { } bond
            && bond.TryGetProperty("_kBuff", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(v => (int)(GameData.Number(v) ?? 0)).ToArray() : []).Distinct();

    private GameSession[] BondRoster(Attempt attempt, GameSession subject)
    {
        long team = subject.Role == null ? 0 : _teams.TeamOf(subject.Role.Id).TeamId;
        return team == 0 ? [subject] : Present(attempt).Where(attempt.Loaded.Contains)
            .Where(s => _teams.TeamOf(s.Role!.Id).TeamId == team).ToArray();
    }

    /// <summary>
    /// (verified) BuffConfig 990301/990101 modify 601/602 by 200/300 and clear on scene exit.
    /// (inferred) Only loaded, present party members qualify in an instance; refresh when that roster changes.
    /// The configured dungeon duration bounds the buffs, avoiding BuffService's unrelated 60-second fallback.
    /// </summary>
    private async Task RefreshBondsAsync(Attempt attempt)
    {
        if (attempt.Plan.Boss is not { } boss) return;
        var roster = Present(attempt).Where(attempt.Loaded.Contains).ToArray();
        foreach (var member in roster)
        {
            var wanted = BondBuffs(boss, member, BondRoster(attempt, member)).Concat(BlessingBuffs(attempt)).ToHashSet();
            foreach (var old in member.Buffs.Where(b => b.SourceId == attempt.Room.Id && !wanted.Contains(b.ConfigId)).ToArray())
                await _buffs.RemoveAsync(member, old);
            foreach (int id in wanted)
                if (!member.Buffs.Any(b => b.SourceId == attempt.Room.Id && b.ConfigId == id))
                    await _buffs.AddAsync(member, id, attempt.Plan.DurationMs, attempt.Room.Id);
        }
    }

    private Task ClearBondsAsync(GameSession s, Attempt attempt) => Task.WhenAll(s.Buffs
        .Where(b => b.SourceId == attempt.Room.Id).ToArray().Select(b => _buffs.RemoveAsync(s, b)));

    /// <summary>(inferred) Additive basis-point bonus/reduction applied to the owner's dungeon boss damage only.</summary>
    private long BondDamage(GameSession s, MonsterUnit monster, long damage, bool incoming)
    {
        if (damage <= 0 || Active(s) is not { } attempt || attempt.Plan.Boss is not { } boss
            || monster.InstanceId != attempt.Room.Id || monster.Template.NpcId != GameData.Int(boss, "_iBoss")) return damage;
        var roster = BondRoster(attempt, s);
        long attribute = incoming ? 602 : 601;
        double bonus = BondBuffs(boss, s, roster).Sum(id => _data.Row("BuffConfig", id) is { } row
            ? GameData.Pairs(row, "_kChangeAttri").Where(p => p.Id == attribute).Sum(p => p.Value) : 0);
        decimal factor = Math.Max(0, 10000m + (incoming ? -1 : 1) * (decimal)bonus) / 10000m;
        return (long)Math.Clamp(decimal.Round(damage * factor, 0, MidpointRounding.AwayFromZero), 1m, long.MaxValue);
    }
}
