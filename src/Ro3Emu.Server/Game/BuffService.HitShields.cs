using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class BuffService
{
    /// <summary>命中層數盾沿原 MaxStack／StackType 上限。
    /// (inferred) 保留舊剩餘吸收量，加上本次新增層的容量後封頂；重新整理原持續時間，
    /// 不把已消耗的舊層整批補滿，也不因層數已滿而拒絕後續有效命中的容量。</summary>
    internal Task AddHitShieldAsync(GameSession player, int configId, int duration, long sourceId, int layers, long perLayer)
    {
        if (layers <= 0 || perLayer <= 0 || BuffConfig.Get(_data, configId) is not { StackType: 7 } config)
            return Task.CompletedTask;
        int cap = checked((int)Math.Max(1, config.MaxStack));
        var old = player.Buffs.FirstOrDefault(b => b.ExpireMs > MonsterService.NowMs
            && _data.Row("BuffConfig", b.ConfigId) is { } row && GameData.Int(row, "_iBuffID") == config.BuffId);
        int piles = Math.Min(cap, (old?.PileCount ?? 0) + Math.Min(layers, cap));
        long maximum = (long)Math.Clamp((double)perLayer * cap, 0, long.MaxValue);
        long remaining = (long)Math.Clamp((old?.ShieldRemaining ?? 0) + (double)perLayer * Math.Min(layers, cap), 0, maximum);
        return AddCoreAsync(player, configId, duration, sourceId, piles, remaining);
    }
}
