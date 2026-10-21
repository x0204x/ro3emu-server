using System.Runtime.CompilerServices;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

/// <summary>
/// One 31323 use of an item whose server-hosted ItemConfig._iUseEffect entries (ActionConfig _iEXECHost 1) all name one
/// registered action, e.g. 1007011 [[207,10011]] or 1910003 [[349,10003],[349,10004]]. <see cref="Effects"/> holds the
/// numeric arguments of each such entry, the action id first; client-hosted entries ([104], [106]) are left out.
/// </summary>
internal sealed record ItemActionUse(GameSession Session, ItemUseReq Request, InvItem Item, JsonElement Row, int Count,
    IReadOnlyList<long[]> Effects)
{
    /// <summary>Argument <paramref name="n"/> (1-based after the action id) of the first entry, 0 when absent.</summary>
    public long Arg(int n) => Effects.Count > 0 && Effects[0].Length > n ? Effects[0][n] : 0;

    /// <summary>Work that follows the 31323 reply (a Quest Fly Wing's jump, as a Fly Wing's), after the item is spent.</summary>
    public Func<Task>? AfterReply { get; set; }
}

public sealed partial class CharacterService
{
    private sealed record ItemAction(Func<ItemActionUse, Task<int>> Handler, bool TakesParam,
        Func<GameSession, Ownership.ReservationKey[]>? Keys);

    private readonly Dictionary<int, ItemAction> _itemActions = new();
    private readonly ConditionalWeakTable<GameSession, Func<Task>> _itemActionAfterReply = new();

    /// <summary>
    /// Client-hosted actions an item may consist of alone. The client runs them after a successful 31323
    /// (LC_DAL_Item._processItemUse, DoInteractionList), so the server only answers, spending the item or not:
    /// 1 OpenUI (1113008 Melody Conch opens shop 402, whose currency it is: (inferred) not spent),
    /// 2 UseSkill (1201034 Primordial Transmutation Manual, a one-shot behind a confirmation: (inferred) spent).
    /// </summary>
    private static readonly Dictionary<int, bool> ClientOnlyActions = new() { [1] = false, [2] = true };

    /// <summary>
    /// The owner of a server-hosted use action. The handler validates, then spends the item through
    /// <see cref="CommitItemActionAsync"/> (or its own commit) and answers 0; any other code keeps the item and is the
    /// 31323 result. <paramref name="takesParam"/>: the client sends one ItemUseParamInfo with it (105 a horn's text).
    /// <paramref name="keys"/>: what the use reserves beyond the role (a guild, the realm); 31323 of such an item is
    /// admitted as an economic request and the item-use scope reserves them (<see cref="EnterItemUseAsync"/>).
    /// </summary>
    internal void RegisterItemAction(int action, Func<ItemActionUse, Task<int>> handler, bool takesParam = false,
        Func<GameSession, Ownership.ReservationKey[]>? keys = null)
    {
        if (_itemActions.Count == 0) SpecialItemUse.Add(UseItemActionAsync);
        _itemActions.Add(action, new ItemAction(handler, takesParam, keys));
    }

    /// <summary>The single registered action among the item's server-hosted use effects, if any.</summary>
    private int? ItemActionOf(JsonElement row)
    {
        if (GameData.Int(row, "_iIsUse") == 0) return null;
        var server = UseActions(row).Where(a => !ClientHosted(a)).Distinct().ToList();
        return server.Count == 1 && _itemActions.ContainsKey(server[0]) ? server[0] : null;
    }

    /// <summary>An item made only of one <see cref="ClientOnlyActions"/> action.</summary>
    private int? ClientOnlyActionOf(JsonElement row)
    {
        if (GameData.Int(row, "_iIsUse") == 0 || _itemActions.Count == 0) return null;
        var actions = UseActions(row).Distinct().ToList();
        return actions.Count == 1 && ClientOnlyActions.ContainsKey(actions[0]) ? actions[0] : null;
    }

