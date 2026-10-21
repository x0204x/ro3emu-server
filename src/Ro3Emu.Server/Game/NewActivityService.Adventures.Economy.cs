using System.Runtime.CompilerServices;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Data;
using Romsg;

namespace Ro3Emu.Server.Game;

public sealed partial class NewActivityService
{
    private EconomyAsset AdventureAsset(int entry) => _data.Row("ItemConfig", entry) is { } row
        ? new(GameData.Int(row, "_iType") == 1 ? EconomyAssetKind.Currency : EconomyAssetKind.Item, entry)
        : throw new InvalidOperationException($"Adventure economy entry {entry} is not configured.");

    /// <summary>Bind the resolved adventure outcome and its durable marker to one E03 receipt.</summary>
    private Task<int> CommitAdventureAsync(GameSession session, DBNewActivityInfo next,
        IEnumerable<long>? awardIds = null, IReadOnlyList<(int Entry, int Count)>? costs = null,
        IEnumerable<AwardGrant>? extra = null, ActivityRuntimeState? runtime = null,
        Action<RoleItemPlanBuilder>? persist = null, bool stateLockHeld = false,
        Func<Task>? beforeSync = null, Action? committed = null, string? journeySource = null,
        [CallerMemberName] string command = "") =>
        CommitAsync(session, next, awardIds, costs, extra, runtime, persist, stateLockHeld, beforeSync,
            committed: committed, journeySource: journeySource,
            economy: journeySource == null ? new EscrowEconomyIntent("activity/adventure/" + command,
                session.Account ?? "", session.Role?.Id ?? 0) : null);
}
