using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class BuffService
{
    // GloriaDomini 的原 Buff11508 列缺失；SP扣除尊重泛用負面免疫是 inferred，
    // 不捏造原 Buff、控制tag或套用只免疫控制的分類20。
    internal bool ResourceDrainImmune(GameSession player) => ResourceDrainImmune(player.Buffs);
    internal bool ResourceDrainImmune(WorldUnit unit) => ResourceDrainImmune(UnitBuffs(unit));

    private bool ResourceDrainImmune(IEnumerable<ActiveBuff> buffs)
    {
        long now = MonsterService.NowMs;
        return buffs.Any(buff => buff.ExpireMs > now
            && (buff.ShieldDebuffImmunity && buff.ShieldRemaining > 0
                || BuffConfig.Get(_data, buff.ConfigId) is { } row
                && (row.BuffCategory.Contains(DebuffImmunityCategory) || row.BuffCategory.Contains(AllNegativeImmunityCategory))));
    }
}
