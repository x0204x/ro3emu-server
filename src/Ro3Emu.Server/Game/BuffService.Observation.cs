using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class BuffService
{
    /// <summary>觀察一次實際附加／刷新。AddSupportAsync 每次成功皆替換 immutable ActiveBuff record；
    /// 免疫或衰減拒絕不替換，因此不能只用同群組舊狀態判定這次成功。</summary>
    internal ActiveBuff? UnitActiveBuff(WorldUnit unit, int configId, long sourceId) =>
        _unitBuffs.TryGetValue(unit.Uid, out var buffs)
            ? buffs.FirstOrDefault(b => b.ConfigId == configId && b.SourceId == sourceId && b.ExpireMs > MonsterService.NowMs)
            : null;
}
