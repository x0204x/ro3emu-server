using Google.Protobuf;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Server.Micro;

/// <summary>
/// "UserDataService" — the client's global role cache (head names of players, friend lists...). The client asks
/// getAttributeBatchByVersion {"UidFieldVersions": {"&lt;uid&gt;": {"&lt;Type&gt;": version}}} for every player unit,
/// including itself.
/// </summary>
public sealed class UserDataService(SocialOwnerCache cache, OwnerRegistry owners, GameLoop loop, SessionRegistry sessions, GuildService guilds,
    TeamService teams, GuildStore guildStore, GameData data, TimeProvider clock, IOptions<ServerOptions> options,
    ILogger<UserDataService> log) : IMicroService
{
    // (inferred) Full profile replies use increasing versions, so a previous guild/online snapshot is replaceable.
    // The original per-field version allocation is not exported; this service already returns all requested roles.
    private long _version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// The most roles one call may ask for: the client splits its requests at this size
    /// (LC_DAL_RolePublic.iOneTimesGetArrtsCount = 50). Each role costs a reservation and rows on the loop, so without a
    /// limit one 90 KB request held the loop for seconds. Keys past it are not answered, as the client never sends them.
    /// </summary>
    internal const int MaxRolesPerBatch = 50;

    public string Name => "UserDataService";

    public async ValueTask<object?> InvokeAsync(MicroSession session, string method, object? args) =>
        method == "getAttributeBatchByVersion" ? await loop.InvokeAsync(() => GetAttributesAsync(args)) : null;

    private async Task<object?> GetAttributesAsync(object? args)
    {
        var results = new List<object>();
        if (args is IDictionary<object, object?> map && map.TryGetValue("UidFieldVersions", out var v) && v is IDictionary<object, object?> uids)
        {
            object[] asks = [.. uids.Keys.Take(MaxRolesPerBatch)];
            var ids = asks.Select(key => long.TryParse(key.ToString(), out long id) ? id : 0L)
                .Where(id => id > 0).Distinct().ToArray();
            // Guild state has an entity revision separate from the role vector. Until guild publication
            // reaches this projection, a small worker snapshot validates it on every batch.
            while (true)
            {
                var stamps = await cache.GuildStampsAsync(ids);
                using var scope = await owners.EnterAsync("micro/user-data",
                    [.. ids.Select(id => ReservationKey.Role(id)).Concat(stamps.Values.Where(s => s.Id != 0)
                        .Select(s => ReservationKey.Entity("guild", s.Id.ToString()))).Distinct()]);
                var stable = await cache.GuildStampsAsync(ids);
                if (ids.Any(id => stamps[id] != stable[id])) continue;
                var profiles = await cache.LoadProfilesAsync(ids, stable);
                foreach (var key in asks)
                {
                    if (!long.TryParse(key.ToString(), out long uid)) continue;
                    var asked = uids[key] is IDictionary<object, object?> types
                        ? types.Keys.Select(t => t?.ToString() ?? "").ToHashSet() : [];
                    if (profiles.TryGetValue(uid, out var profile) && profile is not null) results.Add(Entry(profile, asked));
                    else if (GuildEntry(uid) is { } guild) results.Add(guild);
                }
                break;
            }
        }
        log.LogDebug("getAttributeBatchByVersion -> {Count} role(s)", results.Count);
        return new Dictionary<string, object?> { ["Ret"] = MicroServiceRouter.Ret(true), ["Results"] = results.ToArray() };
    }

    /// <summary>
    /// A message whose varint fields 1..n are all present, zeros too (PopularityStarData, PersonalUpvoteCount): the client
    /// decodes an absent field as nil, and the gift panel shows popularity only when updateTime is not nil.
    /// </summary>
    private static byte[] Explicit(params long[] values)
    {
        using var stream = new MemoryStream();
        var output = new CodedOutputStream(stream);
        for (int i = 0; i < values.Length; i++)
        {
            output.WriteTag(i + 1, WireFormat.WireType.Varint);
            output.WriteInt64(values[i]);
        }
        output.Flush();
        return stream.ToArray();
    }

    /// <summary>
    /// Guild rows of the leaderboards bind GuildSnap by guild id, not by role (UI_Sys_RankPower_ListCellWidgetCtrl
    /// _bindGuildId 1610-1641 / _onGuildLeaderChanged 2895-2921), through the same global cache request. Guild ids
    /// (guild table, from 1) never meet role ids (role sequence starts at 100000), so an unknown role uid that names a
    /// formal guild is answered with that guild's GuildSnap only; LC_DAL_RolePublic decodes Base only when present
    /// (:627-640).
    /// </summary>
    private Dictionary<string, object?>? GuildEntry(long uid)
    {
        if (uid <= 0 || guildStore.Get(uid) is not { Status: (int)GuildStatus.Normal } guild) return null;
        long snapshotVersion = Interlocked.Increment(ref _version);
        return new()
        {
            ["Uid"] = uid,
            ["FieldsVersion"] = new Dictionary<string, object?> { ["GuildSnap"] = snapshotVersion },
            ["Data"] = new Dictionary<string, object?> { ["GuildSnap"] = guilds.Snapshot(guild).ToByteArray() },
        };
    }

    /// <summary>
    /// "Base" (romsg.RoleAdditionBaseInfo bytes) must always be present; Name/Level/Online/ClusterId are plain values.
    /// The guild roster reads PersonalSystem.Base, not GuildMemberInfo's name/level/online fields
    /// (UI_Sys_Guild_Member_Info_ListWidgetCtrl.lua:160-196,309). OfflineTime is milliseconds, -1 when online.
    /// Membership comes from the exclusive guild index; a stale personal 5047 blob cannot restore a departed guild.
    /// GuildSnap bytes also reset after leaving (LC_DAL_RolePublic.lua:960-980).
    /// </summary>
    private Dictionary<string, object?> Entry(Accounts.Persistence.ProfileSnapshot profile, IReadOnlySet<string> asked)
    {
        var role = profile.Role;
        var session = sessions.All().FirstOrDefault(s => s.Role?.Id == role.Id);
        var live = session?.Progress;
        var guild = profile.Guild;
        var member = guild?.Members.GetValueOrDefault(role.Id);
        var personal = profile.PersonalGuild is { } blob ? DBRoleGuildInfo.Parser.ParseFrom(blob) : null;
        // (inferred) After an unclean stop, use last login if no positive guild logout timestamp was persisted.
        long offline = live != null ? -1L : (member?.OfflineTime is > 0 ? member.OfflineTime : role.LastLoginTime) * 1000;
        var additional = new RoleAdditionBaseInfo
        {
            Career = live?.Career ?? role.Career, Gender = role.Sex, JobLevel = live?.JobLevel ?? 1, BornArea = role.Area,
            GuildID = guild?.Id ?? 0, GuildOfficial = member?.Official ?? 0,
            CantBeInvited = personal?.CantBeInvited ?? false, Offlinetime = offline,
        };
        additional.Appearances.Add(session?.Wardrobe?.CurUseAppearances.ToDictionary(p => p.Key, p => p.Value)
            ?? RoleProto.Appearances(role, profile.Wardrobe));
        // The card's "Fashion" (UI_Sys_Personal_InfoWidgetCtrl._refreshPlayerBaseInfoShow reads Base.fashionScore).
        additional.FashionScore = (int)Math.Min(int.MaxValue, WardrobeService.FashionScore(data, session?.Wardrobe
            ?? (profile.Wardrobe is { Length: > 0 } wardrobe ? DBWardRobeInfo.Parser.ParseFrom(wardrobe) : null)));
        // The profile card's current preset: Base carries its avatar, frame and title (inferred mirror of PersonalInfo).
        var card = PersonalService.Current(session?.Personal
            ?? (profile.Personal is { Length: > 0 } stored ? DBPersonalInfo.Parser.ParseFrom(stored) : null));
        if (card != null)
        {
            additional.AvatarId = card.AvatarId; additional.AvatarFrameId = card.AvatarFrameId;
            additional.PersonalTitleId = card.PersonalTitleId;
        }
        // The worn chat cosmetics (slot type to outfit): other players' chat bubble, honor, overhead title and profile card
        // index them unguarded (UI_Sys_Chat_BgWidgetCtrl, UI_Sys_Personal_InfoWidgetCtrl), so the defaults are sent too.
        additional.SocialOutfitData = SocialOutfitService.Worn(data, role.Sex, live?.Career ?? role.Career, session?.SocialOutfit
            ?? (profile.SocialOutfit is { Length: > 0 } outfits ? DBSocialOutfit.Parser.ParseFrom(outfits) : null),
            clock.GetUtcNow().ToUnixTimeSeconds());
        var fields = new Dictionary<string, object?>
        {
            ["Base"] = additional.ToByteArray(),
            ["Name"] = role.Name,
            ["Level"] = live?.Level ?? role.Level,
            ["Online"] = live != null,
            ["OfflineTime"] = offline,
            ["ClusterId"] = options.Value.ClusterId,
            ["GuildSnap"] = (guild == null ? new GuildSnapInfo() : guilds.Snapshot(guild)).ToByteArray(),
        };
        if (asked.Contains("Team")) fields["Team"] = teams.CacheTeam(role.Id).ToByteArray();
        // "Personal" (romsg.PersonalInfo bytes) only when asked for (UI_Sys_PersonalPanelModel.BindRoleGuid) and parseable.
        if (asked.Contains("Personal") && card != null && PersonalService.Parseable(card)) fields["Personal"] = card.ToByteArray();
        // The profile card asks for PopularityStar with resend-until-present (UI_Sys_Personal_InfoWidgetCtrl.GetPopularity:
        // BindPersonalDataChangeByUIDAndTypeDoOnce), and every reply bumps the versions here, so without it the card
        // refreshes and asks again about five times a second. Zeros are written too (_refreshPopularity, the like counter).
        if (asked.Contains("PopularityStar"))
        {
            var star = PersonalService.Popularity(profile.Popularity, PersonalService.WeekStartMs(clock));
            fields["PopularityStar"] = Explicit(star.Affection, star.Popularity, star.PopularityWeekly, star.UpdateTime, 0);
        }
        // Likes received: the count (the card's heart number) and who liked when (Like History, and whether the reader's
        // own heart is lit). The list's parsers walk it with pairs() unguarded, so an empty list is left out.
        var upvotes = PersonalService.Upvotes(profile.PersonalUpvotes);
        if (asked.Contains("PersonalUpvoteCount")) fields["PersonalUpvoteCount"] = Explicit(upvotes?.Infos.Count ?? 0);
        if (asked.Contains("PersonalUpvoteList") && upvotes != null) fields["PersonalUpvoteList"] = upvotes.ToByteArray();
        long snapshotVersion = Interlocked.Increment(ref _version);
        return new()
        {
            ["Uid"] = role.Id,
            ["FieldsVersion"] = fields.Keys.ToDictionary(k => k, _ => (object?)snapshotVersion),
            ["Data"] = fields,
        };
    }
}
