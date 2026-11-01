using System.Globalization;
using System.Text.Json;
using Ro3Emu.Panel;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Micro;
using Romsg;

namespace Ro3Emu.Server.Http;

/// <summary>
/// The public character profiles and guild pages. Everything is read from the database on the read pool (what the
/// server last saved) and from the online list, never from a live session, so a busy public page costs the game loop
/// nothing. A profile shows what the game lets another player see of a character (its look, class, level, guild,
/// equipment, title and achievements: the "view details" window, CharacterService.DetailInfo) and nothing of its bags,
/// purse, position or mail.
/// </summary>
public sealed partial class PanelBackend
{
    /// <summary>The role blobs a profile reads.</summary>
    private static readonly string[] ProfileBlobs = ["equip_levels", "settings", "title", "achievements"];
    /// <summary>GuildPositionConfig 1, the guild's leader (GuildService: Official == 1).</summary>
    private const int GuildLeader = 1;
    private const int RecentAchievements = 8, MeritBoardRows = 3000;

    public async Task<PanelProfile?> ProfileAsync(string name)
    {
        if ((await accounts.ReadAsync<List<Role>>(new RoleProjectionRead(RoleProjectionKind.ByName, name))).FirstOrDefault() is not { } role) return null;
        var blobs = await accounts.ReadAsync<Dictionary<string, byte[]?>>(new PanelRoleBlobsRead(role.Id, ProfileBlobs));
        var progress = await accounts.ReadAsync<RoleProgress>(new RoleProgressReadPlan(role.Id));
        var online = (await OnlineRowsAsync()).FirstOrDefault(r => r.RoleId == role.Id);
        int career = online?.Career ?? progress.Career, level = Math.Max(online?.Level ?? 0, progress.Level);
        var guild = await accounts.ReadAsync<GuildStore.State?>(new GuildReadPlan(GuildReadKind.ForRole, role.Id));
        string position = guild?.Members.GetValueOrDefault(role.Id) is { } member ? PositionName(member.Official) : "";
        var guildRef = guild != null ? GuildRef(guild) : null;
        if (Hidden(blobs.GetValueOrDefault("settings")))
            return new PanelProfile(role.Name, ClassName(career), ClassIcon(career), level, progress.JobLevel, online != null, true, guildRef, position,
                null, [], null, []);
        var items = await accounts.ReadAsync<List<InvItem>>(new ItemReadPlan(role.Id));
        var levels = blobs.GetValueOrDefault("equip_levels") is { Length: > 0 } saved ? JsonSerializer.Deserialize<EquipLevels>(saved) ?? new() : new();
        var worn = Equipment(new CharacterState(role, progress, [.. items.Where(i => i.Grid > 0)], levels, 0, 0, 0, 0, Live: false));
        return new PanelProfile(role.Name, ClassName(career), ClassIcon(career), level, progress.JobLevel, online != null, false, guildRef, position,
            Title(blobs.GetValueOrDefault("title")), worn, Achievements(blobs.GetValueOrDefault("achievements")), await RoleRanksAsync(role));
    }

    /// <summary>
    /// The game's one privacy choice, SETTING "show detailed info" (MG_Setting SettingShowDetailInfo, LC_DAL_Setting
    /// _processShowDetailInfo): NoShow keeps the details window shut. (inferred) A character that never saved the
    /// setting shows its details: the client offers it as a choice to hide them.
    /// </summary>
    private static bool Hidden(byte[]? settings)
    {
        if (settings is not { Length: > 0 }) return false;
        try { return DBSettingInfo.Parser.ParseFrom(settings).ShowDetailInfo is { ShowDetailInfo: ShowDetailInfoType.NoShow }; }
        catch (Google.Protobuf.InvalidProtocolBufferException) { return true; } // unreadable: the safe side
    }

    /// <summary>The title rank (DBTitleInfo.titleId, a TitleConfig _iID); null below the first rank.</summary>
    private PanelTitle? Title(byte[]? blob)
    {
        if (blob is not { Length: > 0 }) return null;
        long id = DBTitleInfo.Parser.ParseFrom(blob).TitleId;
        if (id <= 0 || TitleConfig.Get(data, id) is not { } row) return null;
        return new PanelTitle(id, data.TextOf(row.TitleName), Image("titles", row.SmallIcon), (int)row.TitleLev);
    }

