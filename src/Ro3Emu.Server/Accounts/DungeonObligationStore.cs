using System.Text.Json;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Accounts;

/// <summary>Frozen personal dungeon outcomes. A completed row remains a natural-source tombstone.</summary>
public sealed class DungeonObligationStore(AccountStore accounts)
{
    private readonly AccountStore _accounts = accounts;

    public Task FreezeAsync(IReadOnlyList<DungeonRewardObligation> obligations) =>
        _accounts.WriteAsync<int>(new DungeonObligationFreezePlan(obligations));

    public IReadOnlyList<DungeonRewardObligation> Pending(long role) =>
        _accounts.ReadSync<List<DungeonRewardObligation>>(new DungeonObligationReadPlan(role));

    public async Task<IReadOnlyList<DungeonRewardObligation>> PendingAsync(long role) =>
        await _accounts.ReadAsync<List<DungeonRewardObligation>>(new DungeonObligationReadPlan(role));

    public IReadOnlyList<DungeonRewardObligation> PendingRoom(long room) =>
        _accounts.ReadSync<List<DungeonRewardObligation>>(new DungeonObligationReadPlan(0, room));

    public IReadOnlyList<DungeonRewardObligation> FrozenRoom(long room) =>
        _accounts.ReadSync<List<DungeonRewardObligation>>(new DungeonObligationReadPlan(0, room, includePrepared: true));

    public bool IsActivated(long room) =>
        _accounts.ReadSync<bool>(new DungeonObligationActivatedReadPlan(room));

    public Task<int> CancelPreparedAsync(long room) =>
        _accounts.WriteAsync<int>(new DungeonObligationCancelPreparedPlan(room));

    internal void EnlistActivate(long room, string killSourceKey, RoleItemPlanBuilder transaction) =>
        transaction.Add(new DungeonObligationActivatePlan(room, killSourceKey));

    internal void EnlistComplete(string key, RoleItemPlanBuilder transaction) =>
        transaction.Add(new DungeonObligationCompletePlan(key));
}

public sealed class DungeonRewardObligation
{
    public required string Key { get; init; }
    public required long RoleId { get; init; }
    public required long RoomId { get; init; }
    public required long SceneEntry { get; init; }
    public required bool Success { get; init; }
    public required long DurationMs { get; init; }
    public required bool NewRecord { get; init; }
    public required byte[] BaseState { get; init; }
    public required byte[] NextState { get; init; }
    public required List<DungeonRewardGood> Goods { get; init; }
    public required long GuildId { get; init; }
    public required int AuctionSourceType { get; init; }
    public required int PlayBook { get; init; }
    public required Dictionary<int, long> AuctionTiers { get; init; }
    public required bool RequiresKill { get; init; }
    public required string KillSourceKey { get; init; }
}

public readonly record struct DungeonRewardGood(int Entry, int Count, int Bind, EconomyAssetKind Kind);
