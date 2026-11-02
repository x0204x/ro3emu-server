using System.Text.Json;
using Romsg;
using Tencent.IM;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// The elite roster (32302/32303/32313/32321, isLeagueElite in 32213/32231), the Main Battlefield's elite gate and the
/// capture point / flag custom data of the battle.
/// </summary>
public sealed partial class GuildLeagueTests
{
    private static async Task<QuickJoinEliteGroupRes> SaveElites(BotClient bot, int version, params (int Index, long Role)[] slots)
    {
        var group = new EliteGroup { LabelType = EnumLabelType.LeagueLabel, Version = version };
        group.ElitUnitData.AddRange(slots.Select(s => new EliteUnit { Index = s.Index, PlayerId = s.Role }));
        await bot.SendGameAsync(GuildService.EliteSave, new QuickJoinEliteGroupReq { CurrentEliteData = group });
        return await Next(bot, GuildService.EliteSave, QuickJoinEliteGroupRes.Parser);
    }

    private static async Task<EliteGroup> QueryElites(BotClient bot)
    {
        await bot.SendGameAsync(GuildService.EliteQuery, new GetEliteGroupDataReq());
        return Assert.Single((await Next(bot, GuildService.EliteQuery, GetEliteGroupDataRes.Parser)).EliteGroupData);
    }

    private long Id(BotClient bot) => Session(bot).Role!.Id;

    private static bool EliteChange(GuildBaseInfoChangeNtf n, bool elite) =>
        n.Tp == GuildBaseInfoChangeNtf.Types.ChangeType.ChangeLeagueElite && n.IsLeagueElite == elite;

    [ClientDataFact]
    public async Task EliteRosterSavesAgainstItsVersionAndRights()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        long guild = await Guild("Astra", alice, bob);

        var empty = await QueryElites(alice);
        Assert.Equal((EnumLabelType.LeagueLabel, 0, 0), (empty.LabelType, empty.Version, empty.ElitUnitData.Count));

        // GuildPermissionConfig 20 EliteSetting: a plain member may not edit.
        Assert.Equal(ErrorCodes.GuildPermission, (await SaveElites(bob, 0, (1, Id(bob)))).Result);

        var saved = await SaveElites(alice, 0, (1, Id(alice)), (2, Id(bob)));
        Assert.Equal(0, saved.Result);
        Assert.Equal(1, saved.CurrentEliteData.Version);
        Assert.Equal([Id(alice), Id(bob)], saved.CurrentEliteData.ElitUnitData.OrderBy(u => u.Index).Select(u => u.PlayerId));
        var pushed = await Next(bob, GuildService.EliteChange, EliteGroupChangeNotify.Parser);
        Assert.Equal(1, Assert.Single(pushed.CurrentEliteData).Version);
        await Next(bob, GuildService.BaseChange, GuildBaseInfoChangeNtf.Parser, n => EliteChange(n, true));

        // isLeagueElite in the member's own 32213.
        await bob.SendGameAsync(GuildService.SyncOnce, new SyncGuildDataOnceReq { Guildid = guild });
        Assert.True((await Next(bob, GuildService.Full, GuildFullInfoNtf.Parser, f => f.IsLeagueElite)).IsLeagueElite);

        // The version is an optimistic lock (10047); the answer carries the server's roster.
        var stale = await SaveElites(alice, 0, (1, Id(alice)));
        Assert.Equal((ErrorCodes.EliteChanged, 1, 2), (stale.Result, stale.CurrentEliteData.Version, stale.CurrentEliteData.ElitUnitData.Count));
        Assert.Equal(ErrorCodes.EliteNotMember, (await SaveElites(alice, 1, (1, Id(carol)))).Result);
        Assert.Equal(ErrorCodes.Parameter, (await SaveElites(alice, 1, (41, Id(bob)))).Result);
        Assert.Equal(2, (await QueryElites(alice)).ElitUnitData.Count);

        // Dropping bob turns his own standing off.
        Assert.Equal(0, (await SaveElites(alice, 1, (1, Id(alice)))).Result);
        await Next(bob, GuildService.BaseChange, GuildBaseInfoChangeNtf.Parser, n => EliteChange(n, false));