    /// <summary>DBRoleAchievementInfo: the achievement level (type 0, AchievementLevel), each category's level
    /// (types 100-105, AchievementType) and the achievements reached (status Complete or GetReward).</summary>
    private PanelAchievements? Achievements(byte[]? blob)
    {
        if (blob is not { Length: > 0 }) return null;
        var info = DBRoleAchievementInfo.Parser.ParseFrom(blob);
        var global = info.AchievementLevelInfo.GetValueOrDefault(0);
        var level = AchievementLevel.All(data).FirstOrDefault(l => l.Level == (global?.Level ?? 0));
        var types = info.AchievementLevelInfo.Where(t => t.Key > 0).OrderBy(t => t.Key).Select(t => AchievementTypeOf(t.Key, t.Value)).ToList();
        var reached = info.Infos.Values.Where(a => a.Status != AchievementStatusType.None).ToList();
        var recent = reached.OrderByDescending(a => a.Stamptime).Take(RecentAchievements).Select(a => AchievementConfig.Get(data, a.AchieveId) is { } row
                ? new PanelAchievement(a.AchieveId, data.TextOf(row.Name), data.TextOf(row.Desc), row.Point, Stamp(a.Stamptime))
                : new PanelAchievement(a.AchieveId, "Achievement " + a.AchieveId.ToString(CultureInfo.InvariantCulture), "", 0, Stamp(a.Stamptime)))
            .ToList();
        return new PanelAchievements(global?.Level ?? 0, level.Row.ValueKind == JsonValueKind.Undefined ? "" : data.TextOf(level.Name),
            level.Row.ValueKind == JsonValueKind.Undefined ? "" : Image("achievements", level.Icon), global?.Point ?? 0, reached.Count,
            AchievementConfig.All(data).Count(), types, recent);
    }

    private static DateTimeOffset? Stamp(long ms) => ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;

    /// <summary>A category's name and icon from its level-0 AchievementType row, its level's name from the level's row.</summary>
    private PanelAchievementType AchievementTypeOf(long type, AchievementLevelInfo level)
    {
        var rows = AchievementType.All(data).Where(r => r.TypeId == type).OrderBy(r => r.Level).ToList();
        var first = rows.FirstOrDefault();
        var at = rows.LastOrDefault(r => r.Level <= level.Level);
        bool known = first.Row.ValueKind != JsonValueKind.Undefined;
        string name = known ? data.TextOf(first.Name) : "";
        if (name.Length == 0) name = "Category " + type.ToString(CultureInfo.InvariantCulture);
        return new PanelAchievementType(type, name, at.Row.ValueKind != JsonValueKind.Undefined ? LevelName(name, data.TextOf(at.LevelName)) : "",
            level.Level, level.Point, known ? Image("achievements", first.Icon) : "");
    }

    /// <summary>
    /// A category level's name for the page. The English texts are the category's name with the rank glued on
    /// ("Growth@{1}" with 1: "Growth1"), which reads as a raw key under the category's own name: "Level 1" instead.
    /// Any other text is kept.
    /// </summary>
    internal static string LevelName(string category, string text) =>
        text.Length > category.Length && text.StartsWith(category, StringComparison.Ordinal) && text[category.Length..].Trim().All(char.IsAsciiDigit)
            ? "Level " + text[category.Length..].Trim() : text;

    /// <summary>The character's place on each character board it is on (the rankings page's boards).</summary>
    private async Task<List<PanelRankPosition>> RoleRanksAsync(Role role)
    {
        var positions = new List<PanelRankPosition>();
        foreach (var (info, board) in BoardList.Where(b => !b.Info.Guild))
        {
            if (board == 0)
            {
                var rows = (await LevelRowsAsync(0)).ToList();
                int at = rows.FindIndex(r => string.Equals(r.Name, role.Name, StringComparison.Ordinal));
                if (at >= 0 && at < BoardSize) positions.Add(new PanelRankPosition(info.Id, info.Title, at + 1, rows[at].Score, info.Score));
                continue;
            }
            int rank = await ranks.RankOfAsync(board, role.Id);
            if (rank is <= 0 or > BoardSize) continue;
            var top = await ranks.TopAsync(board, rank);
            positions.Add(new PanelRankPosition(info.Id, info.Title, rank, top.Count >= rank ? top[rank - 1].Score : 0, info.Score));
        }
        return positions;
    }

    // ---------------------------------------------------------------- guilds

