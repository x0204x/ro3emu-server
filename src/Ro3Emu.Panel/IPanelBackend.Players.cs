namespace Ro3Emu.Panel;

/// <summary>
/// The public character profiles and guild pages: what other players may see of a character or a guild, read from the
/// server (a character in the game from its session, any other from the database). A profile never carries the
/// character's bags, currencies, position, mail or account. Picture fields hold an /img/ address, empty when the server
/// has no picture (the page then draws a letter).
/// </summary>
public partial interface IPanelBackend
{
    /// <summary>The public profile of the character of this name (any letter case); null when there is none.</summary>
    Task<PanelProfile?> ProfileAsync(string name);

    /// <summary>Every guild, by prestige then name.</summary>
    Task<IReadOnlyList<PanelGuildSummary>> GuildsAsync();

    /// <summary>One guild's public page; null when there is no such guild.</summary>
    Task<PanelGuildPage?> GuildAsync(long id);
}

/// <param name="Frame">The badge frame's picture (GuildIconConfig type 1).</param>
/// <param name="Design">The badge design's picture (GuildIconConfig type 2), drawn over the frame.</param>
public sealed record PanelEmblem(string Frame, string Design);

public sealed record PanelGuildRef(long Id, string Name, int Level, PanelEmblem Emblem);

/// <param name="Level">The title rank's level (TitleConfig _kTitleLev).</param>
public sealed record PanelTitle(long Id, string Name, string Icon, int Level);

/// <param name="Board">The board's key on the rankings page.</param>
/// <param name="Rank">1 is first.</param>
public sealed record PanelRankPosition(string Board, string Title, int Rank, long Score, string ScoreName);

/// <param name="Level">The achievement level (AchievementLevel), with its name.</param>
/// <param name="Types">The level of each achievement category (AchievementType).</param>
/// <param name="Recent">The latest achievements reached, newest first.</param>
public sealed record PanelAchievements(int Level, string LevelName, string Icon, long Points, int Reached, int Total,
    IReadOnlyList<PanelAchievementType> Types, IReadOnlyList<PanelAchievement> Recent);

public sealed record PanelAchievementType(long Type, string Name, string LevelName, int Level, long Points, string Icon);

public sealed record PanelAchievement(long Id, string Name, string Description, long Points, DateTimeOffset? Reached);

/// <param name="Private">The player chose not to show their details in the game (SettingShowDetailInfo NoShow): the
/// profile then holds only the name, class, level and guild.</param>
/// <param name="Position">The character's guild position; empty without a guild.</param>
/// <param name="Equipment">What the character wears, with each slot's strengthen and refine levels; no bags.</param>
public sealed record PanelProfile(string Name, string Class, string ClassIcon, int Level, int JobLevel, bool Online, bool Private,
    PanelGuildRef? Guild, string Position, PanelTitle? Title, IReadOnlyList<PanelEquipped> Equipment, PanelAchievements? Achievements,
    IReadOnlyList<PanelRankPosition> Ranks);

/// <param name="Prestige">The guild's prestige (the guild prestige board's score).</param>
/// <param name="Territories">Territory zones the guild holds now.</param>
/// <param name="League">The guild league tier's name; empty when the guild has no standing.</param>
public sealed record PanelGuildSummary(PanelGuildRef Guild, string Leader, int Members, long Prestige, int Territories, string League);

/// <param name="Position">GuildPositionConfig's name of the member's position.</param>
/// <param name="Merit">The member's Battle Merit this territory season (the Battle Merit board's score; 0 unranked).</param>
public sealed record PanelGuildMember(string Name, string Class, string ClassIcon, int Level, string Position, bool Online, long Merit);

/// <param name="Kind">"Capital", "Region", "Stronghold" or "Field".</param>
public sealed record PanelHeldZone(int Id, string Name, string Kind, long MapId, string Image);

/// <param name="Tier">GuildLeagueRankConfig's name of the tier (1 Ragnarok to 8 Black Iron).</param>
/// <param name="Group">The group inside the tier in the last tournament; 0 before the first.</param>
/// <param name="Place">The place 1 to 8 after the last finals; 0 before.</param>
public sealed record PanelLeagueStanding(int Rank, string Tier, int Group, int Place, long Strength);

/// <param name="Announcement">The guild's declaration (shown to everyone in the game's guild list).</param>
/// <param name="Castles">The siege cities the guild defends (SiegeConfig names).</param>
/// <param name="Merit">The members' Battle Merit added up (inferred: the game has no guild Battle Merit of its own).</param>
public sealed record PanelGuildPage(PanelGuildSummary Summary, string Announcement, DateTimeOffset Created, IReadOnlyList<PanelGuildMember> Members,
    IReadOnlyList<PanelHeldZone> Zones, IReadOnlyList<string> Castles, PanelLeagueStanding? Standing, IReadOnlyList<PanelRankPosition> Ranks, long Merit);
