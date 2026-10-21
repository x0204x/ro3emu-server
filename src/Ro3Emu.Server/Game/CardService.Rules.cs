using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class CardService
{
    /// <summary>Socketed cards changed (insert, remove, transfer, plan switch, star-up of a socketed copy):
    /// CardEffects reconciles the card-owned passive buffs.</summary>
    public List<Func<GameSession, Task>> SocketsChanged { get; } = new();

    private async Task NotifySocketsChangedAsync(GameSession session)
    {
        foreach (var handler in SocketsChanged)
            try { await handler(session); }
            catch (Exception e) { _log.LogError(e, "Card socket observer failed for role {Role}", session.Role?.Id); }
    }

    internal Task SocketsChangedAsync(GameSession session) => NotifySocketsChangedAsync(session);

    /// <summary>
    /// LC_CardViewHelper.CheckCardInsert (lua:1010-1149) against the cards in every other socket of the active layout:
    /// ItemConfig._iIfSameCard = 1 refuses a second copy of the same card id (UIWords CardInsertNotCanSame, ErrorCode
    /// 1816 "Only one card with the same name can be socketed"), _iIsEleCard = 1 refuses a second fixed-element card
    /// (CardInsertNotCanFixElement, 1818 "Only one card that changes armor elemental properties"), a nonzero
    /// _iCardGroup refuses a second card of that group (CardInsertNotCanSameGroup; no row uses a group in this client).
    /// The client checks them in this order before 31406. (inferred) The group refusal reuses 1816: no error code names
    /// a card group.
    /// </summary>
    private int InsertRuleError(int entry, IEnumerable<int> others)
    {
        if (_data.Row("ItemConfig", entry) is not { } row) return 0;
        var socketed = others.ToList();
        if (GameData.Int(row, "_iIfSameCard") != 0 && socketed.Contains(entry)) return ErrorCodes.CardSameName;
        if (GameData.Int(row, "_iIsEleCard") != 0 && socketed.Any(IsElementCard)) return ErrorCodes.CardSameElement;
        long group = GameData.Int(row, "_iCardGroup");
        if (group != 0 && socketed.Any(other => CardGroup(other) == group)) return ErrorCodes.CardSameName;
        return 0;
    }

    private bool IsElementCard(int entry) => _data.Row("ItemConfig", entry) is { } row && GameData.Int(row, "_iIsEleCard") != 0;
    private long CardGroup(int entry) => _data.Row("ItemConfig", entry) is { } row ? GameData.Int(row, "_iCardGroup") : 0;

    /// <summary>Card ids socketed anywhere except the listed (grid, socket) addresses.</summary>
    private static IEnumerable<int> SocketedExcept(IEnumerable<InvItem> items, params (int Grid, int Socket)[] excluded) => items
        .Where(i => i.Grid < 0 && !excluded.Contains(CharacterService.CardSocket(i))).Select(i => i.Entry);
}
