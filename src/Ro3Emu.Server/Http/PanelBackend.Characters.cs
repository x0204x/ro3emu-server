using System.Globalization;
using System.Text.Json;
using Ro3Emu.Panel;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Server.Http;

/// <summary>
/// The panel's character sheet and mailbox viewer, read-only. A character in the game is copied from its session on the
/// game loop (its bag, progress and position change there first); any other is read from the database on the read
/// pool. The mailbox is the role's "mailbox" blob, which MailService commits before every change, decoded with
/// MailService's own types.
/// </summary>
public sealed partial class PanelBackend
{
    /// <summary>A character's state, copied off the loop or read from the database.</summary>
    private sealed record CharacterState(Role Role, RoleProgress Progress, List<InvItem> Items, EquipLevels Levels, long Scene,
        double X, double Y, double Z, bool Live);

    private static readonly string[] BaseStats = ["STR", "AGI", "VIT", "INT", "DEX", "LUK"];
    private const int ZenyItem = 1, CartPack = 100;

    private Dictionary<long, (string Name, string Icon)>? _skillNames;

    public async Task<PanelCharacterSheet?> CharacterAsync(long id)
    {
        if (await CharacterStateAsync(id) is not { } state) return null;
        var (role, progress) = (state.Role, state.Progress);
        var currencies = progress.Currency.Where(c => c.Value != 0).OrderBy(c => c.Key != ZenyItem).ThenBy(c => c.Key)
            .Select(c => new PanelCurrency(c.Key, ItemName(c.Key), ItemIconOf(c.Key), c.Value)).ToList();
        var stats = BaseStats.Select((name, i) => new PanelStat(name, progress.Stats[i].ToString(CultureInfo.InvariantCulture))).ToList();
        return new PanelCharacterSheet(role.Id, role.Name, role.Account, progress.Career, ClassName(progress.Career), ClassIcon(progress.Career),
            role.Sex == 1 ? "Male" : "Female", progress.Level, progress.JobLevel, progress.Exp, progress.JobExp, progress.ParagonLevel, state.Scene,
            MapName(state.Scene), MapImage(state.Scene), Math.Round(state.X, 1), Math.Round(state.Y, 1), Math.Round(state.Z, 1),
            DateTimeOffset.FromUnixTimeSeconds(role.CreateTime), Unix(role.LastLoginTime), state.Live, state.Live, stats, progress.BonusPoints,
            currencies, Equipment(state), Bags(state.Items), Skills(progress));
    }

    /// <summary>The live session's copy when the character is in the game, else the database's.</summary>
    private async Task<CharacterState?> CharacterStateAsync(long id)
    {
        try
        {
            var live = await loop.InvokeAsync(() => Task.FromResult(LiveState(id)));
            if (live != null) return live;
        }
        catch (OperationCanceledException) { } // the loop no longer admits work: the server is stopping
        if ((await accounts.ReadAsync<List<Role>>(new RoleProjectionRead(RoleProjectionKind.ById, id: id))).FirstOrDefault() is not { } stored) return null;
        var saved = await accounts.ReadAsync<RoleProgress>(new RoleProgressReadPlan(id));
        var items = await accounts.ReadAsync<List<InvItem>>(new ItemReadPlan(id));
        var levels = await accounts.ReadAsync<byte[]?>(new RoleBlobReadPlan(id, "equip_levels")) is { Length: > 0 } blob
            ? JsonSerializer.Deserialize<EquipLevels>(blob) ?? new() : new();
        return new CharacterState(stored, saved, items, levels, stored.SceneEntry, stored.X, stored.Y, stored.Z, Live: false);
    }

    /// <summary>On the loop: a copy of the character's session, so nothing the panel reads changes under it; null when it is
    /// not in the game.</summary>
    private CharacterState? LiveState(long id)
    {
        if (sessions.All().FirstOrDefault(s => s.Role?.Id == id) is not { Role: { } role, Progress: { } progress } session) return null;
        var copy = new RoleProgress
        {
            Career = progress.Career, Level = progress.Level, JobLevel = progress.JobLevel, Exp = progress.Exp, JobExp = progress.JobExp,
            BonusPoints = progress.BonusPoints, ParagonLevel = progress.ParagonLevel, ParagonExp = progress.ParagonExp,
        };
        progress.Stats.CopyTo(copy.Stats, 0);
        foreach (var (key, value) in progress.Currency) copy.Currency[key] = value;
        foreach (var (key, value) in progress.Skills) copy.Skills[key] = value;
        var levels = new EquipLevels { Strength = new(session.EquipLevels.Strength), Refine = new(session.EquipLevels.Refine), Luck = new(session.EquipLevels.Luck) };
        var at = session.Position;
        return new CharacterState(role, copy, [.. session.Items.Select(i => i.Clone())], levels, session.SceneEntry, at.X, at.Y, at.Z, Live: true);
    }

    private string ItemName(long item) => data.ItemName(item) is { Length: > 0 } name ? name : "Item " + item.ToString(CultureInfo.InvariantCulture);

    private PanelHeldItem Held(InvItem item) => new(item.Guid, item.Entry, ItemName(item.Entry), ItemIconOf(item.Entry),
        (int)(ItemConfig.Get(data, item.Entry)?.Quality ?? 1), item.Stack, item.IsLocked, item.BindType != 0);