    /// <summary>Shortcut eligibility (Bag.cs CanUseItemEffects) for registered and client-only actions.</summary>
    private bool HandlesItemAction(JsonElement row) => ItemActionOf(row) != null || ClientOnlyActionOf(row) != null;

    /// <summary>The 31323 parameter gate (Bag.cs): an action registered with takesParam accepts exactly one.</summary>
    private bool AcceptsItemActionParam(JsonElement row, ItemUseReq req) =>
        ItemActionOf(row) is { } action && _itemActions[action].TakesParam && req.ItemParam.Count == 1;

    /// <summary>The keys a use of this bag item reserves beyond the role, null for a role-only use.</summary>
    private Ownership.ReservationKey[]? ItemUseKeys(GameSession session, long guid)
    {
        if (session.Role == null || session.Items.FirstOrDefault(i => i.Guid == guid && i.Grid == 0) is not { } item
            || data.Row("ItemConfig", item.Entry) is not { } row || ItemActionOf(row) is not { } action
            || _itemActions[action].Keys is not { } keys) return null;
        return keys(session);
    }

    /// <summary>31323 admission: an economic request when the use reserves more than the role (CharacterMessages).</summary>
    internal bool ItemUseWidens(GameSession session, ReadOnlyMemory<byte> body)
    {
        try { return ItemUseKeys(session, ItemUseReq.Parser.ParseFrom(body.Span).ItemGuid) != null; }
        catch (Google.Protobuf.InvalidProtocolBufferException) { return false; }
    }

    /// <summary>
    /// The item use's owner scope: the role, and what the item's action reserves. The keys follow guild membership,
    /// which may change while the use waits: re-admit until the admitted reservation still covers them. Inside an
    /// already admitted scope (31337 shortcut use) the role is borrowed and an action whose keys it lacks refuses.
    /// </summary>
    private async Task<IDisposable?> EnterItemUseAsync(GameSession session, ItemUseReq req)
    {
        if (session.Owners is not { } owners) return null;
        var role = Ownership.ReservationKey.Role(session.Role!.Id);
        while (true)
        {
            var extra = ItemUseKeys(session, req.ItemGuid) ?? [];
            if (extra.Length != 0 && owners.Current is { RuntimeOnly: false } && !owners.CurrentCovers([role, .. extra])) extra = [];
            var scope = await owners.EnterAsync("character item use", [role, .. extra]);
            if (extra.Length == 0 || owners.Reservations.Covers(scope.Context.Operation, ItemUseKeys(session, req.ItemGuid) ?? []))
                return scope;
            scope.Dispose();
        }
    }