        // GuildGameplayMember01 [40, 6, 20]: position 21 is above 20 (10048).
        await alice.SendGameAsync(GuildService.ChangeOfficial, new GuildChangeOfficialReq { TarPlayerID = Id(bob), TarOfficial = 21 });
        Assert.Equal(0, (await Next(alice, GuildService.ChangeOfficial, GuildChangeOfficialResp.Parser)).Result);
        Assert.Equal(ErrorCodes.ElitePosition, (await SaveElites(alice, 2, (1, Id(alice)), (2, Id(bob)))).Result);
    }

    [ClientDataFact]
    public async Task QuickEliteTeamFormsAFortyTeamWithAGuildCard()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        long guild = await Guild("Astra", alice, bob);
        await bob.SendGameAsync(ChatService.JoinChannel, new JoinChatChannelReq { GroupId = $"2_{guild}" });
        Assert.Equal(0, (await Next(bob, ChatService.JoinChannel, JoinChatChannelRsp.Parser)).Result);

        // Only the requester on the roster: 10059.
        Assert.Equal(0, (await SaveElites(alice, 0, (1, Id(alice)))).Result);
        await alice.SendGameAsync(GuildService.EliteTeam, new QuickCreateEliteTeamReq());
        Assert.Equal(ErrorCodes.EliteTeamShort, (await Next(alice, GuildService.EliteTeam, QuickCreateEliteTeamRes.Parser)).Result);
        await bob.SendGameAsync(GuildService.EliteTeam, new QuickCreateEliteTeamReq());
        Assert.Equal(ErrorCodes.GuildPermission, (await Next(bob, GuildService.EliteTeam, QuickCreateEliteTeamRes.Parser)).Result);

        Assert.Equal(0, (await SaveElites(alice, 1, (1, Id(alice)), (2, Id(bob)))).Result);
        await alice.SendGameAsync(GuildService.EliteTeam, new QuickCreateEliteTeamReq());
        Assert.Equal(0, (await Next(alice, GuildService.EliteTeam, QuickCreateEliteTeamRes.Parser)).Result);
        var team = await Next(alice, TeamService.TeamAll, TeamTotalDetailInfo.Parser);
        Assert.Equal(TeamType.Forty, team.Info.Info.TeamType);
        long teamId = team.Info.Info.TeamId;
        var invite = await Next(bob, TeamService.TeamNotifyInvitee, NotifyInvitee.Parser);
        Assert.Equal((Id(alice), teamId), (invite.InviteGuid, invite.TeamTotalSimpleInfo.Info.TeamId));

        // The guild channel card: template 102 GuildTeam with the team id (LC_ChatMessageHandler).
        var pushed = await bob.Inbox.WaitAsync(e => e.IsGame(ChatService.TunnelDown) && e.Body.Length > 8
            && BitConverter.ToUInt32(e.Body, 0) == 4
            && NotifyNewTextChatMessage.Parser.ParseFrom(e.Body.AsSpan(8)).Msg.ExtraParam.Contains("102"), Wait);
        var card = NotifyNewTextChatMessage.Parser.ParseFrom(pushed.Body.AsSpan(8)).Msg;
        Assert.Equal($"2_{guild}", card.ReceiveId);
        using var extra = JsonDocument.Parse(card.ExtraParam);
        Assert.Equal(102, extra.RootElement.GetProperty("templateId").GetInt32());
        using var common = JsonDocument.Parse(extra.RootElement.GetProperty("commonData").GetString()!);
        Assert.Equal(teamId.ToString(), common.RootElement.GetProperty("teamId").GetString());
        Assert.Equal(guild, common.RootElement.GetProperty("guildId").GetInt64());

        // The card's link (42014) admits an elite at once.
        await bob.SendGameAsync(TeamService.TeamEnter, new EnterTeamReq { TeamId = teamId });
        Assert.Equal(0, (await Next(bob, TeamService.TeamEnter, EnterTeamResp.Parser)).Result);
        var joined = await Next(bob, TeamService.TeamAll, TeamTotalDetailInfo.Parser);
        Assert.Equal(new[] { Id(alice), Id(bob) }.Order(), joined.MemberList.Keys.Order());
    }

    [ClientDataFact]
    public async Task MainBattlefieldAdmitsElitesAndShowsCirclesAndFlags()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        await using var dave = await InWorld("Dave");
        long astra = await Guild("Astra", alice, bob);
        long borea = await Guild("Borea", carol, dave);

        await Go(At(1, 3, 5, 0, 30));
        await Until(() => Read(() => League.State.Index == 1), "registration");
        await Go(At(1, 3, 21, 0, 30));
        await Until(() => Read(() => League.State.Cup!.SettledRound == 1), "the preliminaries closed");
        await Go(At(1, 5, 21, 0, 30));
        await Until(() => Read(() => League.State.Cup!.SettledRound == 2), "the semifinals closed");

        // The final opens: both rosters were empty, so the eligible members are filled (mail 1125's auto fill).
        await Go(At(1, 6, 20, 30, 5));
        await Next(carol, GuildService.BaseChange, GuildBaseInfoChangeNtf.Parser, n => EliteChange(n, true));
        await Next(bob, GuildService.BaseChange, GuildBaseInfoChangeNtf.Parser, n => EliteChange(n, true));
        var roster = await QueryElites(alice);
        Assert.Equal(new[] { Id(alice), Id(bob) }.Order(), roster.ElitUnitData.Select(u => u.PlayerId).Order());

        var cup = await Read(() => League.State.Cup!);
        var pairs = GuildLeagueBracket.Pairs([.. cup.Groups[0].Pairs.Select(p => new GuildLeagueBracket.Pair(p[0], p[1]))],
            new Dictionary<long, int> { [astra] = 6, [borea] = 6 }, 3);
        int astraCamp = pairs[0].Blue == astra ? 1 : 2;

        // Bob is taken off the roster: the Main Battlefield refuses him (518), the Sub-Battlefield does not.
        Assert.Equal(0, (await SaveElites(alice, roster.Version, (1, Id(alice)))).Result);
        Assert.Equal(ErrorCodes.LeagueElite, await Enter(bob, League.MainScene, astraCamp));
        Assert.Equal(0, await Enter(bob, League.SubScene, astraCamp));

        // Alice enters and gets the three OccupationCircle units with CustomData 12-16.
        Assert.Equal(0, await Enter(alice, League.MainScene, astraCamp));
        var view = await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser, v => v.Bases.Any(b => b.EntityTemplateID == 4800000));
        var circles = view.Bases.Where(b => b.EntityTemplateID == 4800000).ToArray();
        Assert.Equal(3, circles.Length);
        Assert.All(circles, c => Assert.Equal([12, 13, 14, 15, 16], c.CustomDatas.Select(d => d.Id).Order()));
        Assert.Equal([0, 1, 2], circles.Select(c => c.CustomDatas.Single(d => d.Id == 16).CustomParam.LongParam).Order());
        long fireUid = await Read(() => League.Matches.Single().Main.Points[43].Uid);
        var fireCircle = circles.Single(c => (long)c.Uuid == fireUid);
        Assert.Equal(2, fireCircle.CustomDatas.Single(d => d.Id == 16).CustomParam.LongParam); // GuildLeagueOccupyNoticeType [43, 2]

        // Holding Fire: var 10103 names the circle, then the circle turns to Astra's camp, fully progressed and fortified.
        var fire = Service<WorldData>().Map(League.MainScene)!.Regions[43];
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(alice), new MapPoint(fire.X, fire.Y, fire.Z, 0)));
        await Step(8);
        await Next(alice, DungeonService.Event, DungeonEventNotify.Parser, e => e.Vars.Any(v => v.VarId == 10103 && v.Vi64[0] == fireUid));
        var taken = await Next(alice, WorldCoreService.CustomChange, CustomDataChange.Parser, c => (long)c.EntityId == fireUid
            && c.CustomDatas.Any(d => d.Id == 14 && d.CustomParam.LongParam == astraCamp));
        long Value(long id) => taken.CustomDatas.Single(d => d.Id == id).CustomParam.LongParam;
        Assert.Equal((astraCamp == 1 ? -100 : 100, astraCamp), (Value(12), Value(13)));
        Assert.Equal(new long[] { astraCamp == 1 ? 1 : 0, astraCamp == 1 ? 0 : 1 },
            taken.CustomDatas.Single(d => d.Id == 15).CustomParam.ListLongParam);

        // The courtyard Boss carries IsFlag (20); its final blow gives Alice FlagState (19), delivering at Fire clears it.
        await Go(At(1, 6, 20, 33, 1));
        var boss = await Read(() => League.Matches.Single().Boss!);
        Assert.Contains(await Read(() => boss.Entity().CustomDatas), d => d.Id == 20 && d.CustomParam.LongParam == 1);
        await OnLoop(async () =>
        {
            boss.Hp = 1;
            await Service<SceneService>().TeleportInMapAsync(Session(alice), new MapPoint(boss.X, boss.Y, boss.Z, 0));
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = boss.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, WorldCoreService.CustomChange, CustomDataChange.Parser, c => (long)c.EntityId == Id(alice)
            && c.CustomDatas.Any(d => d.Id == 19 && d.CustomParam.LongParam == 1));
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(alice), new MapPoint(fire.X, fire.Y, fire.Z, 0)));
        await Step(2);
        await Next(alice, WorldCoreService.CustomChange, CustomDataChange.Parser, c => (long)c.EntityId == Id(alice)
            && c.CustomDatas.Any(d => d.Id == 19 && d.CustomParam.LongParam == 0));
        await Next(alice, GuildLeagueService.EventNotify, GuildLeagueEventNtf.Parser, e => e.EventID == 1);
    }
}
