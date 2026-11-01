using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class BuffService
{
    private const int ControlBuffTag = 37, NegativeBuffType = 2;
    internal const int ControlReleaseSkillGroup = 93010; // LC_Unit_Player.CanBlockSkill 明確排除「解控」。
    private const int ControlChargeGroup = 9101; // Chimera's three _kBuff1 entries; three applications, capped by BuffConfig._iMaxStack.

    // SpecialStatusConfig 22-24 (_kDesc): immunity is implemented through the holder buff's _kBuffCategory. Super Armor
    // 9031 carries 19, control immunity 9032 (and the MVP born buff 9009) 20, debuff immunity 9033 21, 9034 22.
    private const int DisplacementImmunityCategory = 19, ControlImmunityCategory = 20,
        DebuffImmunityCategory = 21, AllNegativeImmunityCategory = 22;

    internal List<Func<GameSession, JsonElement, bool>> PlayerBuffImmunities { get; } = [];

    private async Task<bool> RejectIncomingAsync(GameSession player, JsonElement incoming, long sourceId)
    {
        if (ImmuneTo(player.Buffs, incoming) || PlayerBuffImmunities.Any(reject => reject(player, incoming))) return true;
        if (GameData.Int(incoming, "_iType") != NegativeBuffType
            || !GuardianPetRules.Numbers(incoming, "_kBuffCategory").Contains(ControlBuffTag)) return false;
        var charge = player.Buffs.FirstOrDefault(b => b.IsGuardianOwned && b.ExpireMs > MonsterService.NowMs
            && _data.Row("BuffConfig", b.ConfigId) is { } row && GameData.Int(row, "_iBuffID") == ControlChargeGroup);
        // A control that diminishing returns refuses anyway costs no charge.
        if (charge == null || sourceId != player.Role!.Id
            && DecayRefuses(player.Role.Id, player.SceneEntry, (int)GameData.Int(incoming, "_iID"))) return false;
        // (inferred) Spend one charge per incoming control application; ordinary immunity spends none.
        if (charge.PileCount <= 1) await RemoveAsync(player, charge);
        else await AddCoreAsync(player, charge.ConfigId, (int)Math.Clamp(charge.ExpireMs - MonsterService.NowMs, 1, int.MaxValue),
            charge.SourceId, charge.PileCount - 1, guardianOwned: true);
        return true;
    }

    /// <summary>Tag 37 identifies control effects and _iType 2 debuffs; the holder's categories decide immunity.
    /// (inferred) Reject the incoming buff before decay, rather than merely hiding its icon.
    /// 免疫種類由持有狀態的分類決定，不能因施放者不是寵物而失效。</summary>
    private bool ImmuneTo(IEnumerable<ActiveBuff> active, JsonElement incoming)
    {
        if (GameData.Int(incoming, "_iType") != NegativeBuffType) return false;
        long now = MonsterService.NowMs;
        if (active.Any(b => b.ExpireMs > now && b.ShieldDebuffImmunity && b.ShieldRemaining > 0)) return true;
        bool control = GuardianPetRules.Numbers(incoming, "_kBuffCategory").Contains(ControlBuffTag);
        bool displacement = GameData.Int(incoming, "_iBuffID") is KnockBackGroup or KnockUpGroup;
        // Chimera's charge also carries control immunity (category 20), but it is spent per application instead.
        return active.Any(b => b.ExpireMs > now
            && _data.Row("BuffConfig", b.ConfigId) is { } row && GameData.Int(row, "_iBuffID") != ControlChargeGroup
            && GuardianPetRules.Numbers(row, "_kBuffCategory") is var held
            && (held.Contains(DebuffImmunityCategory) || held.Contains(AllNegativeImmunityCategory)
                || control && held.Contains(ControlImmunityCategory)
                || displacement && held.Contains(DisplacementImmunityCategory)));
    }

    /// <summary>A unit holding a control immunity (category 20: the MVPs' born buffs 900801/900901, 9032) resists a control
    /// (tag 37) from any source: Element Control's "When a Boss target resists your [Frozen], [Petrify], or [Stun]" says
    /// bosses resist them. 在通用免疫拒絕前保留此通知，讓元素控制的抵抗後追擊仍能觸發。</summary>
    private bool UnitResistsControl(IEnumerable<ActiveBuff> active, JsonElement incoming)
    {
        var control = new BuffConfig(incoming);
        if (control.Type != NegativeBuffType || !control.BuffCategory.Contains(ControlBuffTag)) return false;
        long now = MonsterService.NowMs;
        return active.Any(b => b.ExpireMs > now && BuffConfig.Get(_data, b.ConfigId) is { } row
            && row.BuffId != ControlChargeGroup && row.BuffCategory.Contains(ControlImmunityCategory));
    }

    /// <summary>A unit resisted a control a player applied: the unit, the player's role id and the control's BuffConfig
    /// row (PassiveSkills: Element Control).</summary>
    internal List<Func<WorldUnit, long, JsonElement, Task>> ControlResisted { get; } = [];

    internal async Task DispelControlsAsync(GameSession player, bool allDebuffs = false)
    {
        foreach (var buff in player.Buffs.Where(b => !b.IsPassive && _data.Row("BuffConfig", b.ConfigId) is { } row
            && GameData.Int(row, "_iType") == NegativeBuffType && GameData.Int(row, "_iDisperse") != 0
            && (allDebuffs || GuardianPetRules.Numbers(row, "_kBuffCategory").Contains(ControlBuffTag))).ToArray())
            await RemoveAsync(player, buff);
    }

    internal bool UnitForbidden(WorldUnit unit, int ordinal) => _unitBuffs.TryGetValue(unit.Uid, out var buffs)
        && buffs.Any(b => b.ExpireMs > MonsterService.NowMs && b.StateOrdinal > 0
            && _data.Row("SpecialStatusConfig", b.StateOrdinal) is { } state
            && GuardianPetRules.Numbers(state, "_kForbidden").Contains(ordinal));

    /// <summary>Does the unit carry a live negative buff (BuffConfig._iType 2: Stun, Immobilize, Poison ...).</summary>
    internal bool UnitHasDebuff(WorldUnit unit) => _unitBuffs.TryGetValue(unit.Uid, out var buffs)
        && buffs.Any(b => b.ExpireMs > MonsterService.NowMs && BuffConfig.Get(_data, b.ConfigId)?.Type == NegativeBuffType);

    internal bool UnitHasGroup(WorldUnit unit, int group) => _unitBuffs.TryGetValue(unit.Uid, out var buffs)
        && buffs.Any(b => b.ExpireMs > MonsterService.NowMs && _data.Row("BuffConfig", b.ConfigId) is { } row
            && GameData.Int(row, "_iBuffID") == group);

    internal ulong UnitStatus(WorldUnit unit) => !_unitBuffs.TryGetValue(unit.Uid, out var buffs) ? 0
        : buffs.Where(b => b.ExpireMs > MonsterService.NowMs && b.StateOrdinal is > 0 and < 33)
            .Aggregate(0UL, (mask, b) => mask | 1UL << (b.StateOrdinal - 1));

    // LC_UnitSpecialStatus.GetStaggerState 使用 1 << _iStaggerType；這不是 _kForbidden 的位元。
    internal ulong UnitStaggerMask(WorldUnit unit) => _unitBuffs.TryGetValue(unit.Uid, out var buffs)
        ? StaggerMask(buffs) : 0;

    private ulong StaggerMask(IEnumerable<ActiveBuff> buffs)
    {
        ulong mask = 0;
        foreach (var buff in buffs.Where(b => b.ExpireMs > MonsterService.NowMs && b.StateOrdinal > 0))
            if (_data.Row("SpecialStatusConfig", buff.StateOrdinal) is { } row
                && GameData.Int(row, "_iStaggerType") is > 0 and < 64)
                mask |= 1UL << (int)GameData.Int(row, "_iStaggerType");
        return mask;
    }

    // LC_State_Unit_Stagger.StateProcess 回傳 PROCESS_HOLD；LC_UnitBase 的 Stagger 優先於 Skill。
    // (inferred) 伺服器同步拒絕硬直中的技能，不捏造原設定沒有的 CastSkill 禁止位元。
    internal bool UnitActionBlocked(WorldUnit unit, int ordinal) => UnitForbidden(unit, ordinal)
        || ordinal == ForbiddenState.CastSkill && UnitStaggerMask(unit) != 0;

    internal bool ActionBlocked(GameSession player, int ordinal, int skillGroup = 0) =>
        !(ordinal == ForbiddenState.CastSkill && skillGroup == ControlReleaseSkillGroup)
        && (HasForbidden(player, ordinal) || ordinal == ForbiddenState.CastSkill && StaggerMask(player.Buffs) != 0);

    internal ulong UnitForbiddenMask(WorldUnit unit)
    {
        ulong mask = 0;
        for (int ordinal = 1; ordinal < 32; ordinal++)
            if (UnitForbidden(unit, ordinal)) mask |= ForbiddenMask(ordinal);
        return mask;
    }
}
