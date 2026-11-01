using Romsg;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class DungeonService
{
    private Vec3 AntSeatOffset()
    {
        var offset = VehicleSeatConfig.Get(_data, DeniroRide)?.Offset ?? [];
        return offset.Length >= 3 ? new Vec3 { X = offset[0] / 100f, Y = offset[1] / 100f, Z = offset[2] / 100f } : new Vec3();
    }

    private async Task NotifyAntCarrierBoardedAsync(Attempt a, GameSession s, SceneActorUnit actor)
    {
        if (!_antTraversals.TryGetValue(s, out var state) || !AntTraversalCurrent(s, state)
            || !AntSeatCurrent(s, state) || state.CarryPublished || s.Role == null) return;
        var passenger = new AoICarry { Id = (ulong)s.Role.Id, CarryOffset = AntSeatOffset() };
        state.CarryPublished = true;
        // 原服 31036 add_carries；AllCarries 為晚進 AOI 所需的同狀態快照（inferred）。
        actor.StoryCarryInfo = new AoICarryInfo { AllCarries = { passenger.Clone() } };
        await AntCarrierPassengersAsync(a, actor, new AoICarryInfo { AddCarries = { passenger } });
    }

    private async Task ClearAntCarrierPassengerAsync(AntTraversal state)
    {
        if (state.Carrier is not { } actor || state.Role == null || actor.StoryCarryInfo is not { } info) return;
        var passenger = info.AllCarries.FirstOrDefault(c => c.Id == (ulong)state.Role.Id);
        if (passenger == null) return;
        actor.StoryCarryInfo = null;
        if (state.Attempt.Room.Closed) return;
        // 原服錄製沒有旁觀者移除樣本；用既有 carry 契約清除其快照，避免重上車後留舊乘客（inferred）。
        try { await AntCarrierPassengersAsync(state.Attempt, actor, new AoICarryInfo { RemoveCarries = { passenger } }); }
        catch (Exception e) { LogAntCleanupFailed(e, state.Attempt.Plan.SceneEntry); }
    }

    private Task AntCarrierPassengersAsync(Attempt a, SceneActorUnit actor, AoICarryInfo carries) =>
        BroadcastAsync(a, WorldCoreService.AoiInfo, new AoIInfoTotal
        {
            AoITickMs = 50, TickNowMs = (ulong)MonsterService.NowMs,
            AoiData = { new AoIInfoSingle { EntityUuid = (ulong)actor.Uid, CarryInfo = carries } },
        });
}
