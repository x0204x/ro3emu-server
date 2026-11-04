using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>Session energy meter from EnergyConfig. Persistence policy is still source-dependent.</summary>
public sealed class PlayerEnergyState
{
    public long Value { get; set; }
    public long LastRecoverMs { get; set; }
    public long NextRecoverMs { get; set; }
}

public sealed partial class BuffService
{
    /// <summary>
    /// 31334 on role entry, for EnergyConfig._iBelongJob matching the selected job (inferred) and for _iBelongMount
    /// naming a mount the role owns: 3112301, the Luxury Dragon's meter (mount 60060010, PublicParamConfig.MountDragonId),
    /// pays skill 3112301's cost [8,3112301,0,12] and has no _iBelongJob. *(inferred)* Ownership, not riding, keeps the
    /// meter: the row keeps its value on death, cut scenes and offline (_iDeadSave/_iCutSceneSave/_iOfflineSave 1).
    /// </summary>
    public async Task InitializeEnergyAsync(GameSession p)
    {
        if (p.Role == null || p.Progress == null) return;
        p.Energy.Clear();
        p.EnergyCareer = 0;
        p.EnergyMounts = 0;
        await ReconcileEnergyAsync(p);
    }

    /// <summary>
    /// On job change preserve shared job meters; ordinary same-job level progression never refills them. A mount
    /// ownership change (MountService.Changed) adds or drops the mount's meters.
    /// </summary>
    public async Task ReconcileEnergyAsync(GameSession p)
    {
        if (p.Role == null || p.Progress == null) return;
        int mounts = OwnedEnergyMounts(p);
        if (p.EnergyCareer == p.Progress.Career && p.EnergyMounts == mounts) return;
        int career = p.Progress.Career;
        var applicable = _data.Table("EnergyConfig").Values
            .Where(row => BelongsToJob(row, career) || BelongsToOwnedMount(row, p))
            .Select(row => (Id: (int)GameData.Int(row, "_iID"), Row: row))
            .Where(x => x.Id > 0).ToList();
        var ids = applicable.Select(x => x.Id).ToHashSet();
        foreach (int id in p.Energy.Keys.Where(id => !ids.Contains(id)).ToList()) p.Energy.Remove(id);
        p.EnergyCareer = career;
        p.EnergyMounts = mounts;
        long now = MonsterService.NowMs;
        foreach (var (id, row) in applicable)
        {
            if (p.Energy.ContainsKey(id)) continue;
            long interval = GameData.Int(row, "_iAutoRecoverInterval");
            p.Energy[id] = new PlayerEnergyState
            {
                Value = Math.Clamp(GameData.Int(row, "_iInitializeValue"), 0, GameData.Int(row, "_iLimitValue")),
                LastRecoverMs = now, NextRecoverMs = interval > 0 ? now + interval : 0,
            };
            await SendEnergyAsync(p, id, (EPlayerEnergyChangeReason)6);
        }
    }

    /// <summary>MountService.Changed: a role whose meters are not initialized yet waits for InitializeEnergyAsync.</summary>
    public Task MountsChangedAsync(GameSession p) => p.EnergyCareer == 0 ? Task.CompletedTask : ReconcileEnergyAsync(p);

    private static bool BelongsToJob(JsonElement row, int career) =>
        row.TryGetProperty("_iBelongJob", out var jobs) && jobs.ValueKind == JsonValueKind.Array
        && jobs.EnumerateArray().Any(j => GameData.Number(j) == career);

    private static bool BelongsToOwnedMount(JsonElement row, GameSession p) =>
        GameData.Int(row, "_iBelongMount") is var mount and > 0 && p.Mounts?.Mounts.ContainsKey(mount) == true;

    /// <summary>How many EnergyConfig mounts the role owns (a change makes the reconcile run; no allocation).</summary>
    private int OwnedEnergyMounts(GameSession p)
    {
        if (p.Mounts == null || p.Mounts.Mounts.Count == 0) return 0;
        int owned = 0;
        foreach (long mount in EnergyMounts())
            if (p.Mounts.Mounts.ContainsKey(mount)) owned++;
        return owned;
    }

    private long[]? _energyMounts;

    private long[] EnergyMounts() => _energyMounts ??= [.. _data.Table("EnergyConfig").Values
        .Select(row => GameData.Int(row, "_iBelongMount")).Where(id => id > 0).Distinct()];

