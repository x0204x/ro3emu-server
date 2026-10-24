using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class GuildTests
{
    [ClientDataFact]
    public async Task GroupMailReachesEveryMemberWithCooldownAndDailyCount()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Level("alice");
        await Level("bob");
        long guildId = await CreateGuild(alice, "Astra");
        long aliceId = (long)alice.Self!.Uuid, bobId = (long)bob.Self!.Uuid;
        await bob.SendGameAsync(GuildService.Apply, new GuildEnterApplyReq { GuildIds = { guildId } });
        Assert.Equal(0, Assert.Single((await Next(bob, GuildService.Apply, GuildEnterApplyResp.Parser)).ApplyResult).Result);
        await alice.SendGameAsync(GuildService.ApplyOpe, new GuildEnterApplyOpeReq { Agree = true, PlayerIds = { bobId } });
        Assert.Equal(0, (await Next(alice, GuildService.ApplyOpe, GuildEnterApplyOpeResp.Parser)).Result);
        static GuildBroadEmailReq Letter(string content) => new() { Req = new SendGuildMailReq { Title = "Astra", Content = content } };

        // An ordinary member lacks GuildPermissionConfig 6 SendMail; the leader's empty text is 10026.
        await bob.SendGameAsync(GuildService.BroadEmail, Letter("hello"));
        Assert.Equal(ErrorCodes.GuildPermission, (await Next(bob, GuildService.BroadEmail, GuildBroadEmailResp.Parser)).Result);
        await alice.SendGameAsync(GuildService.BroadEmail, Letter(" "));
        Assert.Equal(ErrorCodes.GuildMailContent, (await Next(alice, GuildService.BroadEmail, GuildBroadEmailResp.Parser)).Result);
        await alice.SendGameAsync(GuildService.BroadEmail, Letter(new string('x', 81)));
        Assert.Equal(ErrorCodes.GuildTextLength, (await Next(alice, GuildService.BroadEmail, GuildBroadEmailResp.Parser)).Result);

        long before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await alice.SendGameAsync(GuildService.BroadEmail, Letter("Raid at eight"));
        var sent = await Next(alice, GuildService.BroadEmail, GuildBroadEmailResp.Parser);
        Assert.Equal(0, sent.Result);
        Assert.Equal(1, sent.BroadEmailLimit);
        Assert.InRange(sent.NextBroadEmailTime, before + 3_600_000 - 30_000, before + 3_600_000 + 30_000);
        var change = await Next(bob, GuildService.BaseChange, GuildBaseInfoChangeNtf.Parser,
            m => m.Tp == GuildBaseInfoChangeNtf.Types.ChangeType.ChangeBroadEmail);
        Assert.Equal(sent.NextBroadEmailTime, change.NextBroadEmailTime);
        Assert.Equal(1, change.BroadEmailLimit);

        var roles = _app.Services.GetRequiredService<RoleStore>();
        MailService.Mailbox Box(long id) => System.Text.Json.JsonSerializer.Deserialize<MailService.Mailbox>(roles.LoadMailbox(id) ?? "{}"u8.ToArray())!;
        for (int i = 0; i < 120 && (Box(aliceId).Letters.Count == 0 || Box(bobId).Letters.Count == 0); i++) await Task.Delay(25);
        foreach (long role in new[] { aliceId, bobId })
        {
            var letter = Assert.Single(Box(role).Letters);
            Assert.Equal((3, "Astra", "Raid at eight", "Alice", aliceId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (letter.Category, letter.Title, letter.Content, letter.Sender, letter.SenderId));
        }

        // GuildMassEmailCD (161 on cooldown), then GuildMassEmailNum letters a day (10025); the full sync carries both.
        await alice.SendGameAsync(GuildService.BroadEmail, Letter("again"));
        var cooling = await Next(alice, GuildService.BroadEmail, GuildBroadEmailResp.Parser);
        Assert.Equal((ErrorCodes.Cooldown, sent.NextBroadEmailTime, 1L), (cooling.Result, cooling.NextBroadEmailTime, cooling.BroadEmailLimit));
        var g = _app.Services.GetRequiredService<GuildStore>().Get(guildId)!;
        g.NextBroadEmailMs = 0;
        g.BroadEmailCount = 10;
        Assert.True(_app.Services.GetRequiredService<GuildStore>().Save(g));
        await alice.SendGameAsync(GuildService.BroadEmail, Letter("again"));
        Assert.Equal(ErrorCodes.GuildMailDaily, (await Next(alice, GuildService.BroadEmail, GuildBroadEmailResp.Parser)).Result);
        Assert.Single(Box(bobId).Letters);
    }
}