    private async Task<int?> UseItemActionAsync(GameSession session, ItemUseReq req, InvItem item, JsonElement row, int count)
    {
        if (ClientOnlyActionOf(row) is { } client)
            return req.ItemParam.Count > 0 ? ErrorCodes.Parameter : await UseClientOnlyItemAsync(session, req, item, row, count, client);
        if (ItemActionOf(row) is not { } action) return null;
        var owner = _itemActions[action];
        if (!owner.TakesParam && req.ItemParam.Count > 0) return ErrorCodes.Parameter;
        if (owner.Keys is { } keys && session.Owners is { } owners && !owners.CurrentCovers(keys(session)))
            return ErrorCodes.FeatureUnavailable;
        var effects = row.GetProperty("_iUseEffect").EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.Array && e.GetArrayLength() > 0 && GameData.Number(e[0]) == action)
            .Select(e => e.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray())
            .ToList();
        var use = new ItemActionUse(session, req, item, row, count, effects);
        int result = await owner.Handler(use);
        if (result == 0 && use.AfterReply != null) _itemActionAfterReply.AddOrUpdate(session, use.AfterReply);
        return result;
    }

    /// <summary>Bag.cs, after the 31323 reply of a specialized use: the action's follow-up, if it left one.</summary>
    private async Task AfterItemActionAsync(GameSession session)
    {
        if (!_itemActionAfterReply.TryGetValue(session, out var after)) return;
        _itemActionAfterReply.Remove(session);
        await after();
    }

    private async Task<int> UseClientOnlyItemAsync(GameSession session, ItemUseReq req, InvItem item, JsonElement row, int count,
        int action)
    {
        if (!ClientOnlyActions[action]) return 0;
        var use = new ItemActionUse(session, req, item, row, count, [[action]]);
        return await CommitItemActionAsync(use, null, null, new { ClientAction = action }) ? 0 : ErrorCodes.ItemsShort;
    }

    /// <summary>
    /// Spends <see cref="ItemActionUse.Count"/> of the used stack with the action's role state (<paramref name="persist"/>
    /// writes the role columns, <paramref name="committed"/> publishes them) and the item's cooldown in one commit.
    /// The family kind (<see cref="CharacterFamilyEconomy.ItemActionKind"/> unless the owner's family takes the
    /// columns) has the item as its only cost. The caller holds StateLock (31323 does). False: the stack changed.
    /// </summary>
    internal async Task<bool> CommitItemActionAsync(ItemActionUse use, Action<RoleItemPlanBuilder>? persist, Action? committed,
        object outcome, string kind = CharacterFamilyEconomy.ItemActionKind)
    {
        var s = use.Session;
        return await ExchangeLockedAsync(s, [], [], new Dictionary<long, int> { [use.Item.Guid] = use.Count }, popup: false,
            sync: false, persist: tx =>
            {
                persist?.Invoke(tx);
                tx.WithEconomy(FamilyEconomy(s, kind, [(use.Item.Entry, use.Count)], [],
                    new { Item = use.Item.Entry, use.Count, use.Effects, Outcome = outcome }));
            }, committed: committed, asyncCommit: true) != null;
    }

    /// <summary>
    /// The same for an action whose state is not the role's (a guild's unlocked badges, an Adventure Group's card
    /// backgrounds): <paramref name="persist"/> enlists the shared record, and the item leaves as an E05d ItemUse sink
    /// (<paramref name="kind"/>, e.g. "item-action/guild-badge"). The item-use scope must hold the record's keys.
    /// </summary>
    internal async Task<bool> CommitSharedItemActionAsync(ItemActionUse use, string kind, Action<RoleItemPlanBuilder> persist,
        Action? committed)
    {
        var s = use.Session;
        bool currency = data.Row("ItemConfig", use.Item.Entry) is { } row && GameData.Int(row, "_iType") == ItemTypeCurrency;
        var sink = new EconomyEffect(new(EconomyAssetKind.Item, use.Item.Entry),
            new(EconomyOwnerKind.Role, s.Role!.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            EconomyCompartment.Inventory, -use.Count, EconomyLegKind.Sink, EconomyReason.ItemUse);
        if (currency) throw new InvalidOperationException("An item action spends a bag item.");
        string operation = Guid.NewGuid().ToString("N");
        var envelope = new EconomyEnvelope(operation, s.Account!, s.Role.Id, kind, kind, kind + ":" + operation,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), [sink], []);
        // E05d freezes the staged children when it binds, so the item use's cooldown (the 31323 scope's bag_state) is
        // staged here, before it, and the scope is published with the commit instead of by the exchange.
        var scope = LifeItemExchange.Value is { Active: true, Committed: false } live && ReferenceEquals(live.Session, s) ? live : null;
        if (scope != null) scope.Active = false;
        try
        {
            return await ExchangeLockedAsync(s, [], [], new Dictionary<long, int> { [use.Item.Guid] = use.Count }, popup: false,
                sync: false, persist: tx => { persist(tx); scope?.Persist(tx); tx.WithEconomy(envelope); },
                committed: () =>
                {
                    if (scope != null) { scope.Committed = true; scope.Publish(); }
                    committed?.Invoke();
                }, asyncCommit: true) != null;
        }
        finally { if (scope != null) scope.Active = true; }
    }
}
