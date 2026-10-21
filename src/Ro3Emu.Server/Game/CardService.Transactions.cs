using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class CardService
{
    /// <summary>commit 5009 card locations and 5026 selection as one owned state change.</summary>
    private async Task CommitCardStateAsync(GameSession session, List<InvItem> staged,
        DBStorageHoleInfo? plans, string kind, object outcome)
    {
        session.Owners!.Current!.BindPresentation(session);
        var builder = _roles.NewTransaction();
        int itemReceipt = builder.ItemDelta(session.Role!.Id, session.Items, staged);
        if (plans != null) builder.RoleBlob(session.Role.Id, "card_plans", plans.ToByteArray());
        builder.WithEconomy(_character.FamilyEconomy(session, kind, [], [], outcome));
        await _roles.CommitAsync(builder, kind, receipts =>
        {
            CharacterService.ApplyStagedItems(session, staged, (long[])receipts[itemReceipt]!);
            if (plans != null) session.CardPlans = plans;
        });
    }

    private InvItem StageTakeOne(List<InvItem> staged, InvItem stack)
    {
        if (stack.Stack <= 1) return stack;
        stack.Stack--;
        var one = CloneOne(stack);
        staged.Add(one);
        return one;
    }

    private InvItem StageToBag(List<InvItem> staged, InvItem card)
    {
        int limit = _data.Row("ItemConfig", card.Entry) is { } row ?
            (int)Math.Max(1, GameData.Int(row, "_iStackLimit")) : 1;
        if (staged.FirstOrDefault(i => i.Grid == 0 && i.Entry == card.Entry && i.Stack < limit
            && SameState(i, card) && i.IsLocked == card.IsLocked) is { } stack)
        {
            stack.Stack++;
            staged.Remove(card);
            return stack;
        }
        card.Grid = 0;
        card.Sort = staged.Where(i => i.Grid == 0 && i.Pack == card.Pack)
            .Select(i => i.Sort).DefaultIfEmpty().Max() + 1;
        return card;
    }

    private static void StageSingleTarget(List<InvItem> staged, InvItem card)
    {
        if (card.Stack <= 1) return;
        var remainder = CloneOne(card);
        remainder.Stack = card.Stack - 1;
        remainder.Sort = staged.Where(i => i.Grid == 0 && i.Pack == card.Pack)
            .Select(i => i.Sort).DefaultIfEmpty().Max() + 1;
        card.Stack = 1;
        staged.Add(remainder);
    }

    private static List<(int Entry, long Count)> CardPaymentCosts(GameSession session,
        IReadOnlyList<(int Entry, long Count)> currency, IReadOnlyDictionary<long, int> instances)
    {
        var costs = currency.ToList();
        foreach (var (guid, count) in instances)
            costs.Add((session.Items.Single(item => item.Guid == guid).Entry, count));
        return costs;
    }
}
