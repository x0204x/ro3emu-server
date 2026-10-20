using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class CharacterService
{
    /// <summary>EquipmentService supplies the saved loot filter and the same returns as manual dismantling.</summary>
    internal Func<GameSession, InvItem, IReadOnlyList<(int Entry, long Count)>>? AutoDecomposeReturns { get; set; }

    private sealed record AutoDecomposePlan(ItemAutoDecomposeTipsNtf Notice, List<(InvItem Item, int Count)> Sources,
        Dictionary<int, int> Gains);

    /// <summary>
    /// Evaluate the saved filter without changing the bag. Only the newly gained quantity is eligible, even when
    /// acquisition restacks an old instance. 31330 has matching source and gain maps.
    /// </summary>
    private AutoDecomposePlan PlanAutoDecompose(GameSession session, IEnumerable<InvItem> received, IReadOnlyDictionary<long, int> before)
    {
        var notice = new ItemAutoDecomposeTipsNtf();
        var gains = new Dictionary<int, int>();
        var sources = new List<(InvItem Item, int Count)>();
        foreach (var item in received.Distinct())
        {
            int count = item.Stack - before.GetValueOrDefault(item.Guid);
            if (count <= 0 || item.IsLocked || item.Grid != 0 || AutoDecomposeReturns?.Invoke(session, item) is not { Count: > 0 } returns) continue;
            var rewards = returns.GroupBy(r => r.Entry).ToDictionary(g => g.Key, g => g.Sum(r => r.Count) * count);
            // (inferred) Keep the loot intact if the protocol's int32 count cannot represent its complete return.
            if ((long)notice.DecomposeItemInfo.GetValueOrDefault(item.Entry) + count > int.MaxValue
                || rewards.Any(r => r.Value <= 0 || r.Value + gains.GetValueOrDefault(r.Key) > int.MaxValue)) continue;
            sources.Add((item, count));
            notice.DecomposeItemInfo[item.Entry] = notice.DecomposeItemInfo.GetValueOrDefault(item.Entry) + count;
            if (!notice.DecomposeGainItemInfo.TryGetValue(item.Entry, out var sourceGains))
                notice.DecomposeGainItemInfo[item.Entry] = sourceGains = new ItemAutoDecomposeGainItems();
            foreach (var (entry, amount) in rewards)
            {
                gains[entry] = gains.GetValueOrDefault(entry) + (int)amount;
                sourceGains.ItemInfo[entry] = sourceGains.ItemInfo.GetValueOrDefault(entry) + (int)amount;
            }
        }
        return new AutoDecomposePlan(notice, sources, gains);
    }

    private ItemAutoDecomposeTipsNtf AutoDecomposeLoot(GameSession session, IEnumerable<InvItem> received, IReadOnlyDictionary<long, int> before)
    {
        var plan = PlanAutoDecompose(session, received, before);
        foreach (var (item, count) in plan.Sources) TakeFromStack(session, item, count, [], []);
        foreach (var (entry, count) in plan.Gains)
            foreach (var item in AddItemCore(session, entry, count, out _)) session.NewItems.Add(item.Guid);
        return plan.Notice;
    }

    /// <summary>Quest award returns join its detached item/progress snapshot before the one SQLite commit.</summary>
    private ItemAutoDecomposeTipsNtf StageAutoDecomposeAward(GameSession session, List<InvItem> items, RoleProgress progress,
        IEnumerable<InvItem> received, IReadOnlyDictionary<long, int> before, List<(int Entry, int Count)> gained)
    {
        var plan = PlanAutoDecompose(session, received, before);
        foreach (var (item, count) in plan.Sources)
        {
            item.Stack -= count;
            if (item.Stack == 0) items.Remove(item);
        }
        // Returns are granted once. They are not fed back to the saved auto filter.
        foreach (var (entry, count) in plan.Gains)
        {
            var row = data.Row("ItemConfig", entry) ?? throw new InvalidOperationException($"Missing dismantle return {entry}");
            if (GameData.Int(row, "_iType") == ItemTypeCurrency)
            {
                progress.Currency[entry] = checked(progress.Currency.GetValueOrDefault(entry) + count);
                continue;
            }
            int limit = GameData.Int(row, "_iType") == ItemTypeEquip ? 1 : (int)Math.Max(1, GameData.Int(row, "_iStackLimit"));
            int pack = EmblemService.IsEmblem(data, entry) ? 5 : ConfigPack(row);
            int left = count;
            foreach (var item in items.Where(i => CanStackGrant(i, entry, limit, 0)))
            {
                int add = Math.Min(left, limit - item.Stack);
                item.Stack += add;
                left -= add;
                if (left == 0) break;
            }
            while (left > 0)
            {
                int stack = Math.Min(left, limit);
                var item = new InvItem
                {
                    Entry = entry, Stack = stack, Pack = pack,
                    Sort = items.Where(i => i.Grid == 0 && i.Pack == pack).Select(i => i.Sort).DefaultIfEmpty(0).Max() + 1,
                    Attrs = EmblemAttrFactory?.Invoke(entry) ?? RollAttrs(entry)
                };
                items.Add(item);
                left -= stack;
            }
            gained.Add((entry, count));
        }
        return plan.Notice;
    }
}
