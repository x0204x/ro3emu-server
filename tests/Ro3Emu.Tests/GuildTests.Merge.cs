using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class GuildTests
{
    [ClientDataFact]
    public async Task AnAcceptedMergeMovesTheChosenMembersAtFiveAndDisbandsTheInvitedGuild()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        await using var dave = await InWorld("Dave");
        foreach (string name in new[] { "alice", "bob", "carol", "dave" }) await Level(name);
        long astra = await CreateGuild(alice, "Astra");
        long beta = await CreateGuild(bob, "Beta");
        long bobId = (long)bob.Self!.Uuid, carolId = (long)carol.Self!.Uuid, daveId = (long)dave.Self!.Uuid;
        foreach (var bot in new[] { carol, dave })
        {
            await bot.SendGameAsync(GuildService.Apply, new GuildEnterApplyReq { GuildIds = { beta } });
            Assert.Equal(0, Assert.Single((await Next(bot, GuildService.Apply, GuildEnterApplyResp.Parser)).ApplyResult).Result);
            await bob.SendGameAsync(GuildService.ApplyOpe, new GuildEnterApplyOpeReq { Agree = true, PlayerIds = { (long)bot.Self!.Uuid } });
            Assert.Equal(0, (await Next(bob, GuildService.ApplyOpe, GuildEnterApplyOpeResp.Parser)).Result);
        }

        // Astra invites Beta; Beta sees it and accepts.
        await alice.SendGameAsync(GuildService.MergeRecommend, new PullRecommendMergeGuildDataReq());
        Assert.Contains((await Next(alice, GuildService.MergeRecommend, PullRecommendMergeGuildDataRes.Parser)).Data, d => d.Guid == beta);
        await alice.SendGameAsync(GuildService.MergeAsk, new InviteMergeGuildReq { Guildid = beta, IsAgree = true, Announce = "Join us" });
        Assert.Equal(0, (await Next(alice, GuildService.MergeAsk, InviteMergeGuildRes.Parser)).Result);
        await alice.SendGameAsync(GuildService.MergeAsk, new InviteMergeGuildReq { Guildid = beta, IsAgree = true });
        Assert.Equal(ErrorCodes.MergeRepeat, (await Next(alice, GuildService.MergeAsk, InviteMergeGuildRes.Parser)).Result);
        await bob.SendGameAsync(GuildService.MergePull, new PullMergeGuildDataReq());
        Assert.Equal(astra, Assert.Single((await Next(bob, GuildService.MergePull, PullMergeGuildDataRes.Parser)).RecvInvite).Guid);
        await carol.SendGameAsync(GuildService.MergeOpe, new MergeGuildOpeReq { IsAgree = true, Guildid = astra });
        Assert.Equal(ErrorCodes.GuildPermission, (await Next(carol, GuildService.MergeOpe, MergeGuildOpeRes.Parser)).Result);
        await bob.SendGameAsync(GuildService.MergeOpe, new MergeGuildOpeReq { IsAgree = true, Guildid = astra });
        Assert.Equal(0, (await Next(bob, GuildService.MergeOpe, MergeGuildOpeRes.Parser)).Result);
        var pending = await Next(alice, GuildService.BaseChange, GuildBaseInfoChangeNtf.Parser,
            m => m.Tp == GuildBaseInfoChangeNtf.Types.ChangeType.ChangeBase && m.Base.PendingMergeGuildId == beta);
        Assert.Equal("Astra", pending.Base.Name);

        // The merge view from Beta's side; Beta brings Bob and Carol, Dave stays behind.
        await bob.SendGameAsync(GuildService.MergeDetail, new MergeGuildDetailDataReq());
        var detail = await Next(bob, GuildService.MergeDetail, MergeGuildDetailDataRes.Parser);
        Assert.Equal((0, beta, astra, "Join us"), (detail.Result, detail.TargetGuildData.Guid, detail.InvitedGuildData.Guid, detail.Announce));
        Assert.Equal(3, detail.Memberids.Count);
        Assert.InRange(detail.LastMergeTime - DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 1, 2 * 86_400);
        await bob.SendGameAsync(GuildService.MergeMembers, new SaveMergeMemberReq { Guildid = astra, Memberids = { bobId, carolId } });
        Assert.Equal(0, (await Next(bob, GuildService.MergeMembers, SaveMergeMemberRes.Parser)).Result);

        // 05:00 comes.
        var store = _app.Services.GetRequiredService<GuildStore>();
        foreach (long id in new[] { astra, beta })
        {
            var g = store.Get(id)!;
            g.MergeAtSec = 1;
            Assert.True(store.Save(g));
        }
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() => _app.Services.GetRequiredService<GuildService>().ReconcileAsync());
        Assert.Null(store.Get(beta));
        var merged = store.Get(astra)!;
        Assert.Equal((20, 20), (merged.Members[bobId].Official, merged.Members[carolId].Official));
        Assert.False(merged.Members.ContainsKey(daveId));
        Assert.Equal((0L, false), (merged.PendingMergeGuildId, merged.MergeAbsorbs));
        Assert.True(merged.MergeCooldownSec > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 6 * 86_400);
        Assert.Equal(astra, store.ForRole(bobId)!.Id);
        Assert.Null(store.ForRole(daveId));
        Assert.Equal(astra, (await Next(carol, GuildService.Full, GuildFullInfoNtf.Parser, m => m.Guid == astra)).Guid);
        Assert.Equal(0, (await Next(dave, GuildService.Full, GuildFullInfoNtf.Parser, m => m.Guid == 0)).Guid);

        var roles = _app.Services.GetRequiredService<RoleStore>();
        MailService.Mailbox Box(long id) => System.Text.Json.JsonSerializer.Deserialize<MailService.Mailbox>(roles.LoadMailbox(id) ?? "{}"u8.ToArray())!;
        long aliceId = (long)alice.Self!.Uuid;
        for (int i = 0; i < 100 && !Box(aliceId).Letters.Any(l => l.TemplateId == 1121); i++) await Task.Delay(20);
        Assert.Contains(Box(aliceId).Letters, l => l.TemplateId == 1121);

        // Astra is cooling down.
        await alice.SendGameAsync(GuildService.MergeAsk, new InviteMergeGuildReq { Guildid = beta, IsAgree = true });
        Assert.NotEqual(0, (await Next(alice, GuildService.MergeAsk, InviteMergeGuildRes.Parser)).Result);
    }
}
