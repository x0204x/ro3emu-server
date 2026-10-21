using System.Net;
using System.Net.Sockets;
using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;
using Tencent.IM;

namespace Ro3Emu.Tests;

public sealed partial class GuildTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-guild-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private WebApplication _app = null!;
    private string _http = "";

    public async Task InitializeAsync()
    {
        var (web, micro, gate) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = web;
            o.MicroGatePort = micro;
            o.GameGatePort = gate;
            o.GuildBlockedWords = ["ban"];
        }, _clock);
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{web}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name, bool create = true)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id); await WorldSettle.StallsAsync(_app.Services);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private async Task Level(string name)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={name.ToLowerInvariant()}", new StringContent("!lvl 21"))).EnsureSuccessStatusCode();
    }

    private static GuildCreateReq Create(string name, string notice = "") => new()
    {
        Name = name, Announce = notice,
        Icon = new GuildHeadIcon { BackgroundType = 100101, IconType = 200101 },
    };

    private async Task<long> CreateGuild(BotClient bot, string name)
    {
        await bot.SendGameAsync(GuildService.Create, Create(name));
        Assert.Equal(0, (await Next(bot, GuildService.Create, GuildCreateResp.Parser)).Result);
        var full = await Next(bot, GuildService.Full, GuildFullInfoNtf.Parser, m => m.Guid > 0);
        Assert.Equal(name, full.Name);
        Assert.NotEmpty(full.Building);
        Assert.Single(full.MemberInfo);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Assert.InRange(full.CreateTime, now - 30_000, now + 30_000);
        Assert.InRange(full.MemberInfo[0].EnterTime, now - 30_000, now + 30_000);
        return full.Guid;
    }

    [ClientDataFact]
    public async Task ThreePlayersApplyInviteTransferLeaveAndDissolveOverWire()
    {
        await using var alice = await InWorld("Alice");
        await using var carol = await InWorld("Carol");
        await Level("alice");
        await Level("carol");

        await carol.SendGameAsync(GuildService.HelperList, new GuildCreateHelperListReq());
        Assert.Equal(ErrorCodes.GuildMissing,
            (await Next(carol, GuildService.HelperList, GuildCreateHelperListResp.Parser)).Result);

        await alice.SendGameAsync(GuildService.Create, Create("Banned"));
        Assert.Equal(ErrorCodes.TextSensitive, (await Next(alice, GuildService.Create, GuildCreateResp.Parser)).Result);
        long guildId = await CreateGuild(alice, "Astra");
        await alice.SendGameAsync(GuildService.SyncOnce, new SyncGuildDataOnceReq());
        await Next(alice, GuildService.SyncOnce, SyncGuildDataOnceRes.Parser);
        Assert.Equal(guildId, (await Next(alice, GuildService.Full, GuildFullInfoNtf.Parser, x => x.Guid == guildId && !x.IsJoin)).Guid);
        await carol.SendGameAsync(GuildService.Apply, new GuildEnterApplyReq { GuildIds = { guildId }, Cancel = true });
        Assert.Equal(ErrorCodes.GuildApplicationMissing,
            Assert.Single((await Next(carol, GuildService.Apply, GuildEnterApplyResp.Parser)).ApplyResult).Result);
        await carol.SendGameAsync(GuildService.Create, Create("Astra"));
        Assert.Equal(ErrorCodes.GuildNameTaken, (await Next(carol, GuildService.Create, GuildCreateResp.Parser)).Result);

        long bobId;
        var sessions = _app.Services.GetRequiredService<SessionRegistry>();
        var handover = _app.Services.GetRequiredService<RoleHandover>();
        var owners = _app.Services.GetRequiredService<OwnerRegistry>();
        var detached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unregistered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        GameSession? bobSession = null;
        try
        {
            await using (var bob = await InWorld("Bob"))
            {
                bobId = (long)bob.Self!.Uuid;
                await Level("bob");
                await bob.SendGameAsync(GuildService.Apply, new GuildEnterApplyReq { GuildIds = { guildId, 99999999 }, BatchApply = true });
                var batch = await Next(bob, GuildService.Apply, GuildEnterApplyResp.Parser);
                Assert.Equal(0, batch.Result);
                Assert.Equal(0, batch.ApplyResult.Single(x => x.GuildId == guildId).Result);
                Assert.Equal(ErrorCodes.GuildMissing, batch.ApplyResult.Single(x => x.GuildId != guildId).Result);
                bobSession = sessions.All().Single(s => s.Role?.Id == bobId);
                handover.PhaseForTest = (phase, session) =>
                {
                    if (phase == "Detached" && ReferenceEquals(session, bobSession)) detached.TrySetResult();
                };
                sessions.UnregisteredForTest = session =>
                {
                    if (ReferenceEquals(session, bobSession)) unregistered.TrySetResult();
                };
            }
            await detached.Task.WaitAsync(Wait);
            await unregistered.Task.WaitAsync(Wait);
        }
        finally { handover.PhaseForTest = null; sessions.UnregisteredForTest = null; }
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
        {
            Assert.Equal(RoleOwnerState.Detached, owners.Get(bobId).State);
            Assert.DoesNotContain(sessions.All(), s => s.Role?.Id == bobId);
            return Task.CompletedTask;
        });
        // Approval is durable even while the applicant has no game connection.
        await alice.SendGameAsync(GuildService.ApplyOpe, new GuildEnterApplyOpeReq { Agree = true, PlayerIds = { bobId } });
        var approval = await Next(alice, GuildService.ApplyOpe, GuildEnterApplyOpeResp.Parser);
        Assert.Equal(0, approval.Result);
        Assert.Equal(0, Assert.Single(approval.Data).Result);
        await using var joinedBob = await InWorld("Bob", create: false);
        Assert.Equal(guildId, (await Next(joinedBob, GuildService.GuildSync, DBRoleGuildInfoIncrement.Parser, x => x.GuildId == guildId)).GuildId);

        await alice.SendGameAsync(GuildService.Invite, new GuildInviteReq { TarPlayerID = { (long)carol.Self!.Uuid } });
        var invite = await Next(carol, GuildService.InviteNtf, GuildInviteNtf.Parser);
        Assert.Equal(0, Assert.Single((await Next(alice, GuildService.Invite, GuildInviteResp.Parser)).InviteResult).Result);
        await joinedBob.SendGameAsync(GuildService.InviteAck, new GuildInviteAckReq { GuildID = guildId, Token = invite.Token, Accept = true });
        Assert.Equal(ErrorCodes.GuildInviteInvalid, (await Next(joinedBob, GuildService.InviteAck, GuildInviteAckResp.Parser)).Result);
        await carol.SendGameAsync(GuildService.InviteAck, new GuildInviteAckReq { GuildID = guildId, Token = invite.Token, Accept = true });
        Assert.Equal(0, (await Next(carol, GuildService.InviteAck, GuildInviteAckResp.Parser)).Result);
        await carol.SendGameAsync(GuildService.InviteAck, new GuildInviteAckReq { GuildID = guildId, Token = invite.Token, Accept = true });
        Assert.Equal(ErrorCodes.GuildInviteInvalid, (await Next(carol, GuildService.InviteAck, GuildInviteAckResp.Parser, m => m.Result != 0)).Result);

        await joinedBob.SendGameAsync(ChatService.JoinChannel, new JoinChatChannelReq { GroupId = $"2_{guildId}" });
        Assert.Equal(0, (await Next(joinedBob, ChatService.JoinChannel, JoinChatChannelRsp.Parser)).Result);
        await joinedBob.SendGameAsync(GuildService.Leave, new GuildMemberLeaveReq());
        Assert.Equal(0, (await Next(joinedBob, GuildService.Leave, GuildMemberLeaveResp.Parser)).Result);
        Assert.Equal(0, (await Next(joinedBob, GuildService.Full, GuildFullInfoNtf.Parser, x => x.Guid == 0)).ShortUid);
        await joinedBob.SendGameAsync(ChatService.JoinChannel, new JoinChatChannelReq { GroupId = $"2_{guildId}" });
        Assert.Equal(ErrorCodes.NotInGuild, (await Next(joinedBob, ChatService.JoinChannel, JoinChatChannelRsp.Parser, x => x.Result != 0)).Result);

        await alice.SendGameAsync(GuildService.ChangeOfficial, new GuildChangeOfficialReq { TarPlayerID = (long)carol.Self.Uuid, TarOfficial = 1 });
        Assert.Equal(0, (await Next(alice, GuildService.ChangeOfficial, GuildChangeOfficialResp.Parser)).Result);
        await alice.SendGameAsync(GuildService.Leave, new GuildMemberLeaveReq());
        Assert.Equal(0, (await Next(alice, GuildService.Leave, GuildMemberLeaveResp.Parser)).Result);
        await carol.SendGameAsync(GuildService.Leave, new GuildMemberLeaveReq());
        Assert.Equal(0, (await Next(carol, GuildService.Leave, GuildMemberLeaveResp.Parser)).Result);
        Assert.Null(_app.Services.GetRequiredService<GuildStore>().Get(guildId));
    }

    [ClientDataFact]
    public async Task BuildingUpgradeUsesPersistedContributionAndConfiguredMinutes()
    {
        await using var alice = await InWorld("Alice");
        await Level("alice");
        long id = await CreateGuild(alice, "Builds");
        await alice.SendGameAsync(GuildService.SyncOnce, new SyncGuildDataOnceReq());
        var full = await Next(alice, GuildService.Full, GuildFullInfoNtf.Parser, x => x.Guid == id && !x.IsJoin);
        var visible = new GuildOfficalRight();
        visible.Data.AddRange(full.OfficalRight.Data.Where(x => x.RightType is not (8 or 17 or 18 or 19 or 24 or 25)));
        Assert.Equal(20, visible.Data.Count);
        await alice.SendGameAsync(GuildService.Edit, new GuildEditReq { Tp = (GuildEditReq.Types.EditType)4, Right = visible });
        Assert.Equal(0, (await Next(alice, GuildService.Edit, GuildEditResp.Parser)).Result);
        visible.Data.Add(full.OfficalRight.Data.Single(x => x.RightType == 8));
        await alice.SendGameAsync(GuildService.Edit, new GuildEditReq { Tp = (GuildEditReq.Types.EditType)4, Right = visible });
        Assert.Equal(ErrorCodes.Parameter, (await Next(alice, GuildService.Edit, GuildEditResp.Parser, x => x.Result != 0)).Result);
        await alice.SendGameAsync(GuildService.Edit, new GuildEditReq
        {
            Tp = (GuildEditReq.Types.EditType)10,
            Announce = "Hiring now", JoinLevelLimit = 21, AutoAcceptApply = true
        });
        var recruit = await Next(alice, GuildService.Edit, GuildEditResp.Parser, x => x.Tp == 10);
        Assert.Equal(0, recruit.Result);
        Assert.False(recruit.IsOpeRecruit);
        Assert.Equal("Hiring now", recruit.Announce);
        Assert.Equal(21, recruit.JoinLevelLimit);
        Assert.True(recruit.AutoAcceptApply);
        await alice.SendGameAsync(GuildService.Edit, new GuildEditReq
        {
            Tp = (GuildEditReq.Types.EditType)10,
            Announce = "Publish me", JoinLevelLimit = 0, AutoAcceptApply = false, IsOpeRecruit = true
        });
        Assert.Equal(0, (await Next(alice, GuildService.Edit, GuildEditResp.Parser, x => x.IsOpeRecruit)).Result);
        Assert.Equal("Publish me", _app.Services.GetRequiredService<GuildStore>().Get(id)!.Announce);
        Assert.True(_app.Services.GetRequiredService<GuildStore>().Get(id)!.LastRecruitMs > 0);
        await alice.SendGameAsync(GuildService.BuildUpgrade, new GuildBuildLevelUpReq { BuildingID = 1 });
        Assert.Equal(ErrorCodes.GuildContributeShort, (await Next(alice, GuildService.BuildUpgrade, GuildBuildLevelUpResp.Parser)).Result);

        var store = _app.Services.GetRequiredService<GuildStore>();
        var guild = store.Get(id)!;
        guild.Contribute = 8_000_000; // fixture balance; no unverified earning endpoint
        Assert.True(store.Save(guild));
        long before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await alice.SendGameAsync(GuildService.BuildUpgrade, new GuildBuildLevelUpReq { BuildingID = 1 });
        var result = await Next(alice, GuildService.BuildUpgrade, GuildBuildLevelUpResp.Parser, x => x.Result == 0);
        Assert.InRange(result.EndLevelUpTime - before, 3_590_000, 3_610_000);
        Assert.Equal(0, store.Get(id)!.Contribute);
        await alice.SendGameAsync(GuildService.BuildUpgrade, new GuildBuildLevelUpReq { BuildingID = 1 });
        Assert.Equal(ErrorCodes.GuildBuildBusy, (await Next(alice, GuildService.BuildUpgrade, GuildBuildLevelUpResp.Parser, x => x.Result != 0)).Result);
        guild = store.Get(id)!;
        guild.BuildingEndTimes[1] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1;
        Assert.True(store.Save(guild));
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() => _app.Services.GetRequiredService<GuildService>().ReconcileAsync());
        guild = store.Get(id)!;
        Assert.Equal(2, guild.Buildings[1]);
        Assert.Equal(2, guild.Level);
        Assert.False(guild.BuildingEndTimes.ContainsKey(1));
        Assert.Equal(2, (await Next(alice, GuildService.BaseChange, GuildBaseInfoChangeNtf.Parser,
            x => x.Build?.BuildingID == 1 && x.Build.Level == 2)).Build.Level);
    }

    [ClientDataFact]
    public async Task ManagementRejectsUnprivilegedEditsAndOfficerKickThenPersistsOfflineKick()
    {
        await using var alice = await InWorld("Alice");
        await using var carol = await InWorld("Carol");
        await Level("alice");
        await Level("carol");
        long id = await CreateGuild(alice, "Manage");
        await alice.SendGameAsync(GuildService.Edit, new GuildEditReq { Tp = (GuildEditReq.Types.EditType)6, AutoAcceptApply = true });
        Assert.Equal(0, (await Next(alice, GuildService.Edit, GuildEditResp.Parser)).Result);

        long bobId;
        await using (var bob = await InWorld("Bob"))
        {
            bobId = (long)bob.Self!.Uuid;
            await Level("bob");
            foreach (var member in new[] { bob, carol })
            {
                await member.SendGameAsync(GuildService.Apply, new GuildEnterApplyReq { GuildIds = { id } });
                Assert.Equal(0, Assert.Single((await Next(member, GuildService.Apply, GuildEnterApplyResp.Parser)).ApplyResult).Result);
                Assert.Equal(id, (await Next(member, GuildService.Full, GuildFullInfoNtf.Parser, x => x.IsJoin)).Guid);
            }
            await bob.SendGameAsync(GuildService.Edit, new GuildEditReq { Tp = (GuildEditReq.Types.EditType)1, Announce = "Unauthorized" });
            Assert.Equal(ErrorCodes.GuildPermission, (await Next(bob, GuildService.Edit, GuildEditResp.Parser)).Result);
            await alice.SendGameAsync(GuildService.ChangeOfficial, new GuildChangeOfficialReq { TarPlayerID = bobId, TarOfficial = 2 });
            Assert.Equal(0, (await Next(alice, GuildService.ChangeOfficial, GuildChangeOfficialResp.Parser)).Result);
            await alice.SendGameAsync(GuildService.ChangeOfficial, new GuildChangeOfficialReq { TarPlayerID = (long)carol.Self!.Uuid, TarOfficial = 2 });
            Assert.Equal(ErrorCodes.GuildPositionFull,
                (await Next(alice, GuildService.ChangeOfficial, GuildChangeOfficialResp.Parser, x => x.TarPlayerID == (long)carol.Self.Uuid)).Result);
            await alice.SendGameAsync(GuildService.Kick, new GuildMemberKickReq { TarPlayerIDs = { bobId }, IfSingleKick = true });
            Assert.Equal(ErrorCodes.GuildOfficerKick, (await Next(alice, GuildService.Kick, GuildMemberKickResp.Parser)).Result);
            await alice.SendGameAsync(GuildService.ChangeOfficial, new GuildChangeOfficialReq { TarPlayerID = bobId, TarOfficial = 20 });
            Assert.Equal(0, (await Next(alice, GuildService.ChangeOfficial, GuildChangeOfficialResp.Parser, x => x.TarOfficial == 20)).Result);
        }

        await alice.SendGameAsync(GuildService.Kick, new GuildMemberKickReq { TarPlayerIDs = { bobId }, IfSingleKick = true });
        Assert.Equal(0, (await Next(alice, GuildService.Kick, GuildMemberKickResp.Parser, x => x.Result == 0)).Result);
        // The success response is the persistence boundary; no delay or retry may conceal a stale guild view.
        var personal = DBRoleGuildInfo.Parser.ParseFrom(_app.Services.GetRequiredService<RoleStore>().LoadGuildInfo(bobId));
        Assert.Equal(0, personal.GuildId);
        Assert.Equal(id, personal.LastGuildId);
        await using var rejoinedBob = await InWorld("Bob", create: false);
        Assert.Equal(0, (await Next(rejoinedBob, GuildService.GuildSync, DBRoleGuildInfoIncrement.Parser, x => x.SyncAllMark)).GuildId);

        await alice.SendGameAsync(GuildService.Edit, new GuildEditReq { Tp = (GuildEditReq.Types.EditType)5, Notice = "42" });
        Assert.Equal(ErrorCodes.GuildTextNumbers, (await Next(alice, GuildService.Edit, GuildEditResp.Parser)).Result);
        await alice.SendGameAsync(GuildService.Edit, new GuildEditReq { Tp = (GuildEditReq.Types.EditType)5, Notice = "Meet tonight" });
        Assert.Equal(0, (await Next(alice, GuildService.Edit, GuildEditResp.Parser, x => x.Notice == "Meet tonight")).Result);
        Assert.Equal("Meet tonight", (await Next(carol, GuildService.BaseChange, GuildBaseInfoChangeNtf.Parser,
            x => x.Base?.Notice == "Meet tonight")).Base.Notice);
    }

    [ClientDataFact]
    public async Task PrecreateHelpersTransitionAndRecruitmentPushOverWire()
    {
        await using var founder = await InWorld("Founder");
        await using var helper1 = await InWorld("Helperone");
        await using var helper2 = await InWorld("Helpertwo");
        await using var helper3 = await InWorld("Helperthree");
        await Level("founder");
        await Level("helperone");
        await Level("helpertwo");
        await Level("helperthree");
        var create = Create("Helpers", "Join us");
        create.AutoCreateHelper = true;
        await founder.SendGameAsync(GuildService.Create, create);
        Assert.Equal(0, (await Next(founder, GuildService.Create, GuildCreateResp.Parser)).Result);
        var pre = await Next(founder, GuildService.Full, GuildFullInfoNtf.Parser, x => x.Guid > 0);
        Assert.Equal(GuildStatus.PreCreate, pre.Status);
        using (var client = new HttpClient())
            (await client.PostAsync($"{_http}/ro3emu/gm?account=founder", new StringContent("!lvl 99"))).EnsureSuccessStatusCode();
        await founder.SendGameAsync(GuildService.MarketList, new ListOnBlackMarketReq { UnitPrice = 500, Count = 10 });
        Assert.Equal(ErrorCodes.FeatureUnavailable,
            (await Next(founder, GuildService.MarketList, ListOnBlackMarketRsp.Parser)).Result);
        await founder.SendGameAsync(GuildService.Edit, new GuildEditReq
        {
            Tp = (GuildEditReq.Types.EditType)10,
            Announce = "Help build", JoinLevelLimit = 21, AutoAcceptApply = true
        });
        Assert.Equal(0, (await Next(founder, GuildService.Edit, GuildEditResp.Parser, x => x.Tp == 10)).Result);
        Assert.Equal(GuildStatus.PreCreate, (await Next(founder, GuildService.BaseChange,
            GuildBaseInfoChangeNtf.Parser, x => x.Base?.Announce == "Help build")).Base.Status);
        Assert.Equal((int)GuildStatus.PreCreate, _app.Services.GetRequiredService<GuildStore>().Get(pre.Guid)!.Status);
        await helper1.SendGameAsync(GuildService.HelperList, new GuildCreateHelperListReq());
        var list = await Next(helper1, GuildService.HelperList, GuildCreateHelperListResp.Parser);
        Assert.Equal(0, list.Result);
        Assert.Equal(pre.Guid, Assert.Single(list.Data).Guild.Guid);

        // GuildResponsePeopleLimit 3 counts the founder: two responders found the guild.
        foreach (var helper in new[] { helper1, helper2 })
        {
            await helper.SendGameAsync(GuildService.HelperOpe, new GuildCreateHelperOpeReq { GuildIds = { pre.Guid } });
            var rsp = await Next(helper, GuildService.HelperOpe, GuildCreateHelperOpeResp.Parser);
            Assert.Equal(0, rsp.Result);
            Assert.True(Assert.Single(rsp.HelperResult).Success);
        }
        var normal = await Next(founder, GuildService.Full, GuildFullInfoNtf.Parser,
            x => x.Guid == pre.Guid && x.Status == GuildStatus.Normal);
        Assert.Equal(3, normal.MemberInfo.Count);
        await helper3.SendGameAsync(GuildService.HelperOpe, new GuildCreateHelperOpeReq { GuildIds = { pre.Guid } });
        Assert.False(Assert.Single((await Next(helper3, GuildService.HelperOpe, GuildCreateHelperOpeResp.Parser)).HelperResult).Success);

        await helper1.SendGameAsync(ChatService.JoinChannel, new JoinChatChannelReq { GroupId = "@TGS#_8_0" });
        Assert.Equal(0, (await Next(helper1, ChatService.JoinChannel, JoinChatChannelRsp.Parser)).Result);
        await founder.SendGameAsync(GuildService.Recruit, new GuildRecruitReq { Content = "Join our guild" });
        Assert.Equal(0, (await Next(founder, GuildService.Recruit, GuildRecruitResp.Parser)).Result);
        var pushed = await helper1.Inbox.WaitAsync(x => x.IsGame(ChatService.TunnelDown) && x.Body.Length > 8
            && BinaryPrimitives.ReadUInt32LittleEndian(x.Body) == 4, Wait);
        var chatMessage = NotifyNewTextChatMessage.Parser.ParseFrom(pushed.Body.AsSpan(8));
        Assert.Equal("Join our guild", chatMessage.Msg.Content);
        Assert.Equal("@TGS#_8_0", chatMessage.Msg.ReceiveId);
        using var special = JsonDocument.Parse(chatMessage.Msg.ExtraParam);
        Assert.Equal(101, special.RootElement.GetProperty("templateId").GetInt32());
        await founder.SendGameAsync(GuildService.Recruit, new GuildRecruitReq { Content = "Again" });
        Assert.Equal(ErrorCodes.Cooldown, (await Next(founder, GuildService.Recruit, GuildRecruitResp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task BlackMarketListsTradesRecordsAndEnforcesBalancesOverWire()
    {
        await using var seller = await InWorld("Seller");
        await using var buyer = await InWorld("Buyer");
        await Level("seller");
        await Level("buyer");
        long sellerId = await CreateGuild(seller, "Sell");
        long buyerId = await CreateGuild(buyer, "Buy");
        var store = _app.Services.GetRequiredService<GuildStore>();
        var sellGuild = store.Get(sellerId)!;
        sellGuild.Materials = 100;
        Assert.True(store.Save(sellGuild));
        var buyGuild = store.Get(buyerId)!;
        buyGuild.Money = 10000;
        Assert.True(store.Save(buyGuild));
        await seller.SendGameAsync(GuildService.MarketList, new ListOnBlackMarketReq { UnitPrice = 500, Count = 10 });
        Assert.Equal(ErrorCodes.FeatureUnavailable, (await Next(seller, GuildService.MarketList, ListOnBlackMarketRsp.Parser)).Result);
        using (var client = new HttpClient())
        {
            (await client.PostAsync($"{_http}/ro3emu/gm?account=seller", new StringContent("!lvl 99"))).EnsureSuccessStatusCode();
            (await client.PostAsync($"{_http}/ro3emu/gm?account=buyer", new StringContent("!lvl 99"))).EnsureSuccessStatusCode();
        }
        await seller.SendGameAsync(GuildService.MarketList, new ListOnBlackMarketReq { UnitPrice = 500, Count = 20 });
        Assert.Equal(0, (await Next(seller, GuildService.MarketList, ListOnBlackMarketRsp.Parser, x => x.Result == 0)).Result);
        Assert.Equal(80, store.Get(sellerId)!.Materials);
        var market = store.LoadMarket();
        var stall = Assert.Single(market.Stalls);
        Assert.True(stall.AvailableMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await buyer.SendGameAsync(GuildService.MarketItems, new GetBlackMarketItemsReq());
        Assert.Equal(ErrorCodes.GuildMissing, (await Next(buyer, GuildService.MarketItems, GetBlackMarketItemsRsp.Parser)).Result);
        stall.AvailableMs = 0; // advance the configured listing delay without sleeping in a wire test
        Assert.True(store.SaveMarket(market, []));
        await buyer.SendGameAsync(GuildService.MarketItems, new GetBlackMarketItemsReq());
        Assert.Equal(20, Assert.Single((await Next(buyer, GuildService.MarketItems, GetBlackMarketItemsRsp.Parser, x => x.Result == 0)).Items).TotalRemainCount);
        await buyer.SendGameAsync(GuildService.MarketBuy, new BuyBlackMarketItemReq { UnitPrice = 500, BuyCount = 30 });
        Assert.Equal(ErrorCodes.ItemsShort, (await Next(buyer, GuildService.MarketBuy, BuyBlackMarketItemRsp.Parser)).Result);
        await buyer.SendGameAsync(GuildService.MarketBuy, new BuyBlackMarketItemReq { UnitPrice = 500, BuyCount = 10 });
        Assert.Equal(0, (await Next(buyer, GuildService.MarketBuy, BuyBlackMarketItemRsp.Parser, x => x.Result == 0)).Result);
        Assert.Equal(5000, store.Get(buyerId)!.Money);
        Assert.Equal(10, store.Get(buyerId)!.Materials);
        Assert.Equal(4875, store.Get(sellerId)!.Money);
        await seller.SendGameAsync(GuildService.MarketSellRecords, new GetBlackMarketSellRecordsReq());
        Assert.Equal(10, Assert.Single((await Next(seller, GuildService.MarketSellRecords, GetBlackMarketSellRecordsRsp.Parser)).Records).Count);
        await buyer.SendGameAsync(GuildService.MarketBuyRecords, new GetBlackMarketBuyRecordsReq());
        Assert.Equal(10, Assert.Single((await Next(buyer, GuildService.MarketBuyRecords, GetBlackMarketBuyRecordsRsp.Parser)).Records).Count);
        await buyer.SendGameAsync(GuildService.MarketUnlist, new UnlistFromBlackMarketReq { StallUniqueId = stall.Id });
        Assert.NotEqual(0, (await Next(buyer, GuildService.MarketUnlist, UnlistFromBlackMarketRsp.Parser)).Result);
        await seller.SendGameAsync(GuildService.MarketUnlist, new UnlistFromBlackMarketReq { StallUniqueId = stall.Id });
        Assert.Equal(0, (await Next(seller, GuildService.MarketUnlist, UnlistFromBlackMarketRsp.Parser, x => x.Result == 0)).Result);
        Assert.Equal(90, store.Get(sellerId)!.Materials);
        Assert.Empty(store.LoadMarket().Stalls);
    }

    [ClientDataFact]
    public async Task DissolvedSellerStallDoesNotBlockOtherSamePriceSeller()
    {
        await using var orphan = await InWorld("Orphan");
        await using var seller = await InWorld("Seller");
        await using var buyer = await InWorld("Buyer");
        foreach (var name in new[] { "orphan", "seller", "buyer" })
        {
            await Level(name);
            using var client = new HttpClient();
            (await client.PostAsync($"{_http}/ro3emu/gm?account={name}", new StringContent("!lvl 99"))).EnsureSuccessStatusCode();
        }
        long orphanId = await CreateGuild(orphan, "Gone");
        long sellerId = await CreateGuild(seller, "Remain");
        long buyerId = await CreateGuild(buyer, "Buyers");
        var store = _app.Services.GetRequiredService<GuildStore>();
        foreach (long id in new[] { orphanId, sellerId })
        {
            var guild = store.Get(id)!;
            guild.Materials = 10;
            Assert.True(store.Save(guild));
        }
        var funded = store.Get(buyerId)!;
        funded.Money = 5000;
        Assert.True(store.Save(funded));
        foreach (var bot in new[] { orphan, seller })
        {
            await bot.SendGameAsync(GuildService.MarketList, new ListOnBlackMarketReq { UnitPrice = 500, Count = 10 });
            Assert.Equal(0, (await Next(bot, GuildService.MarketList, ListOnBlackMarketRsp.Parser)).Result);
        }
        var market = store.LoadMarket();
        Assert.Equal(2, market.Stalls.Count);
        foreach (var stall in market.Stalls) stall.AvailableMs = 0;
        Assert.True(store.SaveMarket(market, []));
        store.Delete(orphanId); // guild dissolution can delete the seller while its market escrow remains
        var openingEconomy = MarketEconomyEvidence.Cut(_dataDir, RepoRoot());
        await buyer.SendGameAsync(GuildService.MarketItems, new GetBlackMarketItemsReq());
        var visible = await Next(buyer, GuildService.MarketItems, GetBlackMarketItemsRsp.Parser);
        Assert.Equal(10, Assert.Single(visible.Items).TotalRemainCount);
        Assert.Single(store.LoadMarket().Stalls);
        var recovered = Assert.Single(store.LoadMarket().RecoveryStalls);
        Assert.Equal(orphanId, recovered.GuildId);
        Assert.Equal(10, recovered.RemainCount);
        var recoveryLedger = MarketEconomyEvidence.Exact(_dataDir, RepoRoot(), openingEconomy,
            MarketEconomyEvidence.Cut(_dataDir, RepoRoot()), recovery: true);
        Assert.DoesNotContain(recoveryLedger.Legs, x => x.Kind is Ro3Emu.Server.Economy.EconomyLegKind.Source
            or Ro3Emu.Server.Economy.EconomyLegKind.Sink);
        Assert.Contains(recoveryLedger.Legs, x => x.Location.Compartment == Ro3Emu.Server.Economy.EconomyCompartment.Recovery && x.Delta == 10);
        await buyer.SendGameAsync(GuildService.MarketBuy, new BuyBlackMarketItemReq { UnitPrice = 500, BuyCount = 10 });
        Assert.Equal(0, (await Next(buyer, GuildService.MarketBuy, BuyBlackMarketItemRsp.Parser)).Result);
        Assert.Equal(4875, store.Get(sellerId)!.Money);
        Assert.Equal(10, store.Get(buyerId)!.Materials);
    }

    [ClientDataFact]
    public async Task BatchKickRollsBackEveryMemberWhenSecondPersonalWriteFailsThenRevokesChatOnRetry()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        await Level("alice");
        await Level("bob");
        await Level("carol");
        long guildId = await CreateGuild(alice, "Batch");
        await alice.SendGameAsync(GuildService.Edit, new GuildEditReq { Tp = (GuildEditReq.Types.EditType)6, AutoAcceptApply = true });
        Assert.Equal(0, (await Next(alice, GuildService.Edit, GuildEditResp.Parser)).Result);
        foreach (var member in new[] { bob, carol })
        {
            await member.SendGameAsync(GuildService.Apply, new GuildEnterApplyReq { GuildIds = { guildId } });
            Assert.Equal(0, Assert.Single((await Next(member, GuildService.Apply, GuildEnterApplyResp.Parser)).ApplyResult).Result);
            await Next(member, GuildService.Full, GuildFullInfoNtf.Parser, x => x.Guid == guildId && x.IsJoin);
            await member.SendGameAsync(ChatService.JoinChannel, new JoinChatChannelReq { GroupId = $"2_{guildId}" });
            Assert.Equal(0, (await Next(member, ChatService.JoinChannel, JoinChatChannelRsp.Parser)).Result);
        }

        var sessions = _app.Services.GetRequiredService<SessionRegistry>();
        var bobSession = sessions.All().Single(x => x.Role?.Id == (long)bob.Self!.Uuid);
        var carolSession = sessions.All().Single(x => x.Role?.Id == (long)carol.Self!.Uuid);
        var aliceSession = sessions.All().Single(x => x.Role?.Id == (long)alice.Self!.Uuid);
        var roles = _app.Services.GetRequiredService<RoleStore>();
        var guilds = _app.Services.GetRequiredService<GuildStore>();
        long bobId = bobSession.Role!.Id, carolId = carolSession.Role!.Id;
        byte[] bobBefore = roles.LoadGuildInfo(bobId)!, carolBefore = roles.LoadGuildInfo(carolId)!;
        var request = new GuildMemberKickReq { TarPlayerIDs = { bobId, carolId }, IfSingleKick = false };

        FixtureSql.Execute(_app.Services.GetRequiredService<AccountStore>(),
            $"CREATE TRIGGER fail_second_kick BEFORE UPDATE OF guild_info ON role WHEN NEW.id = {carolId} "
            + "BEGIN SELECT RAISE(ABORT, 'second personal view failure'); END");
        try
        {
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
                _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
                    _app.Services.GetRequiredService<GuildService>().KickAsync(aliceSession, request)));
            Assert.Equal(guildId, guilds.ForRole(bobId)!.Id);
            Assert.Equal(guildId, guilds.ForRole(carolId)!.Id);
            Assert.Equal(bobBefore, roles.LoadGuildInfo(bobId));
            Assert.Equal(carolBefore, roles.LoadGuildInfo(carolId));
            Assert.Equal(guildId, bobSession.Guild!.GuildId);
            Assert.Equal(guildId, carolSession.Guild!.GuildId);
            Assert.Contains($"2_{guildId}", bobSession.ChatGroups);
            Assert.Contains($"2_{guildId}", carolSession.ChatGroups);
        }
        finally
        {
            FixtureSql.Execute(_app.Services.GetRequiredService<AccountStore>(), "DROP TRIGGER fail_second_kick");
        }

        await alice.SendGameAsync(GuildService.Kick, request);
        Assert.Equal(0, (await Next(alice, GuildService.Kick, GuildMemberKickResp.Parser)).Result);
        foreach (var (id, session) in new[] { (bobId, bobSession), (carolId, carolSession) })
        {
            Assert.Null(guilds.ForRole(id));
            var personal = DBRoleGuildInfo.Parser.ParseFrom(roles.LoadGuildInfo(id));
            Assert.Equal(0, personal.GuildId);
            Assert.Equal(guildId, personal.LastGuildId);
            Assert.Equal(0, session.Guild!.GuildId);
            Assert.DoesNotContain($"2_{guildId}", session.ChatGroups);
        }
    }

    [ClientDataFact]
    public async Task RenamePaymentIsAtomicAndAQueuedRenameCannotResurrectDissolvedGuild()
    {
        await using var alice = await InWorld("Alice");
        await Level("alice");
        long id = await CreateGuild(alice, "First");
        await alice.SendGameAsync(GuildService.Edit, new GuildEditReq { Tp = (GuildEditReq.Types.EditType)2, Name = "Second" });
        Assert.Equal(ErrorCodes.ItemsShort, (await Next(alice, GuildService.Edit, GuildEditResp.Parser)).Result);
        Assert.Equal("First", _app.Services.GetRequiredService<GuildStore>().Get(id)!.Name);
        using (var client = new HttpClient())
            (await client.PostAsync($"{_http}/ro3emu/gm?account=alice", new StringContent("!item 3 20"))).EnsureSuccessStatusCode();
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(x => x.Role?.Id == (long)alice.Self!.Uuid);
        long funded = session.Progress!.Currency.GetValueOrDefault(3);
        Assert.Equal(20, funded);
        await alice.SendGameAsync(GuildService.Edit, new GuildEditReq { Tp = (GuildEditReq.Types.EditType)2, Name = "Second" });
        Assert.Equal(0, (await Next(alice, GuildService.Edit, GuildEditResp.Parser, x => x.Name == "Second")).Result);
        Assert.Equal("Second", _app.Services.GetRequiredService<GuildStore>().Get(id)!.Name);
        Assert.Equal(10, session.Progress.Currency.GetValueOrDefault(3));

        var guild = _app.Services.GetRequiredService<GuildStore>().Get(id)!;
        guild.LastChangeNameTime = 0; // permit the queued second rename in this race fixture
        Assert.True(_app.Services.GetRequiredService<GuildStore>().Save(guild));
        await session.StateLock.WaitAsync();
        Task? pendingRename = null;
        try
        {
            var loop = _app.Services.GetRequiredService<GameLoop>();
            var service = _app.Services.GetRequiredService<GuildService>();
            // Start on the game loop and return only after EditAsync reaches the held StateLock.
            await loop.InvokeAsync(() =>
            {
                pendingRename = loop.TrackAsync("test guild rename", () =>
                    service.EditAsync(session, new GuildEditReq { Tp = (GuildEditReq.Types.EditType)2, Name = "Third" }));
                return Task.CompletedTask;
            });
            await loop.InvokeAsync(() => service.LeaveAsync(session));
            Assert.Equal(0, (await Next(alice, GuildService.Leave, GuildMemberLeaveResp.Parser)).Result);
        }
        finally { session.StateLock.Release(); }
        await pendingRename!;
        Assert.NotEqual(0, (await Next(alice, GuildService.Edit, GuildEditResp.Parser, x => x.Name == "Third" || x.Result != 0)).Result);
        Assert.Null(_app.Services.GetRequiredService<GuildStore>().Get(id));
        Assert.Equal(10, session.Progress.Currency.GetValueOrDefault(3));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

    /// <summary>104007 CheckJoinGuild reads the membership: "Join a guild" (10200080, automatic) and 100026 complete
    /// when the role founds a guild, and stay complete after a relogin.</summary>
    [ClientDataFact]
    public async Task FoundingAGuildCompletesTheJoinAGuildQuests()
    {
        var alice = await InWorld("Alice");
        await Level("alice");
        await QuestProbe.AcceptAsync(_app, _http, "alice", 100026); // [104007,1], claimed in the panel
        await QuestProbe.AtAsync(_app, "alice", 100026, 0);
        await QuestProbe.AcceptAsync(_app, _http, "alice", 10200080); // [104007,1], delivered automatically
        await CreateGuild(alice, "Joiners");
        await QuestProbe.DoneAsync(_app, "alice", 100026);
        await QuestProbe.WaitAsync(_app, "alice", 10200080, i => i.Status == TaskState.Delivery, "delivered");

        await alice.DisposeAsync();
        await QuestProbe.OfflineAsync(_app, "alice");
        await using var again = await InWorld("Alice", create: false);
        Assert.Equal(1, (await QuestProbe.DoneAsync(_app, "alice", 100026)).CompleteNum[0]);
    }
}
