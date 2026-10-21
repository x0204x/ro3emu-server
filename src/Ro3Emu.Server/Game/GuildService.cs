using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Guild membership and roster. ServerOptions.GuildFounding "responders" founds every guild
/// through responders as the original (PreCreate, GuildService.Responders); "instant" (default) keeps the shortcut:
/// a guild created without the create widget's autoCreateHelper toggle is Normal at once.
/// Guild funds, contribution and buildings are persisted and shown; no unproven contribution-earning formula is used.
/// (inferred) Default building rows, initial rank, hall level mapping, rank order, name character whitelist,
/// invitation token lifecycle and post-mutation push order are recorded in docs/systems/guild.md.
/// </summary>
public sealed partial class GuildService(GameData data, GuildStore store, RoleStore roles, SessionRegistry sessions,
    CharacterService character, FunctionService functions, ChatService chat, IOptions<ServerOptions> options,
    TimeProvider clock, Ownership.OwnerRegistry ownerRegistry, AwardService awards, GameLoop loop, SceneService scene,
    TeamService teams, MailService mail, ILogger<GuildService> log)
{
    public const uint GuildSync = 5047, Create = 32210, Recommend = 32211, HelperList = 32212, Full = 32213, MemberChange = 32214;
    public const uint CheckSensitive = 10003;

    /// <summary>An online member's committed guild view changed guild or position (create, join, leave, appoint;
    /// achievements 104007/107003). Runs inside the commit's apply: listeners post their work.</summary>
    public List<Action<GameSession>> MembershipChanged { get; } = new();
    /// <summary>Rank rights were saved for this guild. Listeners post work without changing membership.</summary>
    public List<Action<long>> RightsChanged { get; } = new();
    public const uint Apply = 32223, ApplyOpe = 32224, ApplyChange = 32225, Edit = 32230, BaseChange = 32231;
    public const uint ChangeOfficial = 32232, Kick = 32233, Leave = 32234, Invite = 32237, InviteNtf = 32238, InviteAck = 32239;
    public const uint Detail = 32290, BuildUpgrade = 32242, MarketBooth = 83900, MarketItems = 83903, MarketList = 83901, MarketUnlist = 83902;
    public const uint MemberIds = 32269, SceneGuildId = 32322, SyncOnce = 32323;
    public const uint MarketBuy = 83904, MarketSellRecords = 83905, MarketBuyRecords = 83906, MarketUpdate = 83908;

    /// <summary>Guild-owned goods supplied by the territory escort ledger for the 32213 full snapshot.</summary>
    public Func<long, GuildItemData>? ItemDataOf { get; set; }

    private readonly Dictionary<long, Invitation> _invites = new();
    private long _nextToken = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private sealed record Invitation(long GuildId, long Recipient, long Inviter, long ExpiresMs);
    // The injected clock, as WeekStart: membership times compared with the guild week must share its calendar.
    private long NowMs() => clock.GetUtcNow().ToUnixTimeMilliseconds();
    private long NowSec() => clock.GetUtcNow().ToUnixTimeSeconds();
    private GameSession? Online(long id) => sessions.All().FirstOrDefault(s => s.Role?.Id == id);
    private GuildStore.State? Own(GameSession session) => session.Role == null ? null : store.ForRole(session.Role.Id);

    private async Task LoginCoreAsync(GameSession session)
    {
        HookDeposits();
        HookItemActions();
        // Authoritative membership index wins over any stale role blob after reconnect/role switch.
        var guild = Own(session);
        var personal = Personal(session.Role!.Id);
        await SavePersonalAsync(session, guild == null ? WithoutGuild(personal) : RoleInfo(guild, guild.Members[session.Role.Id], personal));
        await SyncRole(session);
        if (guild != null)
        {
            await SendFull(session, guild, false);
            await PushDividendAsync(session, guild); // the full info has no dividend field (32231 tp=14)
            if (guild.Status == (int)GuildStatus.PreCreate && IsFounder(guild, session.Role.Id)) await PushRedDotAsync(guild);
        }
    }

    private async Task OnlineChangedCoreAsync(GameSession session, bool online)
    {
        // A replaced connection must not overwrite the currently active session's roster state.
        if (!online && session.Role != null && Online(session.Role.Id) is { } current && current != session) return;
        var guild = Own(session);
        if (guild == null || !guild.Members.TryGetValue(session.Role!.Id, out var member)) return;
        member.Level = session.Progress?.Level ?? member.Level;
        member.Career = session.Progress?.Career ?? member.Career;
        member.OfflineTime = online ? -1 : NowSec();
        await SaveGuildAsync(guild);
        await PushMember(guild, member, GuildMemberInfoChangeNtf.Types.ChangeType.Update, online ? 0 : member.RoleId);
    }

    private DBRoleGuildInfo Personal(long roleId) => roles.LoadGuildInfo(roleId) is { } bytes
        ? DBRoleGuildInfo.Parser.ParseFrom(bytes) : new DBRoleGuildInfo();

    private static DBRoleGuildInfo WithoutGuild(DBRoleGuildInfo prior) => new()
    {
        LastGuildId = prior.LastGuildId, HistoryContribute = prior.HistoryContribute,
        CantBeInvited = prior.CantBeInvited, PunishTime = prior.PunishTime,
        MultiApplicationTime = prior.MultiApplicationTime,
    };

    private Task SyncRole(GameSession session)
    {
        var info = session.Guild ?? new DBRoleGuildInfo();
        // Last week's chests expired with the week (UITips 17019); the client shows any key as received.
        if (info.ActiveRewardReceived.Any(r => r.Value < WeekStart))
        {
            info = info.Clone();
            foreach (long key in info.ActiveRewardReceived.Where(r => r.Value < WeekStart).Select(r => r.Key).ToArray())
                info.ActiveRewardReceived.Remove(key);
            session.Guild = info;
        }
        return CharacterService.SyncAsync(session, GuildSync, info);
    }

    private static DBRoleGuildInfo RoleInfo(GuildStore.State guild, GuildStore.Member member, DBRoleGuildInfo? prior = null)
    {
        var leader = guild.Members.Values.Single(m => m.Official == 1);
        return new DBRoleGuildInfo
        {
            GuildId = guild.Id, GuildName = guild.Name, Official = member.Official, Status = guild.Status,
            GuildIcon = Icon(guild), GuildLeaderId = leader.RoleId, GuildLeaderName = leader.Name,
            GuildLeaderJob = leader.Career, HistoryContribute = Math.Max(member.HistoryContribute, prior?.HistoryContribute ?? 0),
            Right = Rights(guild),
            LastGuildId = prior?.LastGuildId ?? 0, CantBeInvited = prior?.CantBeInvited ?? false,
            PunishTime = prior?.PunishTime ?? 0, MultiApplicationTime = prior?.MultiApplicationTime ?? 0,
            CatShopInfo = prior?.GuildId == guild.Id ? prior.CatShopInfo?.Clone() ?? new DBGuildCatShopInfo()
                : new DBGuildCatShopInfo(),
            IsCatShopAllSubmit = prior?.GuildId == guild.Id && prior.IsCatShopAllSubmit,
            ActiveRewardReceived = { prior?.GuildId == guild.Id ? prior.ActiveRewardReceived : new Google.Protobuf.Collections.MapField<long, long>() },
        };
    }

    internal static GuildHeadIcon Icon(GuildStore.State g) => new() { BackgroundType = g.Background, IconType = g.Icon };
    private static GuildResource Resource(GuildStore.State g) => new()
    {
        Money = g.Money, Contribute = g.Contribute, Materials = g.Materials,
        Reputation = g.Reputation, AuctionMaterials = g.AuctionMaterials,
    };

    private static GuildOfficalRight Rights(GuildStore.State g)
    {
        var result = new GuildOfficalRight();
        foreach (var (right, officials) in g.Rights.OrderBy(r => r.Key))
        {
            var row = new OfficalRight { RightType = right };
            row.AbledOfficial.AddRange(officials.Select(i => (long)i));
            result.Data.Add(row);
        }
        return result;
    }

    private Dictionary<int, List<int>> DefaultRights()
    {
        var result = new Dictionary<int, List<int>>();
        foreach (var row in data.Table("GuildPermissionConfig").Values)
        {
            int id = (int)GameData.Int(row, "_iID");
            result[id] = GameData.Pairs(row, "_iDetail").Where(p => p.Value >= 2).Select(p => (int)p.Id).ToList();
        }
        return result;
    }

    internal bool HasRight(GuildStore.State g, long actor, int right) => g.Members.TryGetValue(actor, out var m)
        && (m.Official == 1 || g.Rights.GetValueOrDefault(right)?.Contains(m.Official) == true);

    /// <summary>The committed guild's GuildPermissionConfig right for one member (e.g. 26 GuildHuntCommander).</summary>
    internal bool HasRight(long guildId, long actor, int right) => store.Get(guildId) is { } g && HasRight(g, actor, right);

    /// <summary>
    /// GuildHuntingService: true while the guild races in an unsettled Hunting Challenge. Leaving and kicking then answer
    /// 5840 "This action cannot be performed during the Hunting Challenge." (ErrorCodeConfig 5840; which requests answer
    /// it is inferred: the roster decides who is rewarded).
    /// </summary>
    internal Func<long, bool>? HuntingBusy { get; set; }

    internal long MaxMembers(GuildStore.State g)
    {
        var hall = data.Table("GuildBuildingConfig").Values.FirstOrDefault(r =>
            GameData.Int(r, "_iType") == 1 && GameData.Int(r, "_iLv") == g.Buildings.GetValueOrDefault(1, 1));
        return hall.ValueKind == JsonValueKind.Object ? GameData.Int(hall, "_iPeople") : 80;
    }

    private GuildMemberInfo MemberInfo(GuildStore.Member m, long excludeOnlineRole = 0) => new()
    {
        PlayerID = m.RoleId, Name = m.Name, Level = m.Level, Career = m.Career,
        Official = m.Official, EnterTime = m.EnterTime,
        OfflineTime = m.RoleId != excludeOnlineRole && Online(m.RoleId) != null ? -1 : m.OfflineTime,
        CurContribute = m.CurContribute, HistoryContribute = m.HistoryContribute,
        LastWeekActivity = store.ForRole(m.RoleId) is { } g ? (int)Math.Min(int.MaxValue, LastWeekActivity(g, m)) : 0,
    };

    internal GuildSnapInfo Snapshot(GuildStore.State g, long viewer = 0)
    {
        var leader = g.Members.Values.Single(m => m.Official == 1);
        var snap = new GuildSnapInfo
        {
            Guid = g.Id, ShortUid = g.Id, Name = g.Name, Level = g.Level, Announce = g.Announce,
            MemberCount = g.Members.Count, TotalMemberCount = MaxMembers(g), Icon = Icon(g),
            LeaderId = leader.RoleId, LeaderName = leader.Name, AutoAcceptApply = g.AutoAccept,
            IsApplyed = g.Applications.ContainsKey(viewer) || g.HelperApplies.ContainsKey(viewer), JoinLevelLimit = g.JoinLevelLimit, Reputation = g.Reputation,
        };
        // The client blindly copies historyNames and iterates guildManagerData; include actual current name/leader.
        snap.HistoryNames.AddRange(g.HistoryNames.Count > 0 ? g.HistoryNames : [g.Name]);
        snap.GuildManagerData.AddRange(g.Members.Values.Where(m => m.Official is not (20 or 21))
            .Select(m => new GuildManagerInfo { PlayerID = m.RoleId, RightType = m.Official }));
        return snap;
    }

    private GuildFullInfoNtf FullInfo(GuildStore.State g, bool isJoin)
    {
        var full = new GuildFullInfoNtf
        {
            Guid = g.Id, ShortUid = g.Id, Name = g.Name, Level = g.Level, Announce = g.Announce,
            Notice = g.Notice, CreateTime = g.CreateTime, Status = (GuildStatus)g.Status,
            MaxMemberCount = MaxMembers(g), IconInfo = Icon(g), OfficalRight = Rights(g), Resource = Resource(g),
            Items = ItemDataOf?.Invoke(g.Id) ?? new GuildItemData(),
            AutoAcceptApply = g.AutoAccept, AutoCreateHelper = g.AutoCreateHelper,
            CreateHelperEndTime = g.HelperDeadlineMs,
            MaxCreateHelperNum = Setting("GuildResponsePeopleLimit", 3)[0],
            JoinLevelLimit = g.JoinLevelLimit, LastChangeNameTime = g.LastChangeNameTime,
            LastChangeNoticeTime = (int)g.LastChangeNoticeTime, IsJoin = isJoin, ActivityInfo = ActivityInfo(g),
        };
        full.HistoryNames.AddRange(g.HistoryNames.Count > 0 ? g.HistoryNames : [g.Name]);
        full.MemberInfo.AddRange(g.Members.Values.Select(m => MemberInfo(m)));
        if (g.Status == (int)GuildStatus.PreCreate)
            // Responders in join order, the founder last (GuildService.Responders).
            full.CreateHelperInfo.AddRange(g.Members.Values.OrderBy(m => m.Official == 1).ThenBy(m => m.EnterTime)
                .Select(m => HelperInfo(m.RoleId, m.Name, m.Level, m.Career, m.EnterTime)));
        if (g.Status == (int)GuildStatus.PreCreate)
            full.CreateHelperApplyData.AddRange(LiveResponses(g).OrderBy(a => a.ApplyTime)
                .Select(a => HelperInfo(a.RoleId, a.Name, a.Level, a.Career, a.ApplyTime)));
        full.ApplyInfo.AddRange(g.Applications.Values.Select(ApplyInfo));
        full.Building.AddRange(g.Buildings.Select(k => BuildingInfo(g, k.Key)));
        full.UnLockHeadIconData.AddRange(UnlockedIcons(g));
        full.ShopCommodityData.AddRange(StoreCounts(g));
        return full;
    }

    private static GuildBuilding BuildingInfo(GuildStore.State g, long type) => new()
    {
        BuildingID = type, Level = g.Buildings[type], EndLevelUpTime = g.BuildingEndTimes.GetValueOrDefault(type),
    };

    private static GuildApplyEnterInfo ApplyInfo(GuildStore.Application a) => new()
    {
        PlayerID = a.RoleId, Name = a.Name, Level = a.Level, Career = a.Career, ApplyTime = a.ApplyTime,
    };

    private Task SendFull(GameSession s, GuildStore.State g, bool joined)
    {
        var full = FullInfo(g, joined);
        full.IsLeagueElite = s.Role != null && IsLeagueElite(g, s.Role.Id);
        return s.SendGameAsync(Full, full);
    }

    /// <summary>Publish committed caravan funds/contribution from a freshly loaded guild aggregate.</summary>
    public async Task RefreshCaravanResourcesAsync(long guildId)
    {
        if (store.Get(guildId) is not { } guild) return;
        var transaction = roles.NewTransaction();
        EnlistViews(guild, transaction, deferUnavailable: true);
        await roles.CommitAsync(transaction, "guild/caravan-projections");
        foreach (var member in guild.Members.Values)
            if (Online(member.RoleId) is { } online && online.Attachment?.Cancelled != true
                && online.Owner?.State != Ownership.RoleOwnerState.Quiescing)
            {
                await SyncRole(online);
                await SendFull(online, guild, false);
            }
    }

    private async Task PushMember(GuildStore.State g, GuildStore.Member member, GuildMemberInfoChangeNtf.Types.ChangeType kind,
        long excludeOnlineRole = 0)
    {
        var ntf = new GuildMemberInfoChangeNtf { Status = kind };
        ntf.Info.Add(MemberInfo(member, excludeOnlineRole));
        foreach (var m in g.Members.Values)
            if (Online(m.RoleId) is { } s) await s.SendGameAsync(MemberChange, ntf);
    }

    private async Task PushApplications(GuildStore.State g, GuildStore.Application a, GuildEnterApplyChangeNtf.Types.ChangeType kind)
    {
        var ntf = new GuildEnterApplyChangeNtf();
        ntf.Change.Add(new GuildEnterApplyChangeNtf.Types.ApplyChangeInfo { ChangeType = kind, Info = ApplyInfo(a) });
        foreach (var m in g.Members.Values)
            if (HasRight(g, m.RoleId, 2) && Online(m.RoleId) is { } s) await s.SendGameAsync(ApplyChange, ntf);
    }

    private async Task RefreshMembers(GuildStore.State g)
    {
        foreach (var m in g.Members.Values)
            if (Online(m.RoleId) is { } s)
            {
                await SyncRole(s);
                await SendFull(s, g, false);
            }
    }

    private async Task ClearMember(GameSession s, GuildStore.State former)
    {
        await SyncRole(s);
        await SendMemberClear(s, former);
        chat.GuildMembershipChanged(s, former.Id);
    }

    private async Task SendMemberClear(GameSession s, GuildStore.State former)
    {
        // (inferred) 32214 only changes roster. A full reset is the sole observed route that clears GuildBaseInfo.shortUid.
        // Keep real building rows so DecodeBuilding's unguarded pairs(building) can consume the exit packet.
        var clear = new GuildFullInfoNtf
        {
            Guid = 0, ShortUid = 0, Status = GuildStatus.None,
            IconInfo = new GuildHeadIcon(), OfficalRight = new GuildOfficalRight(), Resource = new GuildResource()
        };
        clear.HistoryNames.AddRange(former.HistoryNames.Count > 0 ? former.HistoryNames : [former.Name]);
        clear.Building.AddRange(former.Buildings.Select(k => BuildingInfo(former, k.Key)));
        await s.SendGameAsync(Full, clear);
    }

    private int CheckName(string name, long except = 0)
    {
        var len = Setting("GuildNameNumNew", 2, 7);
        if (name.Length < len[0] || name.Length > len[1]) return ErrorCodes.NameLengthInvalid;
        if (name.All(char.IsDigit)) return ErrorCodes.GuildNameNumbers;
        if (!Regex.IsMatch(name, @"^[\p{L}\p{N} _-]+$") || name != name.Trim()) return ErrorCodes.GuildNameInvalid;
        if (Sensitive(name)) return ErrorCodes.TextSensitive;
        if (store.NameExists(name, except)) return ErrorCodes.GuildNameTaken;
        return 0;
    }

    private bool Sensitive(string text) => options.Value.GuildBlockedWords.Any(word =>
        !string.IsNullOrWhiteSpace(word) && text.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static bool AllNumeric(string text) => text.Length > 0 &&
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    public Task CheckSensitiveAsync(GameSession s, CheckSensitiveWordReq req) => s.SendGameAsync(CheckSensitive,
        new CheckSensitiveWordResp
        {
            Key = req.Key, Content = req.Content,
            Result = (uint)(Sensitive(req.Content) ? ErrorCodes.TextSensitive : 0)
        });

    private long[] Setting(string name, params long[] fallback)
    {
        var row = data.Table("GuildSettingConfig").Values.FirstOrDefault(r => r.TryGetProperty("_kSetting", out var v) && v.GetString() == name);
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("_kValue", out var value)) return fallback;
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray();
        return GameData.Number(value) is { } n ? [(long)n] : fallback;
    }

    /// <summary>A frame (type 1) and a design (type 2) the create widget shows (_iCreateShow), unlocked by default or, for an
    /// existing guild, by a 113 item (GuildService.ItemActions).</summary>
    private int CheckIcon(GuildHeadIcon? icon, GuildStore.State? guild = null)
    {
        if (icon == null) return ErrorCodes.Parameter;
        var frame = data.Row("GuildIconConfig", icon.BackgroundType);
        var design = data.Row("GuildIconConfig", icon.IconType);
        // The create widget sends frame data first (type 1, 100xxx), icon data second (type 2, 200xxx).
        if (frame is not { } f || GameData.Int(f, "_iType") != 1 || !IconUnlocked(guild, f) || GameData.Int(f, "_iCreateShow") == 0) return ErrorCodes.GuildFrameLocked;
        if (design is not { } d || GameData.Int(d, "_iType") != 2 || !IconUnlocked(guild, d) || GameData.Int(d, "_iCreateShow") == 0) return ErrorCodes.GuildIconLocked;
        return 0;
    }

    private async Task CreateCoreAsync(GameSession s, GuildCreateReq req)
    {
        int code = s.Role == null ? ErrorCodes.Parameter
            : !functions.IsOpen(s, 13) ? ErrorCodes.GuildLevelCreate
            : Own(s) != null ? ErrorCodes.AlreadyInGuild
            : CheckName(req.Name);
        if (code == 0) code = CheckIcon(req.Icon);
        if (code == 0 && Sensitive(req.Announce)) code = ErrorCodes.TextSensitive;
        if (code == 0 && AllNumeric(req.Announce)) code = ErrorCodes.GuildTextNumbers;
        if (code == 0 && req.Announce.Length > Setting("GuildNoticeContentNumNew", 0, 80)[1]) code = ErrorCodes.GuildTextLength;
        if (code == 0 && !req.OnlyCheck)
        {
            var cost = Setting("GuildCreateCost", 0, 0);
            bool founding = req.AutoCreateHelper || options.Value.GuildFounding == "responders";
            var guild = new GuildStore.State
            {
                Name = req.Name, Announce = req.Announce, CreateTime = NowMs(),
                Background = req.Icon.BackgroundType, Icon = req.Icon.IconType, Rights = DefaultRights(),
                Status = founding ? (int)GuildStatus.PreCreate : (int)GuildStatus.Normal,
                HelperDeadlineMs = founding ? NowMs() + Setting("GuildResponseTimeLimit", 86400)[0] * 1000 : 0,
                AutoCreateHelper = req.AutoCreateHelper,
            };
            foreach (var row in data.Table("GuildBuildingConfig").Values.Where(r => GameData.Int(r, "_iLv") == 1))
                guild.Buildings[GameData.Int(row, "_iType")] = 1;
            guild.Members[s.Role!.Id] = NewMember(s, 1);
            try
            {
                if (cost.Length >= 2 && cost[0] > 0 && cost[1] > 0)
                {
                    // Even when a later config assigns a creation cost, payment and uniqueness commit together.
                    if (await character.ExchangeLockedAsync(s, [((int)cost[0], cost[1])], [],
                            persist: tx =>
                            {
                                store.EnlistSave(guild, tx); EnlistViews(guild, tx);
                                tx.WithEconomy(GuildPayment(s, "guild-create", (int)cost[0], cost[1]));
                            }, asyncCommit: true) == null)
                        code = ErrorCodes.ItemsShort;
                }
                else if (!await SaveGuildAsync(guild)) code = store.NameExists(req.Name) ? ErrorCodes.GuildNameTaken : ErrorCodes.AlreadyInGuild;
            }
            catch (SqliteException e) when (e.SqliteErrorCode == 19)
            {
                code = store.NameExists(req.Name) ? ErrorCodes.GuildNameTaken : ErrorCodes.AlreadyInGuild;
            }
            if (code == 0)
            {
                await s.SendGameAsync(Create, new GuildCreateResp { Result = 0 });
                await SyncRole(s);
                await SendFull(s, guild, true);
                return;
            }
        }
        await s.SendGameAsync(Create, new GuildCreateResp { Result = code });
    }

    private GuildStore.Member NewMember(GameSession s, int official) => new()
    {
        RoleId = s.Role!.Id, Name = s.Role.Name, Level = s.Progress!.Level,
        Career = s.Progress.Career, EnterTime = NowMs(), OfflineTime = -1, Official = official,
    };

    private GuildStore.Member NewMember(Role role, int official)
    {
        var progress = roles.LoadProgress(role.Id);
        return new GuildStore.Member
        {
            RoleId = role.Id, Name = role.Name, Level = progress.Level,
            Career = progress.Career, EnterTime = NowMs(), OfflineTime = NowSec(), Official = official
        };
    }

    private Task RecommendCoreAsync(GameSession s, GuildRecommendListReq req)
    {
        var list = store.List().Where(g => g.Status == (int)GuildStatus.Normal)
            .Where(g => req.SearchName.Length == 0 || g.Name.Contains(req.SearchName, StringComparison.OrdinalIgnoreCase))
            .Where(g => !req.IsCanJoin || g.Members.Count < MaxMembers(g)).Where(g => g.Id > req.LastGuildShortId).ToList();
        // The bridge omits empty repeated messages; the success DAL ipairs(guilds) cannot parse an empty list.
        // (inferred) A no-match refusal is safer than reporting a false success with an absent list.
        var resp = new GuildRecommendListResp
        {
            Result = list.Count == 0 ? ErrorCodes.GuildMissing : 0,
            SearchName = req.SearchName, TotalGuildCount = list.Count
        };
        resp.Guilds.AddRange(list.Take(Math.Clamp(req.Count, 1, 100)).Select(g => Snapshot(g, s.Role?.Id ?? 0)));
        return s.SendGameAsync(Recommend, resp);
    }

    private Task HelperListCoreAsync(GameSession s, GuildCreateHelperListReq req)
    {
        var guilds = store.List().Where(g => g.Status == (int)GuildStatus.PreCreate && g.HelperDeadlineMs > NowMs()
            && (req.SearchName.Length == 0 || g.Name.Contains(req.SearchName, StringComparison.OrdinalIgnoreCase))).ToArray();
        // Empty repeated fields decode as nil in this bridge; the DAL calls ipairs without a guard.
        var rsp = new GuildCreateHelperListResp
        {
            Result = guilds.Length == 0 ? ErrorCodes.GuildMissing : 0,
            SearchName = req.SearchName
        };
        rsp.Data.AddRange(guilds.Select(g => new GuildCreateHelperListResp.Types.GuildHelperInfo
        { Guild = Snapshot(g, s.Role?.Id ?? 0), FinishTime = g.HelperDeadlineMs }));
        return s.SendGameAsync(HelperList, rsp);
    }

    private Task DetailCoreAsync(GameSession s, GuildDetailInfoReq req)
    {
        var g = store.Get(req.GuildId);
        var resp = new GuildDetailInfoResp { Result = g == null ? ErrorCodes.GuildMissing : 0, GuildId = req.GuildId };
        if (g != null)
        {
            resp.SnapshotData = Snapshot(g, s.Role?.Id ?? 0);
            resp.BuildData.AddRange(g.Buildings.Select(k => new DBGuildBuilding
            {
                BuildingID = k.Key, Level = k.Value,
                EndLevelUpTime = g.BuildingEndTimes.GetValueOrDefault(k.Key)
            }));
            resp.PlayerHistoryData.AddRange(g.Members.Values.Select(m => new PlayerHistoryInfo
            { PlayerId = m.RoleId, Official = m.Official, HistoryContribute = (int)Math.Min(int.MaxValue, m.HistoryContribute) }));
        }
        return s.SendGameAsync(Detail, resp);
    }

    private Task SceneGuildIdCoreAsync(GameSession s) => s.SendGameAsync(SceneGuildId,
        new SwitchSceneSyncGuildId { Guildid = Own(s)?.Id ?? 0 });

    private async Task SyncOnceCoreAsync(GameSession s, SyncGuildDataOnceReq req)
    {
        var guild = Own(s);
        await s.SendGameAsync(SyncOnce, new SyncGuildDataOnceRes());
        if (guild != null && (req.Guildid == 0 || guild.Id == req.Guildid)) await SendFull(s, guild, false);
    }

    private Task MemberIdsCoreAsync(GameSession s, GuildMemberIDListReq req)
    {
        var g = Own(s);
        var response = new GuildMemberIDListResp { Result = g == null || g.Id != req.GuildID ? ErrorCodes.NotInGuild : 0 };
        if (response.Result == 0) response.PlayerIDList.AddRange(g!.Members.Keys);
        return s.SendGameAsync(MemberIds, response);
    }

    private async Task JoinAsync(long roleId, GuildStore.State g, GuildStore.Member? approver = null)
    {
        var online = Online(roleId);
        var member = online != null ? NewMember(online, 20) : NewMember(roles.Get(roleId)!, 20);
        g.Members[member.RoleId] = member;
        g.Applications.Remove(member.RoleId);
        if (approver != null)
            Journal(g, EvApproved, [approver.RoleId, member.RoleId], Str(approver.Name), Str(member.Name), Str(member.Name));
        else Journal(g, EvJoined, [member.RoleId], Str(member.Name));
        if (!await SaveGuildAsync(g)) throw new InvalidOperationException("exclusive guild membership changed during join");
        if (online != null)
        {
            await SyncRole(online);
            await SendFull(online, g, true);
        }
        await PushMember(g, member, GuildMemberInfoChangeNtf.Types.ChangeType.Join);
    }

    private int CanJoin(GameSession s, GuildStore.State? g) => s.Role == null ? ErrorCodes.Parameter
        : g == null ? ErrorCodes.GuildMissing
        : Own(s) != null ? ErrorCodes.AlreadyInGuild
        : g.Status != (int)GuildStatus.Normal ? ErrorCodes.FeatureUnavailable
        : g.Members.Count >= MaxMembers(g) ? ErrorCodes.GuildFull
        : s.Progress!.Level < g.JoinLevelLimit ? ErrorCodes.GuildJoinLevel : 0;

    private int CanJoin(long roleId, GuildStore.State? g) => roles.Get(roleId) == null ? ErrorCodes.GuildApplicationMissing
        : g == null ? ErrorCodes.GuildMissing
        : store.ForRole(roleId) != null ? ErrorCodes.AlreadyInGuild
        : g.Status != (int)GuildStatus.Normal ? ErrorCodes.FeatureUnavailable
        : g.Members.Count >= MaxMembers(g) ? ErrorCodes.GuildFull
        : roles.LoadProgress(roleId).Level < g.JoinLevelLimit ? ErrorCodes.GuildJoinLevel : 0;

    private async Task ExpireApplicationsAsync()
    {
        // GuildApplyTimeout is configured as 72 hours. The unit is inferred from the client timer.
        long cutoff = NowSec() - Setting("GuildApplyTimeout", 72)[0] * 3600;
        foreach (var guild in store.List())
        {
            var expired = guild.Applications.Values.Where(a => a.ApplyTime <= cutoff).ToArray();
            if (expired.Length == 0) continue;
            foreach (var application in expired) guild.Applications.Remove(application.RoleId);
            await SaveGuildAsync(guild);
            foreach (var application in expired)
                await PushApplications(guild, application, GuildEnterApplyChangeNtf.Types.ChangeType.Delete);
        }
    }

    private async Task ApplyCoreAsync(GameSession s, GuildEnterApplyReq req)
    {
        await ExpireApplicationsAsync();
        var resp = new GuildEnterApplyResp { BatchApply = req.BatchApply, Cancel = req.Cancel };
        foreach (long id in req.GuildIds.Distinct().Take(50))
        {
            var g = store.Get(id);
            int code = CanJoin(s, g);
            if (code == 0 && !req.Cancel && g != null && !g.Applications.ContainsKey(s.Role!.Id))
            {
                if (g.Applications.Count >= Setting("ApplicationListMaxNum", 50)[0]) code = ErrorCodes.GuildApplicationListFull;
                else if (store.List().Count(item => item.Applications.ContainsKey(s.Role.Id)) >=
                    Setting("ApplicationListPersonMaxNum", 10)[0]) code = ErrorCodes.GuildApplicationLimit;
            }
            if (req.Cancel && g != null)
                code = g.Applications.ContainsKey(s.Role!.Id) ? 0 : ErrorCodes.GuildApplicationMissing;
            if (code == 0 && g != null)
            {
                if (req.Cancel)
                {
                    var old = g.Applications[s.Role!.Id];
                    g.Applications.Remove(s.Role.Id);
                    await SaveGuildAsync(g);
                    await PushApplications(g, old, GuildEnterApplyChangeNtf.Types.ChangeType.Delete);
                }
                else
                {
                    var a = new GuildStore.Application
                    {
                        RoleId = s.Role!.Id, Name = s.Role.Name,
                        Level = s.Progress!.Level, Career = s.Progress.Career, ApplyTime = NowSec()
                    };
                    g.Applications[a.RoleId] = a;
                    if (!g.AutoAccept) await SaveGuildAsync(g);
                    await PushApplications(g, a, GuildEnterApplyChangeNtf.Types.ChangeType.Add);
                    if (g.AutoAccept) await JoinAsync(s.Role.Id, g);
                }
            }
            resp.ApplyResult.Add(new GuildEnterApplyResp.Types.ApplyResult { GuildId = id, Result = code });
            // For a valid batch, the DAL reads each result; an aggregate failure would skip committed successes.
        }
        if (req.GuildIds.Count == 0) resp.Result = ErrorCodes.Parameter;
        await s.SendGameAsync(Apply, resp);
    }

    private async Task ApplyOpeCoreAsync(GameSession s, GuildEnterApplyOpeReq req)
    {
        await ExpireApplicationsAsync();
        var g = Own(s);
        var resp = new GuildEnterApplyOpeResp { Agree = req.Agree, IsBatch = req.IsBatch };
        foreach (long id in req.PlayerIds.Distinct().Take(50))
        {
            int code = g == null ? ErrorCodes.NotInGuild : !HasRight(g, s.Role!.Id, 2) ? ErrorCodes.GuildPermission
                : !g.Applications.ContainsKey(id) ? ErrorCodes.GuildApplicationMissing : 0;
            var target = roles.Get(id);
            if (code == 0 && req.Agree)
                code = target == null ? ErrorCodes.GuildApplicationMissing : CanJoin(id, g);
            if (code == 0 && g != null)
            {
                var a = g.Applications[id];
                g.Applications.Remove(id);
                if (!req.Agree) await SaveGuildAsync(g);
                await PushApplications(g, a, GuildEnterApplyChangeNtf.Types.ChangeType.Delete);
                if (req.Agree) await JoinAsync(id, g, g.Members[s.Role!.Id]);
            }
            resp.Data.Add(new GuildEnterApplyOpeResp.Types.ApplyRes { PlayerId = id, Result = code });
            // Per-target result remains visible even when another target in the batch succeeds.
        }
        if (req.PlayerIds.Count == 0) resp.Result = ErrorCodes.Parameter;
        await s.SendGameAsync(ApplyOpe, resp);
    }

    private async Task InviteCoreAsync(GameSession s, GuildInviteReq req)
    {
        var g = Own(s);
        var resp = new GuildInviteResp { IsBatch = req.IsBatch };
        foreach (long id in req.TarPlayerID.Distinct().Take(50))
        {
            var target = Online(id);
            int code = g == null ? ErrorCodes.NotInGuild : !HasRight(g, s.Role!.Id, 7) ? ErrorCodes.GuildPermission
                : target == null ? ErrorCodes.PlayerOffline : CanJoin(target, g);
            if (code == 0 && (target!.Guild?.CantBeInvited ?? Personal(id).CantBeInvited)) code = ErrorCodes.GuildInviteRefused;
            if (code == 0)
            {
                long token = ++_nextToken;
                _invites[token] = new Invitation(g!.Id, id, s.Role!.Id, NowMs() + Setting("GuildInvitationTime", 30)[0] * 1000);
                await target!.SendGameAsync(InviteNtf, new GuildInviteNtf { Info = Snapshot(g), Token = token, PlayerId = s.Role.Id });
            }
            resp.InviteResult.Add(new GuildInviteResp.Types.InviteResult { TargetPlayerId = id, Result = code });
            // Per-target result remains visible even when another target in the batch succeeds.
        }
        if (req.TarPlayerID.Count == 0) resp.Result = ErrorCodes.Parameter;
        await s.SendGameAsync(Invite, resp);
    }

    private async Task InviteAckCoreAsync(GameSession s, GuildInviteAckReq req)
    {
        Invitation? invite = null;
        int code = s.Role == null || !_invites.TryGetValue(req.Token, out invite) || invite.Recipient != s.Role.Id
            || invite.GuildId != req.GuildID || invite.ExpiresMs < NowMs() ? ErrorCodes.GuildInviteInvalid : 0;
        // Stage without consuming the token; a failed membership commit can be retried.
        var g = code == 0 ? store.Get(req.GuildID) : null;
        if (code == 0 && req.Accept)
            code = g == null || !g.Members.ContainsKey(invite!.Inviter) ? ErrorCodes.GuildInviteInvalid : CanJoin(s, g);
        await s.SendGameAsync(InviteAck, new GuildInviteAckResp { GuildID = req.GuildID, Accept = req.Accept, Result = code });
        if (code == 0 && req.Accept) await JoinAsync(s.Role!.Id, g!);
        if (invite?.Recipient == s.Role?.Id) _invites.Remove(req.Token);
    }

    private async Task ChangeOfficialCoreAsync(GameSession s, GuildChangeOfficialReq req)
    {
        var g = Own(s);
        int next = (int)req.TarOfficial;
        var row = data.Row("GuildPositionConfig", next);
        int code = g == null ? ErrorCodes.NotInGuild : !HasRight(g, s.Role!.Id, 1) ? ErrorCodes.GuildPermission
            : !g.Members.ContainsKey(req.TarPlayerID) ? ErrorCodes.GuildMemberMissing
            : row == null ? ErrorCodes.Parameter : 0;
        if (code == 0 && g != null)
        {
            var actor = g.Members[s.Role!.Id];
            var target = g.Members[req.TarPlayerID];
            int previous = target.Official;
            if (next == 1)
            {
                if (actor.Official != 1 || target.RoleId == actor.RoleId) code = ErrorCodes.GuildPermission;
                else { actor.Official = 20; target.Official = 1; }
            }
            else if (target.Official == 1 || (actor.Official != 1 && next <= actor.Official)) code = ErrorCodes.GuildPermission;
            else
            {
                int limit = (int)GameData.Int(row!.Value, "_iPositionNum");
                if (limit >= 0 && g.Members.Values.Count(m => m.Official == next) >= limit) code = ErrorCodes.GuildPositionFull;
                else target.Official = next;
            }
            if (code == 0 && next == 1)
                Journal(g, EvLeader, [actor.RoleId, target.RoleId], Str(actor.Name), Str(target.Name));
            else if (code == 0 && previous != next)
                Journal(g, EvPosition, [actor.RoleId, target.RoleId], Str(actor.Name), Str(target.Name), Int(previous), Int(next));
            if (code == 0)
            {
                await SaveGuildAsync(g);
                await RefreshMembers(g);
                await PushMember(g, target, GuildMemberInfoChangeNtf.Types.ChangeType.Update);
            }
        }
        await s.SendGameAsync(ChangeOfficial, new GuildChangeOfficialResp
        {
            Result = code, TarPlayerID = req.TarPlayerID,
            TarOfficial = req.TarOfficial, ResetElit = req.ResetElit
        });
    }

    private async Task LeaveCoreAsync(GameSession s)
    {
        var g = Own(s);
        int code = g == null ? ErrorCodes.NotInGuild : g.Members[s.Role!.Id].Official == 1 && g.Members.Count > 1
            ? ErrorCodes.GuildPermission : HuntingBusy?.Invoke(g.Id) == true ? ErrorCodes.GuildHunting : 0;
        if (code == 0 && g != null)
        {
            var member = g.Members[s.Role!.Id];
            g.Members.Remove(member.RoleId);
            bool elite = DropElite(g, member.RoleId);
            if (g.Members.Count == 0) await DeleteGuildAsync(g, [member.RoleId]);
            else
            {
                Journal(g, EvLeft, [member.RoleId], Str(member.Name));
                await SaveGuildAsync(g, [member.RoleId]);
                if (elite) await PushElitesAsync(g, LeagueElites(g).Values.ToHashSet());
            }
            await s.SendGameAsync(Leave, new GuildMemberLeaveResp { Result = 0 });
            await PushMember(g, member, GuildMemberInfoChangeNtf.Types.ChangeType.Leave);
            await ClearMember(s, g);
            return;
        }
        await s.SendGameAsync(Leave, new GuildMemberLeaveResp { Result = code });
    }

    private async Task KickCoreAsync(GameSession s, GuildMemberKickReq req)
    {
        var g = Own(s);
        int code = g == null ? ErrorCodes.NotInGuild : !HasRight(g, s.Role!.Id, req.IfSingleKick ? 3 : 8)
            ? ErrorCodes.GuildPermission : req.TarPlayerIDs.Count == 0 ? ErrorCodes.Parameter
            : HuntingBusy?.Invoke(g.Id) == true ? ErrorCodes.GuildHunting : 0;
        var removed = new List<GuildStore.Member>();
        var targetViews = new List<(GuildStore.Member Member, GameSession? Session, DBRoleGuildInfo Personal)>();
        bool eliteDropped = false;
        if (code == 0 && g != null)
        {
            foreach (long id in req.TarPlayerIDs.Distinct())
            {
                if (!g.Members.TryGetValue(id, out var m)) { code = ErrorCodes.GuildMemberMissing; break; }
                if (m.Official is not (20 or 21)) { code = ErrorCodes.GuildOfficerKick; break; }
                if (g.Members[s.Role!.Id].Official != 1 && m.Official <= g.Members[s.Role.Id].Official)
                { code = ErrorCodes.GuildPermission; break; }
                removed.Add(m);
            }
            if (code == 0)
            {
                foreach (var m in removed)
                {
                    var target = Online(m.RoleId);
                    var personal = WithoutGuild(target?.Guild ?? Personal(m.RoleId));
                    personal.LastGuildId = g.Id;
                    targetViews.Add((m, target, personal));
                }
                var kicker = g.Members[s.Role!.Id];
                foreach (var m in removed)
                {
                    g.Members.Remove(m.RoleId);
                    eliteDropped |= DropElite(g, m.RoleId);
                    Journal(g, EvKicked, [kicker.RoleId, m.RoleId], Str(kicker.Name), Str(m.Name));
                }
                // The roster index and every personal view share a single commit. No session or chat state
                // changes until the transaction succeeds, so a failed later role write rolls all removals back.
                var transaction = roles.NewTransaction();
                store.EnlistSave(g, transaction, "Guild kick roster write failed");
                foreach (var view in targetViews)
                    transaction.RoleBlob(view.Member.RoleId, "guild_info", view.Personal.ToByteArray());
                await roles.CommitAsync(transaction, "guild/kick");
                // Revoke chat before the first network await; notification failure cannot preserve access.
                foreach (var view in targetViews)
                {
                    if (view.Session == null) continue;
                    view.Session.Guild = view.Personal;
                    chat.GuildMembershipChanged(view.Session, g.Id);
                }
            }
        }
        var resp = new GuildMemberKickResp { Result = code };
        resp.TarPlayerIDs.AddRange(req.TarPlayerIDs);
        if (code == 0 && g != null)
            foreach (var view in targetViews)
            {
                await PushMember(g, view.Member, GuildMemberInfoChangeNtf.Types.ChangeType.Kick);
                if (view.Session != null)
                {
                    await CharacterService.SyncAsync(view.Session, GuildSync, view.Personal);
                    await SendMemberClear(view.Session, g);
                }
            }
        if (code == 0 && g != null && eliteDropped) await PushElitesAsync(g, LeagueElites(g).Values.ToHashSet());
        await s.SendGameAsync(Kick, resp);
    }

    private async Task EditCoreAsync(GameSession s, GuildEditReq req)
    {
        var g = Own(s);
        int code = g == null ? ErrorCodes.NotInGuild : 0;
        int tp = (int)req.Tp;
        if (tp == 9 && g == null && s.Role != null)
        {
            var personal = WithoutGuild(s.Guild ?? Personal(s.Role.Id));
            if (!req.IsCheck) { personal.CantBeInvited = req.CantBeInvited; await SavePersonalAsync(s, personal); await SyncRole(s); }
            await s.SendGameAsync(Edit, new GuildEditResp { Result = 0, Tp = tp, CantBeInvited = personal.CantBeInvited });
            return;
        }
        bool storedWithPayment = false;
        bool publishRecruit = false;
        int right = tp switch { 1 or 5 => 4, 2 => 9, 3 => 5, 4 => 10, 6 or 8 => 2, 7 or 10 => 7, 9 => 0, _ => 0 };
        if (code == 0 && right > 0 && !HasRight(g!, s.Role!.Id, right)) code = ErrorCodes.GuildPermission;
        if (code == 0 && req.GuildId != 0 && req.GuildId != g!.Id) code = ErrorCodes.GuildMissing;
        if (code == 0 && g != null)
        {
            switch (tp)
            {
                case 1:
                    if (Sensitive(req.Announce)) code = ErrorCodes.TextSensitive;
                    else if (AllNumeric(req.Announce)) code = ErrorCodes.GuildTextNumbers;
                    else if (req.Announce.Length > Setting("GuildNoticeContentNumNew", 0, 80)[1]) code = ErrorCodes.GuildTextLength;
                    else if (!req.IsCheck) g.Announce = req.Announce; break;
                case 2:
                    if (req.IsCheck)
                    {
                        code = CheckName(req.Name, g.Id);
                        if (code == 0 && g.LastChangeNameTime > 0 &&
                            NowSec() < g.LastChangeNameTime + Setting("GuildChangeNameTimeLimit", 7)[0] * 86400)
                            code = ErrorCodes.Cooldown;
                    }
                    else
                    {
                        // RunGuildAsync admitted the character gate and complete owner set together.
                        {
                            // Re-read under the payment lock: the guild may have dissolved while the request waited.
                            g = Own(s);
                            code = g == null ? ErrorCodes.NotInGuild : !HasRight(g, s.Role!.Id, 9)
                                ? ErrorCodes.GuildPermission : req.GuildId != 0 && req.GuildId != g.Id
                                ? ErrorCodes.GuildMissing : CheckName(req.Name, g.Id);
                            if (code == 0 && g!.LastChangeNameTime > 0 &&
                                NowSec() < g.LastChangeNameTime + Setting("GuildChangeNameTimeLimit", 7)[0] * 86400)
                                code = ErrorCodes.Cooldown;
                            if (code == 0)
                            {
                                string oldName = g!.Name;
                                long oldTime = g.LastChangeNameTime;
                                g.HistoryNames.Add(oldName);
                                g.Name = req.Name;
                                g.LastChangeNameTime = NowSec();
                                var cost = Setting("GuildChangeNameCost", 0, 0);
                                try
                                {
                                    if (cost.Length >= 2 && cost[0] > 0 && cost[1] > 0)
                                    {
                                        if (await character.ExchangeLockedAsync(s, [((int)cost[0], cost[1])], [],
                                            persist: tx =>
                                            {
                                                store.EnlistSave(g, tx, "guild disappeared during rename"); EnlistViews(g, tx);
                                                tx.WithEconomy(GuildPayment(s, "guild-rename", (int)cost[0], cost[1]));
                                            }, asyncCommit: true) == null)
                                            code = ErrorCodes.ItemsShort;
                                        else storedWithPayment = true;
                                    }
                                    else if (!await SaveGuildAsync(g)) code = ErrorCodes.GuildMissing;
                                    else storedWithPayment = true;
                                }
                                catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
                                {
                                    code = ErrorCodes.GuildNameTaken;
                                }
                                catch (RowMissingException ex) when (ex.Is(GuildStore.Table, g.Id))
                                {
                                    code = ErrorCodes.GuildMissing;
                                }
                                if (code != 0)
                                {
                                    g.Name = oldName;
                                    g.LastChangeNameTime = oldTime;
                                    g.HistoryNames.RemoveAt(g.HistoryNames.Count - 1);
                                }
                            }
                        }
                    }
                    break;
                case 3:
                    code = CheckIcon(req.Icon, g);
                    if (code == 0 && !req.IsCheck) { g.Background = req.Icon.BackgroundType; g.Icon = req.Icon.IconType; }
                    break;
                case 4:
                    code = CheckRights(req.Right);
                    if (code == 0 && !req.IsCheck)
                        foreach (var setting in req.Right.Data)
                            g.Rights[(int)setting.RightType] = setting.AbledOfficial.Select(x => (int)x).ToList();
                    break;
                case 5:
                    if (Sensitive(req.Notice)) code = ErrorCodes.TextSensitive;
                    else if (AllNumeric(req.Notice)) code = ErrorCodes.GuildTextNumbers;
                    else if (req.Notice.Length > Setting("GuildNoticeContentNumNew", 0, 80)[1]) code = ErrorCodes.GuildTextLength;
                    else if (!req.IsCheck) { g.Notice = req.Notice; g.LastChangeNoticeTime = NowSec(); }
                    break;
                case 6: if (!req.IsCheck) g.AutoAccept = req.AutoAcceptApply; break;
                case 7: if (!req.IsCheck) g.AutoCreateHelper = req.AutoCreateHelper; break;
                case 8:
                    if (req.JoinLevelLimit < 0 || req.JoinLevelLimit > 200) code = ErrorCodes.Parameter;
                    else if (!req.IsCheck) g.JoinLevelLimit = req.JoinLevelLimit; break;
                case 9: if (!req.IsCheck) { var personal = (s.Guild ?? new DBRoleGuildInfo()).Clone(); personal.CantBeInvited = req.CantBeInvited; await SavePersonalAsync(s, personal); } break;
                case 10:
                    if (req.IsOpeRecruit && g.LastRecruitMs + Setting("GuildRecruitButtonCD", 300)[0] * 1000 > NowMs())
                        code = ErrorCodes.Cooldown;
                    else if (req.IsOpeRecruit && string.IsNullOrWhiteSpace(req.Announce)) code = ErrorCodes.GuildTextLength;
                    else if (Sensitive(req.Announce)) code = ErrorCodes.TextSensitive;
                    else if (AllNumeric(req.Announce)) code = ErrorCodes.GuildTextNumbers;
                    else if (req.Announce.Length > Setting("GuildNoticeContentNumNew", 0, 80)[1]) code = ErrorCodes.GuildTextLength;
                    else if (req.JoinLevelLimit < 0 || req.JoinLevelLimit > 200) code = ErrorCodes.Parameter;
                    else if (!req.IsCheck)
                    {
                        g.Announce = req.Announce;
                        g.JoinLevelLimit = req.JoinLevelLimit;
                        g.AutoAccept = req.AutoAcceptApply;
                        publishRecruit = req.IsOpeRecruit;
                        if (publishRecruit) g.LastRecruitMs = NowMs();
                    }
                    break;
                default: code = ErrorCodes.FeatureUnavailable; break;
            }
            if (code == 0 && g != null && !req.IsCheck)
            {
                bool saved = tp == 9 || storedWithPayment || await SaveGuildAsync(g);
                if (tp == 4 && saved)
                    foreach (var handler in RightsChanged) handler(g.Id);
                await RefreshMembers(g);
                if (tp is 1 or 2 or 5 or 10)
                {
                    var change = new GuildBaseInfoChangeNtf
                    {
                        Tp = GuildBaseInfoChangeNtf.Types.ChangeType.ChangeBase,
                        Base = new GuildBaseInfoChangeNtf.Types.BaseChangeInfo
                        {
                            Name = g.Name, Level = g.Level,
                            Announce = g.Announce, Notice = g.Notice, Status = (GuildStatus)g.Status,
                            MaxMemberCount = MaxMembers(g), LastChangeNameTime = g.LastChangeNameTime,
                            LastChangeNoticeTime = (int)g.LastChangeNoticeTime
                        }
                    };
                    change.Base.HistoryNames.AddRange(g.HistoryNames.Count > 0 ? g.HistoryNames : [g.Name]);
                    foreach (var m in g.Members.Values) if (Online(m.RoleId) is { } peer) await peer.SendGameAsync(BaseChange, change);
                }
                if (tp == 10)
                {
                    await PushBase(g, new GuildBaseInfoChangeNtf
                    {
                        Tp = GuildBaseInfoChangeNtf.Types.ChangeType.ChangeAutoAcceptApply,
                        AutoAcceptApply = g.AutoAccept
                    });
                    await PushBase(g, new GuildBaseInfoChangeNtf
                    {
                        Tp = GuildBaseInfoChangeNtf.Types.ChangeType.ChangeJoinLevelLimit,
                        JoinLevelLimit = g.JoinLevelLimit
                    });
                    if (publishRecruit)
                    {
                        await chat.PublishRecruitmentAsync(s, g.Announce, g.Id,
                            g.Status == (int)GuildStatus.PreCreate, g.Members.Count - 1);
                    }
                }
            }
        }
        await s.SendGameAsync(Edit, new GuildEditResp
        {
            Result = code, Tp = tp,
            Name = g?.Name ?? "", Announce = g?.Announce ?? "", Notice = g?.Notice ?? "",
            Icon = g == null ? new GuildHeadIcon() : Icon(g), Right = g == null ? new GuildOfficalRight() : Rights(g),
            AutoAcceptApply = g?.AutoAccept ?? false, AutoCreateHelper = g?.AutoCreateHelper ?? false,
            JoinLevelLimit = g?.JoinLevelLimit ?? 0, CantBeInvited = s.Guild?.CantBeInvited ?? false,
            IsOpeRecruit = req.IsOpeRecruit
        });
    }

    /// <summary>The Guild League's handlers for 30410/30411/30412 (GuildLeagueService), run inside this family's guild scope.</summary>
    internal sealed record LeagueHooks(Func<GameSession, uint, Task> MgrData, Func<GameSession, uint, Task> Info, Func<GameSession, uint, Task> GroupInfo);
    internal LeagueHooks? League { get; set; }

    /// <summary>
    /// 30410 Guild League manager data. Without the league service the activity is "not open" and the own guild unranked
    /// (LC_DAL_GuildLeague reads activityInfo and myGuildInfo without nil checks).
    /// </summary>
    private Task LeagueMgrDataCoreAsync(GameSession s, uint msgId)
    {
        if (League is { } league) return league.MgrData(s, msgId);
        var g = Own(s);
        return s.SendGameAsync(msgId, new GetGuildLeagueMgrDataRsp
        {
            ActivityInfo = new LeagueActivityInfo { Open = false, EStatus = GuildLeagueStatus.NotOpen },
            MyGuildInfo = g == null ? new GuildLeagueGuildDungeonData() : new GuildLeagueGuildDungeonData { GuildID = g.Id, Name = g.Name, Icon = Icon(g) },
        });
    }

    private Task LeagueInfoCoreAsync(GameSession s, uint msgId) => League is { } league ? league.Info(s, msgId)
        : s.SendGameAsync(msgId, new GetGuildLeagueInfoRsp { Info = new GuildLeagueInfo() });

    private Task LeagueGroupInfoCoreAsync(GameSession s, uint msgId) => League is { } league ? league.GroupInfo(s, msgId)
        : s.SendGameAsync(msgId, new GetGuildLeagueGroupInfoRsp { EStatus = GuildLeagueStatus.NotOpen });

    /// <summary>32242: config's current-level 107 contribution cost and _iTime minutes. Completion timing is inferred.</summary>
    private async Task BuildUpgradeCoreAsync(GameSession s, GuildBuildLevelUpReq req)
    {
        await ReconcileCoreAsync();
        var g = Own(s);
        long type = req.BuildingID;
        int code = g == null ? ErrorCodes.NotInGuild : !HasRight(g, s.Role!.Id, 11) ? ErrorCodes.GuildPermission
            : !g.Buildings.TryGetValue(type, out _) ? ErrorCodes.Parameter
            : g.BuildingEndTimes.Values.Any(end => end > NowMs()) ? ErrorCodes.GuildBuildBusy : 0;
        JsonElement row = default;
        if (code == 0 && g != null)
        {
            row = data.Table("GuildBuildingConfig").Values.FirstOrDefault(r => GameData.Int(r, "_iType") == type
                && GameData.Int(r, "_iLv") == g.Buildings[type]);
            var next = data.Table("GuildBuildingConfig").Values.FirstOrDefault(r => GameData.Int(r, "_iType") == type
                && GameData.Int(r, "_iLv") == g.Buildings[type] + 1);
            if (row.ValueKind != JsonValueKind.Object || next.ValueKind != JsonValueKind.Object) code = ErrorCodes.GuildBuildMax;
            else if (GameData.Pairs(row, "_kLimit").Any(limit => g.Buildings.GetValueOrDefault(limit.Id) < limit.Value)) code = ErrorCodes.GuildHallLow;
        }
        if (code == 0 && g != null)
        {
            var price = row.GetProperty("_iNewCostMoney");
            long currency = price.ValueKind == JsonValueKind.Array && price.GetArrayLength() > 1 ? (long)(GameData.Number(price[0]) ?? 0) : 0;
            long amount = price.ValueKind == JsonValueKind.Array && price.GetArrayLength() > 1 ? (long)(GameData.Number(price[1]) ?? 0) : 0;
            if (currency != 107 || amount <= 0) code = ErrorCodes.Parameter;
            else if (g.Contribute < amount) code = ErrorCodes.GuildContributeShort;
            else
            {
                g.Contribute -= amount;
                g.BuildingEndTimes[type] = NowMs() + GameData.Int(row, "_iTime") * 60_000;
                Journal(g, EvBuilding, [s.Role!.Id], Str(s.Role.Name), Int(amount), Int(type), Int(g.Buildings[type] + 1));
                var economy = new Ro3Emu.Server.Economy.EconomyEnvelope(Guid.NewGuid().ToString("N"), s.Account!, s.Role!.Id,
                    "guild-build", $"{g.Id}:{type}:{g.Buildings[type]}", $"guild-build:{g.Id}:{type}:{g.Buildings[type]}", NowMs(),
                    [new(new(Ro3Emu.Server.Economy.EconomyAssetKind.GuildResource, 2),
                        new(Ro3Emu.Server.Economy.EconomyOwnerKind.Guild, g.Id.ToString(CultureInfo.InvariantCulture)),
                        Ro3Emu.Server.Economy.EconomyCompartment.GuildTreasury, -amount,
                        Ro3Emu.Server.Economy.EconomyLegKind.Sink, Ro3Emu.Server.Economy.EconomyReason.GuildResourceSpend)], []);
                await SaveGuildAsync(g, economy: economy);
                await s.SendGameAsync(BuildUpgrade, new GuildBuildLevelUpResp
                {
                    Result = 0, BuildingID = type,
                    EndLevelUpTime = g.BuildingEndTimes[type]
                });
                await PushBase(g, new GuildBaseInfoChangeNtf
                {
                    Tp = GuildBaseInfoChangeNtf.Types.ChangeType.ChangeResource,
                    Resource = Resource(g)
                });
                await PushBase(g, new GuildBaseInfoChangeNtf
                {
                    Tp = GuildBaseInfoChangeNtf.Types.ChangeType.ChangeBuilding,
                    Build = BuildingInfo(g, type)
                });
                return;
            }
        }
        await s.SendGameAsync(BuildUpgrade, new GuildBuildLevelUpResp { Result = code, BuildingID = type });
    }

    /// <summary>32231 resource change after another guild system commits an atomic treasury transaction.</summary>
    public Task RefreshResourceAsync(long guildId) => store.Get(guildId) is { } guild
        ? PushBase(guild, new GuildBaseInfoChangeNtf
        {
            Tp = GuildBaseInfoChangeNtf.Types.ChangeType.ChangeResource,
            Resource = Resource(guild)
        }) : Task.CompletedTask;

    private async Task PushBase(GuildStore.State g, GuildBaseInfoChangeNtf change)
    {
        foreach (var m in g.Members.Values)
            if (Online(m.RoleId) is { } peer) await peer.SendGameAsync(BaseChange, change);
    }

    /// <summary>Persisted end times finish on login, heartbeat or next guild operation; restart does not lose progress.</summary>
    private long _lastReconcileMs = long.MinValue;

    /// <summary>The heartbeat trigger of <see cref="ReconcileAsync"/>, at most once per second realm-wide: every
    /// player's ping ran the full scan of every guild (100 scans a second at 500 players).</summary>
    public Task ReconcileOnHeartbeatAsync()
    {
        long nowMs = NowMs();
        if (nowMs - _lastReconcileMs < 1000) return Task.CompletedTask;
        _lastReconcileMs = nowMs;
        return ReconcileAsync();
    }

    /// <summary>Realm-wide guild maintenance: expired applications/helpers, finished buildings.</summary>
    private async Task ReconcileCoreAsync()
    {
        await ExpireApplicationsAsync();
        await ExpireHelpersAsync();
        long now = NowMs();
        foreach (var g in store.List().Where(g => g.BuildingEndTimes.Values.Any(end => end > 0 && end <= now)))
        {
            foreach (long type in g.BuildingEndTimes.Where(k => k.Value > 0 && k.Value <= now).Select(k => k.Key).ToArray())
            {
                g.BuildingEndTimes.Remove(type);
                g.Buildings[type]++;
                if (type == 1) g.Level = (int)g.Buildings[type]; // (inferred) guild level follows Hall level.
                await SaveGuildAsync(g);
                await PushBase(g, new GuildBaseInfoChangeNtf
                {
                    Tp = GuildBaseInfoChangeNtf.Types.ChangeType.ChangeBuilding,
                    Build = BuildingInfo(g, type)
                });
                if (type == 1) await RefreshMembers(g);
            }
        }
    }

    private int CheckRights(GuildOfficalRight? proposed)
    {
        if (proposed == null) return ErrorCodes.Parameter;
        // The edit widget sends only GetNeedShowPermission rows; hidden rights stay server-owned.
        var rows = data.Table("GuildPermissionConfig").Values.Where(r => GameData.Int(r, "_iIsShow") == 1)
            .ToDictionary(r => (int)GameData.Int(r, "_iID"));
        if (proposed.Data.Count != rows.Count || proposed.Data.Select(r => r.RightType).Distinct().Count() != rows.Count) return ErrorCodes.Parameter;
        foreach (var right in proposed.Data)
        {
            if (!rows.TryGetValue((int)right.RightType, out var row)) return ErrorCodes.Parameter;
            var choices = GameData.Pairs(row, "_iDetail").ToDictionary(p => (int)p.Id, p => (int)p.Value);
            foreach (var (official, detail) in choices)
            {
                bool enabled = right.AbledOfficial.Contains(official);
                if ((detail == 0 && enabled) || (detail == 3 && !enabled)) return ErrorCodes.GuildPermission;
            }
            if (right.AbledOfficial.Distinct().Count() != right.AbledOfficial.Count || right.AbledOfficial.Any(o => !choices.ContainsKey((int)o))) return ErrorCodes.Parameter;
        }
        return 0;
    }

    public bool MarketReady => true;
}