    /// <summary>Worn items by slot (EquipGridType), each with its slot's strengthen and refine levels and its enchantments.</summary>
    private List<PanelEquipped> Equipment(CharacterState state) =>
        [.. state.Items.Where(i => i.Grid > 0).OrderBy(i => i.Grid).Select(i => new PanelEquipped(i.Grid, SlotName(i.Grid), Held(i),
            state.Levels.Strength.GetValueOrDefault(i.Grid), state.Levels.Refine.GetValueOrDefault(i.Grid),
            [.. i.Enchantments.OrderBy(e => e.Key).SelectMany(e => e.Value.Attr)
                .Select(a => new PanelStat(AttributeName(a.Key), "+" + AttributeValue(a.Key, a.Value)))]))];

    private static string SlotName(int grid) =>
        Enum.IsDefined(typeof(Romsg.EquipGridType), grid) ? Words(((Romsg.EquipGridType)grid).ToString()) : "Slot " + grid.ToString(CultureInfo.InvariantCulture);

    private List<PanelBag> Bags(List<InvItem> items) =>
        [.. items.Where(i => i.Grid == 0 && i.Stack > 0).GroupBy(i => i.Pack).OrderBy(g => g.Key)
            .Select(g => new PanelBag(g.Key, PackName(g.Key), [.. g.OrderBy(i => i.Sort).ThenBy(i => i.Guid).Select(Held)]))];

    /// <summary>(inferred) The bag tabs as the items they hold suggest (BackpackConfig _iTabName has no text the export
    /// carries); 100 is the merchant's cart (CareerSpecialService).</summary>
    private static string PackName(int pack) => pack switch
    {
        1 => "Items",
        2 => "Equipment",
        3 => "Cards",
        4 => "Totems",
        5 => "Emblems",
        CartPack => "Cart",
        _ => "Bag " + pack.ToString(CultureInfo.InvariantCulture),
    };

    private List<PanelSkill> Skills(RoleProgress progress)
    {
        var names = SkillNames();
        return [.. progress.Skills.Where(s => s.Value > 0).Select(s => names.TryGetValue(s.Key, out var skill)
                ? new PanelSkill(s.Key, skill.Name, Image("skills", skill.Icon), s.Value)
                : new PanelSkill(s.Key, "Skill " + s.Key.ToString(CultureInfo.InvariantCulture), "", s.Value))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Each skill group's name and icon file, from its first SkillConfig row; built once.</summary>
    private Dictionary<long, (string Name, string Icon)> SkillNames()
    {
        if (_skillNames is { } built) return built;
        var names = new Dictionary<long, (string, string)>();
        foreach (var row in SkillConfig.All(data).Where(r => r.SkillId > 0).OrderBy(r => r.Level))
            if (!names.ContainsKey(row.SkillId) && Name(row.Name) is { Length: > 0 } name) names[row.SkillId] = (name, row.Icon);
        return _skillNames = names;
    }

    // ---------------------------------------------------------------- mail

    public async Task<PanelMailPage?> MailAsync(long id, int page, int pageSize)
    {
        if ((await accounts.ReadAsync<List<Role>>(new RoleProjectionRead(RoleProjectionKind.ById, id: id))).FirstOrDefault() is not { } role) return null;
        var blob = await accounts.ReadAsync<byte[]?>(new RoleBlobReadPlan(id, "mailbox"));
        MailService.Mailbox box;
        bool readable = true;
        try { box = blob is { Length: > 0 } ? JsonSerializer.Deserialize<MailService.Mailbox>(blob) ?? new() : new(); }
        catch (JsonException)
        {
            box = new();
            readable = false;
        }
        var letters = box.Letters.OrderByDescending(l => l.CreatedTime).ThenByDescending(l => l.Id, StringComparer.Ordinal).ToList();
        return new PanelMailPage(role.Id, role.Name, [.. letters.Skip((page - 1) * pageSize).Take(pageSize).Select(Mail)], letters.Count,
            page, pageSize, readable);
    }

    /// <summary>
    /// One letter: its own title and text, or for a template mail (TemplateId) MailConfig's _iTitle and _iContent with
    /// the colour marks left out. Attachments are configured grants, item instances (market mail) and currencies.
    /// </summary>
    private PanelLetter Mail(MailService.Letter letter)
    {
        var template = letter.TemplateId > 0 ? MailConfig.Get(data, letter.TemplateId) : null;
        string title = letter.Title.Length > 0 ? letter.Title : template is { } t ? data.TextOf(t.Title) : "";
        string text = letter.Content.Length > 0 ? letter.Content : template is { } c ? data.TextOf(c.Content) : "";
        var attachments = letter.Attachments.Select(a => MailItem(a.Entry, a.Count))
            .Concat(letter.Items.Select(i => MailItem(i.Entry, i.Stack)))
            .Concat(letter.Currency.OrderBy(c => c.Key).Select(c => MailItem(c.Key, c.Value))).ToList();
        return new PanelLetter(letter.Id, ColourMark().Replace(title, "").Trim(), ColourMark().Replace(text, "").Trim(),
            letter.Sender.Length > 0 ? letter.Sender : "System", DateTimeOffset.FromUnixTimeSeconds(letter.CreatedTime),
            letter.RemoveTime > 0 ? DateTimeOffset.FromUnixTimeSeconds(letter.RemoveTime) : null, letter.Read, letter.Claimed, attachments);
    }

    private PanelLetterItem MailItem(long item, long count) =>
        new(item, ItemName(item), ItemIconOf(item), (int)(ItemConfig.Get(data, item)?.Quality ?? 1), count);
}
