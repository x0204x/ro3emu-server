using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class EmblemService
{
    /// <summary>
    /// GMCreateLvEmblem 174 (the class presets): a new Soul Echo of that item at a level, its attributes rolled as 82424's
    /// level-ups roll them, deployed in the active plan's first free slot. (inferred) The level is capped at the quality's
    /// EmblemLvConfig maximum and its exp is the levels' total; the Soul Echo function gate is not checked; with every slot
    /// taken (or another season's echo) it stays in the bag.
    /// </summary>
    internal async Task<string> GmCreateAsync(GameSession session, int entry, int level)
    {
        if (!IsEmblem(_data, entry)) return $"{entry} is not a Soul Echo.";
        var owned = session.Items.Where(i => i.Entry == entry).Select(i => i.Guid).ToHashSet();
        await _character.AddItemAsync(session, entry, 1);
        using var _ = await CharacterService.Lock(session);
        if (session.Items.Where(i => i.Entry == entry && i.Grid == 0 && !owned.Contains(i.Guid)).MaxBy(i => i.Guid) is not { } created)
            return $"Soul Echo {entry} was not created.";
        int quality = Quality(entry);
        int target = Math.Clamp(level, 0, EmblemLvConfig.All(_data).Where(r => r.Quality == quality).Select(r => (int)r.Lv).DefaultIfEmpty(0).Max());
        var beforeItems = session.Items.Select(i => i.Clone()).ToArray();
        var afterItems = beforeItems.Select(i => i.Clone()).ToList();
        var echo = afterItems.Single(i => i.Guid == created.Guid);
        var attrs = echo.Attrs == null ? GenerateAttrs(entry) ?? [] : new(echo.Attrs);
        long totalExp = attrs.GetValueOrDefault(TotalExp);
        for (int next = (int)attrs.GetValueOrDefault(Level) + 1; next <= target; next++)
        {
            totalExp += LevelRow(quality, next) is { } row ? new EmblemLvConfig(row).Exp : 0;
            AddPrimaryLevel(attrs, entry);
        }
        attrs[Level] = target;
        attrs[Exp] = 0;
        attrs[TotalExp] = totalExp;
        AddMarks(attrs, entry, target);
        AddSubattrs(attrs, entry, target);
        var state = CopyState(session.Emblems);
        var plan = state.Plans[state.CurrentPlan];
        int slot = SeasonValid(entry) ? Enumerable.Range(FirstSlot, LastSlot - FirstSlot + 1).FirstOrDefault(s => !plan.Slots.ContainsKey(s)) : 0;
        if (slot != 0)
        {
            plan.Slots[slot] = echo.Guid;
            attrs[Source] = 2;
            attrs[Position] = slot;
        }
        echo.Attrs = attrs;
        long previous = session.EmblemPower;
        await CommitEmblemsAsync(session, "emblem-gm-create", state, beforeItems, afterItems, [],
            new { Entry = entry, echo.Guid, Level = target, Slot = slot, Attributes = attrs });
        await ChangedAsync(session, previous, inventory: true);
        return $"Soul Echo {_data.ItemName(entry)} ({entry}) level {target}" + (slot != 0 ? $" in slot {slot}." : ", in the bag (no free slot).");
    }
}
