using Romsg;

namespace Ro3Emu.Server.Game;

public sealed partial class WorldInteractionService
{
    // 保留非空 carry_offset，讓 LC_VehicleHelper.ProcessVehicle 辨識這是解除而不是缺少更新。
    private static AoICarry EmptyCarry() => new() { CarryOffset = new Vec3() };

    private async Task NotifyCarryAsync(GameSession subject, AoICarry carry, bool includeSelf = true)
    {
        if (includeSelf) await SendCarryAsync(subject, subject, carry);
        foreach (var viewer in _world.Viewers(subject).ToList()) await SendCarryAsync(viewer, subject, carry);
    }

    private static Task SendCarryAsync(GameSession viewer, GameSession subject, AoICarry carry)
    {
        var info = new AoIInfoSingle { CarryInfo = new AoICarryInfo { BeCarried = carry.Clone() } };
        // 原服 31044 不帶 entity_uuid；LC_Scene_Sync._syncSelfAOIInfo 會補入本地玩家。
        if (ReferenceEquals(viewer, subject)) return viewer.SendGameAsync(MsgId.SyncAoiSelf, info);
        if (subject.Role == null) return Task.CompletedTask;
        info.EntityUuid = (ulong)subject.Role.Id;
        var total = new AoIInfoTotal { AoITickMs = 50, TickNowMs = (ulong)MonsterService.NowMs, AoiData = { info } };
        return viewer.SendGameAsync(WorldCoreService.AoiInfo, total);
    }
}
