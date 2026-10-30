namespace Ro3Emu.Panel;

/// <summary>
/// One character as the server keeps it, read-only: its sheet (class, levels, stats, currencies, position, equipment,
/// bags, skills) and its mailbox. A character in the game is read from its live session on the game loop, any other
/// from the database. Picture fields hold an /img/ address, empty when the server has no picture.
/// </summary>
public partial interface IPanelBackend
{
    /// <summary>A character's sheet; null when there is no such character (or it was deleted).</summary>
    Task<PanelCharacterSheet?> CharacterAsync(long id);

    /// <summary>A page of a character's mailbox, newest first; null when there is no such character.</summary>
    Task<PanelMailPage?> MailAsync(long id, int page, int pageSize);
}

/// <param name="Account">The account that owns it (the panel shows it to admins only).</param>
/// <param name="Live">Read from the character's session in the game now rather than from the database.</param>
/// <param name="StatPoints">Stat points not yet spent (RoleProgress.BonusPoints).</param>
public sealed record PanelCharacterSheet(long Id, string Name, string Account, long ClassId, string Class, string ClassIcon, string Sex,
    int Level, int JobLevel, long Exp, long JobExp, int ParagonLevel, long MapId, string Map, string MapImage, double X, double Y, double Z,
    DateTimeOffset Created, DateTimeOffset? LastLogin, bool Online, bool Live, IReadOnlyList<PanelStat> Stats, int StatPoints,
    IReadOnlyList<PanelCurrency> Currencies, IReadOnlyList<PanelEquipped> Equipment, IReadOnlyList<PanelBag> Bags, IReadOnlyList<PanelSkill> Skills);

public sealed record PanelCurrency(long ItemId, string Name, string Icon, long Amount);

/// <summary>An item a character holds: <paramref name="Instance"/> is the instance (its guid), <paramref name="ItemId"/> its ItemConfig row.</summary>
/// <param name="Bound">Bound to the character (ItemBindInfo).</param>
public sealed record PanelHeldItem(long Instance, long ItemId, string Name, string Icon, int Quality, int Count, bool Locked, bool Bound);

/// <param name="Slot">The equipment slot (EquipGridType).</param>
/// <param name="Strengthen">The slot's strengthen level (EquipLevels; the level belongs to the slot, not the item).</param>
/// <param name="Refine">The slot's refine level.</param>
/// <param name="Enchantments">The item's enchantment rolls (ItemBaseInfo.enchantment), attribute and value.</param>
public sealed record PanelEquipped(int Grid, string Slot, PanelHeldItem Item, int Strengthen, int Refine, IReadOnlyList<PanelStat> Enchantments);

/// <param name="Pack">The bag tab (BackpackConfig id; 100 is the cart).</param>
public sealed record PanelBag(int Pack, string Name, IReadOnlyList<PanelHeldItem> Items);

/// <param name="Id">The skill group (SkillConfig _iSkillID).</param>
public sealed record PanelSkill(long Id, string Name, string Icon, int Level);

/// <param name="Readable">False when the stored mailbox could not be decoded (it is then listed as empty).</param>
public sealed record PanelMailPage(long CharacterId, string Character, IReadOnlyList<PanelLetter> Mails, int Total, int Page, int PageSize, bool Readable);

/// <param name="Expires">When the game removes it; null when it never expires.</param>
/// <param name="Claimed">Its attachments were taken.</param>
public sealed record PanelLetter(string Id, string Title, string Text, string Sender, DateTimeOffset Sent, DateTimeOffset? Expires, bool Read,
    bool Claimed, IReadOnlyList<PanelLetterItem> Attachments);

public sealed record PanelLetterItem(long ItemId, string Name, string Icon, int Quality, long Count);