    public async Task<IReadOnlyList<PanelGuildSummary>> GuildsAsync()
    {
        var guilds = await accounts.ReadAsync<List<GuildStore.State>>(new GuildReadPlan(GuildReadKind.List));
        var (map, league) = (await TerritoryAsync(), await LeagueAsync());
        return [.. guilds.Select(g => GuildSummary(g, map, league)).OrderByDescending(g => g.Prestige).ThenBy(g => g.Guild.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<PanelGuildPage?> GuildAsync(long id)
    {
        if (await accounts.ReadAsync<GuildStore.State?>(new GuildReadPlan(GuildReadKind.Guild, id)) is not { } guild) return null;
        var (map, league) = (await TerritoryAsync(), await LeagueAsync());
        var online = (await OnlineRowsAsync()).Select(r => r.RoleId).ToHashSet();
        var current = await accounts.ReadAsync<Dictionary<long, (string Name, int Career, int Level)>>(new PanelRoleNamesRead([.. guild.Members.Keys]));
        var merit = (await ranks.TopAsync(RankService.BattleGlory, MeritBoardRows)).ToDictionary(r => r.Id, r => r.Score);
        var members = guild.Members.Values
            .Select(m => (Member: m, Now: current.TryGetValue(m.RoleId, out var c) ? c : (m.Name, m.Career, m.Level)))
            .OrderBy(m => PositionSort(m.Member.Official)).ThenByDescending(m => m.Now.Level).ThenBy(m => m.Now.Name, StringComparer.OrdinalIgnoreCase)
            .Select(m => new PanelGuildMember(m.Now.Name, ClassName(m.Now.Career), ClassIcon(m.Now.Career), m.Now.Level, PositionName(m.Member.Official),
                online.Contains(m.Member.RoleId), merit.GetValueOrDefault(m.Member.RoleId)))
            .ToList();
        var zones = map.Zones.Where(z => z.Owner?.Id == id).Select(z => new PanelHeldZone(z.Id, z.Name, z.Kind, z.MapId, z.Image)).ToList();
        var castles = map.Castles.Where(c => c.Defender?.Id == id).Select(c => c.Name).ToList();
        var standing = league?.Guilds.GetValueOrDefault(id) is { } s ? new PanelLeagueStanding(s.Tier, TierName(s.Tier), s.Group, s.Place, s.Strength) : null;
        return new PanelGuildPage(GuildSummary(guild, map, league), guild.Announce, DateTimeOffset.FromUnixTimeMilliseconds(guild.CreateTime), members, zones, castles,
            standing, await GuildRanksAsync(id), members.Sum(m => m.Merit));
    }

    private PanelGuildSummary GuildSummary(GuildStore.State g, PanelTerritoryMap map, GuildLeagueService.LeagueState? league) =>
        new(GuildRef(g), g.Members.Values.FirstOrDefault(m => m.Official == GuildLeader)?.Name ?? "", g.Members.Count, g.Reputation,
            map.Zones.Count(z => z.Owner?.Id == g.Id), league?.Guilds.GetValueOrDefault(g.Id) is { } s ? TierName(s.Tier) : "");

    private async Task<List<PanelRankPosition>> GuildRanksAsync(long id)
    {
        var positions = new List<PanelRankPosition>();
        foreach (var (info, board) in BoardList.Where(b => b.Info.Guild))
        {
            int rank = await ranks.RankOfAsync(board, id);
            if (rank is <= 0 or > BoardSize) continue;
            var top = await ranks.TopAsync(board, rank);
            positions.Add(new PanelRankPosition(info.Id, info.Title, rank, top.Count >= rank ? top[rank - 1].Score : 0, info.Score));
        }
        return positions;
    }

    /// <summary>The guild league's standings (GuildLeagueService's durable state); null before the first season.</summary>
    private async Task<GuildLeagueService.LeagueState?> LeagueAsync()
    {
        var blob = await accounts.ReadAsync<byte[]?>(new TerritoryRealmReadPlan(RealmStateKind.GuildLeague));
        try { return blob is { Length: > 0 } ? JsonSerializer.Deserialize<GuildLeagueService.LeagueState>(blob) : null; }
        catch (JsonException) { return null; }
    }

    private string TierName(int tier) =>
        GuildLeagueRankConfig.All(data).FirstOrDefault(r => r.Rank == tier) is { Row.ValueKind: JsonValueKind.Object } row && data.TextOf(row.Name) is { Length: > 0 } name
            ? name : "Tier " + tier.ToString(CultureInfo.InvariantCulture);

    private PanelGuildRef GuildRef(GuildStore.State g) => new(g.Id, g.Name, g.Level, Emblem(g.Background, g.Icon));

    /// <summary>A guild's badge: the frame (State.Background) and the design (State.Icon), GuildIconConfig _kBigIcon.</summary>
    private PanelEmblem Emblem(long frame, long design) => new(GuildIcon(frame), GuildIcon(design));

    private string GuildIcon(long id) => id > 0 && GuildIconConfig.Get(data, id) is { } row ? Image("guilds", row.BigIcon) : "";

    private string PositionName(int official) =>
        GuildPositionConfig.Get(data, official) is { } row && data.TextOf(row.Name) is { Length: > 0 } name ? name
            : official == GuildLeader ? "Leader" : "Member";

    private long PositionSort(int official) => GuildPositionConfig.Get(data, official)?.ShowSort ?? long.MaxValue;
}