    /// <summary>
    /// SkillConfig._kCost type 8 [8,energyConfigId,ratioOfMax,minimumFlat].
    /// LC_Skill_Function.lua:4137-4160 checks max(limit*ratio/10000,minimumFlat).
    /// </summary>
    private bool TryEnergyCosts(JsonElement skill, out List<(int EnergyId, long Amount)> result)
    {
        result = [];
        if (!skill.TryGetProperty("_kCost", out var costs) || costs.ValueKind != JsonValueKind.Array) return true;
        var totals = new Dictionary<int, long>();
        foreach (var entry in costs.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() == 0) return false;
            if (GameData.Number(entry[0]) != 8) continue;
            if (entry.GetArrayLength() < 4) return false;
            int id = (int)(GameData.Number(entry[1]) ?? 0);
            if (id <= 0 || _data.Row("EnergyConfig", id) is not { } config) return false;
            long limit = GameData.Int(config, "_iLimitValue");
            double? ratio = GameData.Number(entry[2]), minimum = GameData.Number(entry[3]);
            if (limit < 0 || ratio is null or < 0 || minimum is null or < 0) return false;
            long percent = (long)Math.Ceiling(limit * ratio.Value / 10000);
            long flat = (long)minimum.Value;
            long amount = Math.Max(percent, flat);
            if (amount > long.MaxValue - totals.GetValueOrDefault(id)) return false;
            totals[id] = totals.GetValueOrDefault(id) + amount;
        }
        result.AddRange(totals.Select(kv => (kv.Key, kv.Value)));
        return true;
    }

    public bool CanSpendEnergy(GameSession p, JsonElement skill) =>
        TryEnergyCosts(skill, out var costs)
        && costs.All(c => p.Energy.TryGetValue(c.EnergyId, out var meter) && meter.Value >= c.Amount);

    /// <summary>Call only after cast validation succeeds; changes each charged meter once and sends 31334.</summary>
    public async Task SpendEnergyAsync(GameSession p, JsonElement skill)
    {
        if (!TryEnergyCosts(skill, out var costs)
            || costs.Any(c => !p.Energy.TryGetValue(c.EnergyId, out var meter) || meter.Value < c.Amount)) return;
        foreach (var (id, amount) in costs)
        {
            var meter = p.Energy[id];
            meter.Value -= amount;
            await SendEnergyAsync(p, id, (EPlayerEnergyChangeReason)5);
        }
        if (costs.Any(c => c.EnergyId == EnhanceSpearCharges) && p.Energy[EnhanceSpearCharges].Value == 0)
            foreach (var storm in p.Buffs.Where(b => b.ConfigId == FuriousStorm).ToArray()) await RemoveAsync(p, storm);
    }

    /// <summary>Adds to a meter up to its limit: blades, charges and other fills no row's _kHitRecoverEnergy holds.</summary>
    internal async Task AddEnergyAsync(GameSession p, int energy, long amount)
    {
        if (amount <= 0 || !p.Energy.TryGetValue(energy, out var meter) || Data.Config.EnergyConfig.Get(_data, energy) is not { } config) return;
        meter.Value = Math.Clamp(meter.Value + amount, 0, config.LimitValue);
        await SendEnergyAsync(p, energy, (EPlayerEnergyChangeReason)2);
    }

    /// <summary>[Shield Stance]: "At ${6} stacks, your next ${7} Powerful Shield Bash" turn Ultra: the full meter becomes the mode buff's piles.</summary>
    private async Task UltraBashAtFullAsync(GameSession p, int id, PlayerEnergyState meter, long limit)
    {
        if (id != ShieldStance || meter.Value < limit) return;
        meter.Value = 0;
        await AddCoreAsync(p, UltraBashBuff, UltraBashMs, p.Role!.Id, UltraBashCharges);
    }

    /// <summary>Reflect Damage: "there is a ... 50% Chance, respectively, to grant you 1 stack of [Shield Stance]".</summary>
    internal async Task AddShieldStanceAsync(GameSession p)
    {
        if (!p.Energy.TryGetValue(ShieldStance, out var meter) || Data.Config.EnergyConfig.Get(_data, ShieldStance) is not { } config) return;
        meter.Value = Math.Clamp(meter.Value + 1, 0, config.LimitValue);
        await UltraBashAtFullAsync(p, ShieldStance, meter, config.LimitValue);
        await SendEnergyAsync(p, ShieldStance, (EPlayerEnergyChangeReason)2);
    }

    /// <summary>Takes this much from a meter, if it holds that much: a cost no row's _kCost names (Reading Spellbook's Book Pages).</summary>
    internal async Task<bool> TrySpendEnergyAsync(GameSession p, int energy, long amount)
    {
        if (!p.Energy.TryGetValue(energy, out var meter) || meter.Value < amount) return false;
        meter.Value -= amount;
        await SendEnergyAsync(p, energy, (EPlayerEnergyChangeReason)5);
        return true;
    }

    // BuffConfig "Furious Storm"; the EnergyConfig meters Rain of Spears' mode 0 and mode 1 rows pay.
    internal const int FuriousStorm = 1132401;
    private const int RainSpearCharges = 140002, EnhanceSpearCharges = 140003, PrecisionCounter = 140001, ShieldStance = 141002;
    private const int UltraBashBuff = 1163101, UltraBashCharges = 3, UltraBashMs = 3_600_000;

    /// <summary>Sonic Burst's _kBuff1: "[Furious Storm]: Clears all current [Rain of Spears] charges and grants 5 charges of
    /// [Hundred Spear - Enhance]". The buff switches Rain of Spears to its Enhance row, which pays the second meter, and no
    /// data fills that meter. Its tuple's -1 sets no time, so it ends with its charges (<see cref="SpendEnergyAsync"/>),
    /// and the row's 5 is the meter's limit (both inferred).</summary>
    internal async Task StartFuriousStormAsync(GameSession p)
    {
        if (!p.Energy.TryGetValue(RainSpearCharges, out var rain) || !p.Energy.TryGetValue(EnhanceSpearCharges, out var enhance)
            || Data.Config.EnergyConfig.Get(_data, EnhanceSpearCharges) is not { } config) return;
        rain.Value = 0;
        enhance.Value = config.LimitValue;
        await SendEnergyAsync(p, RainSpearCharges, (EPlayerEnergyChangeReason)4);
        await SendEnergyAsync(p, EnhanceSpearCharges, (EPlayerEnergyChangeReason)4);
    }

    /// <summary>SkillConfig._kHitRecoverEnergy [energy id, amount], after an accepted hit (inferred server trigger).</summary>
    public async Task RecoverEnergyOnHitAsync(GameSession p, JsonElement skill)
    {
        if (!skill.TryGetProperty("_kHitRecoverEnergy", out var hit) || hit.ValueKind != JsonValueKind.Array
            || hit.GetArrayLength() < 2) return;
        int id = (int)(GameData.Number(hit[0]) ?? 0);
        long amount = (long)(GameData.Number(hit[1]) ?? 0);
        if (amount <= 0 || !p.Energy.TryGetValue(id, out var meter) || _data.Row("EnergyConfig", id) is not { } config) return;
        long limit = GameData.Int(config, "_iLimitValue");
        meter.Value = Math.Clamp(meter.Value + amount, 0, limit);
        // Precision Thrust: "Gain @{2} stack(s) of [Hundred Spear Pierce] charge for every @{1} HIT(s)": a full 140001 turns into one 140002.
        if (id == PrecisionCounter && meter.Value >= limit)
        {
            meter.Value = 0;
            await AddEnergyAsync(p, RainSpearCharges, 1);
        }
        await UltraBashAtFullAsync(p, id, meter, limit);
        await SendEnergyAsync(p, id, (EPlayerEnergyChangeReason)2);
    }

    private void TickEnergy(GameSession p, long now)
    {
        if (p.Role == null) return;
        foreach (var (id, meter) in p.Energy)
        {
            if (meter.NextRecoverMs == 0 || meter.NextRecoverMs > now || _data.Row("EnergyConfig", id) is not { } row) continue;
            long interval = GameData.Int(row, "_iAutoRecoverInterval");
            if (interval <= 0) { meter.NextRecoverMs = 0; continue; }
            long ticks = 1 + (now - meter.NextRecoverMs) / interval;
            long change = GameData.Int(row, "_iAutoRecoverValue");
            meter.Value = Math.Clamp(meter.Value + ticks * change, 0, GameData.Int(row, "_iLimitValue"));
            meter.LastRecoverMs = meter.NextRecoverMs + (ticks - 1) * interval;
            meter.NextRecoverMs += ticks * interval;
            _loop.TrackLegacyDetached("energy recovery send", () => SendEnergyAsync(p, id, (EPlayerEnergyChangeReason)1));
        }
    }

    private async Task SendEnergyAsync(GameSession p, int id, EPlayerEnergyChangeReason reason)
    {
        var meter = p.Energy[id];
        await p.SendGameAsync(EnergyNotify, new PlayerEnergyNotify
        {
            EnergyConfigId = id, EnergyValue = meter.Value, ChangeReason = reason,
            LastRecoverTimeMs = meter.LastRecoverMs, NextRecoverTimeMs = meter.NextRecoverMs,
        });
        foreach (var handler in Changed) await handler(p);
    }

    /// <summary>技能機制設定既有且仍屬於角色的能量；依原表上限裁切，不建立未授權能量表。</summary>
    internal async Task<bool> SetExistingEnergyValueAsync(GameSession player, int id, long value)
    {
        if (player.Progress == null || !player.Energy.TryGetValue(id, out var meter)
            || _data.Row("EnergyConfig", id) is not { } row
            || !BelongsToJob(row, player.Progress.Career) && !BelongsToOwnedMount(row, player)) return false;
        meter.Value = Math.Clamp(value, 0, Math.Max(0, GameData.Int(row, "_iLimitValue")));
        await SendEnergyAsync(player, id, (EPlayerEnergyChangeReason)2);
        return true;
    }

    /// <summary>技能機制消耗既有能量；拒絕不足的值，不建立不存在的能量表。</summary>
    internal async Task<bool> ConsumeEnergyAmountAsync(GameSession player, int id, long amount)
    {
        if (amount < 0 || !player.Energy.TryGetValue(id, out var meter) || meter.Value < amount) return false;
        meter.Value -= amount;
        await SendEnergyAsync(player, id, (EPlayerEnergyChangeReason)5);
        return true;
    }
}
