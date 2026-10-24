using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class GuildTests
{
    [ClientDataFact]
    public async Task MembersImpeachALongOfflineMasterWhoBecomesAMember()
    {
        var alice = await InWorld("Alice");
        await Level("alice");
        long guildId = await CreateGuild(alice, "Astra");
        long aliceId = (long)alice.Self!.Uuid;
        var bots = new List<BotClient>();
        foreach (string name in new[] { "Bob", "Carol", "Dave" })
        {
            var bot = await InWorld(name);
            bots.Add(bot);
            await Level(name);
            await bot.SendGameAsync(GuildService.Apply, new GuildEnterApplyReq { GuildIds = { guildId } });
            Assert.Equal(0, Assert.Single((await Next(bot, GuildService.Apply, GuildEnterApplyResp.Parser)).ApplyResult).Result);
            await alice.SendGameAsync(GuildService.ApplyOpe, new GuildEnterApplyOpeReq { Agree = true, PlayerIds = { (long)bot.Self!.Uuid } });
            Assert.Equal(0, (await Next(alice, GuildService.ApplyOpe, GuildEnterApplyOpeResp.Parser)).Result);
        }
        var (bob, carol, dave) = (bots[0], bots[1], bots[2]);
        long bobId = (long)bob.Self!.Uuid;
        var store = _app.Services.GetRequiredService<GuildStore>();
        static GuildPeachReq Start() => new();

        // The master is online: no grounds (473). The founder herself may not (475).
        using (var client = new HttpClient())
            (await client.PostAsync($"{_http}/ro3emu/gm?account=bob", new StringContent("!item 3 30"))).EnsureSuccessStatusCode();
        var g = store.Get(guildId)!;
        foreach (var m in g.Members.Values.Where(m => m.RoleId != aliceId)) m.EnterTime -= 8L * 86_400_000;
        Assert.True(store.Save(g));
        await bob.SendGameAsync(GuildService.Peach, Start());
        Assert.Equal(ErrorCodes.ImpeachNoGrounds, (await Next(bob, GuildService.Peach, GuildPeachResp.Parser)).Result);
        await alice.SendGameAsync(GuildService.Peach, Start());
        Assert.Equal(ErrorCodes.ImpeachSelf, (await Next(alice, GuildService.Peach, GuildPeachResp.Parser)).Result);

        // Seven days offline (ActiveImpeachChairmanOffineTimeLimit).
        await alice.DisposeAsync();
        for (int i = 0; i < 100 && store.Get(guildId)!.Members[aliceId].OfflineTime < 0; i++) await Task.Delay(20);
        g = store.Get(guildId)!;
        g.Members[aliceId].OfflineTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 8 * 86_400;
        Assert.True(store.Save(g));

        // Three members online: ceil(50% of 3) = 2 responses, the initiator counting itself.
        await bob.SendGameAsync(GuildService.Peach, Start());
        Assert.Equal(0, (await Next(bob, GuildService.Peach, GuildPeachResp.Parser)).Result);
        var started = await Next(carol, GuildService.BaseChange, GuildBaseInfoChangeNtf.Parser,
            m => m.Tp == GuildBaseInfoChangeNtf.Types.ChangeType.ChangePeach && m.Peach.IsPeaching);
        Assert.Equal((bobId, 2L, true), (started.Peach.PeachOwnerID, started.Peach.NeedCount, started.Peach.CanPeach));
        Assert.Equal([bobId], started.Peach.PeachPlayerIDs);
        Assert.InRange(started.Peach.PeachEndTime - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 3_500_000, 3_600_000);
        await bob.SendGameAsync(GuildService.PeachApply, new GuildPeachApplyReq());
        Assert.Equal(ErrorCodes.ImpeachRepeat, (await Next(bob, GuildService.PeachApply, GuildPeachApplyResp.Parser)).Result);
        await bob.SendGameAsync(GuildService.Peach, Start());
        Assert.Equal(ErrorCodes.ImpeachInProgress, (await Next(bob, GuildService.Peach, GuildPeachResp.Parser)).Result);

        // Carol's response completes it: Bob leads, Alice is a member, and the next one waits ActiveImpeachCD hours.
        await carol.SendGameAsync(GuildService.PeachApply, new GuildPeachApplyReq());
        Assert.Equal(0, (await Next(carol, GuildService.PeachApply, GuildPeachApplyResp.Parser)).Result);
        var ended = await Next(dave, GuildService.BaseChange, GuildBaseInfoChangeNtf.Parser,
            m => m.Tp == GuildBaseInfoChangeNtf.Types.ChangeType.ChangePeach && !m.Peach.IsPeaching);
        Assert.InRange(ended.Peach.NextPeachTime - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 23 * 3_600_000L, 24 * 3_600_000L);
        g = store.Get(guildId)!;
        Assert.Equal((1, 20), (g.Members[bobId].Official, g.Members[aliceId].Official));
        Assert.Equal(0, _app.Services.GetRequiredService<CharacterService>().CountOf(
            _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == bobId), 3));
        await dave.SendGameAsync(GuildService.Peach, Start());
        Assert.Equal(ErrorCodes.ImpeachCooldown, (await Next(dave, GuildService.Peach, GuildPeachResp.Parser)).Result);

        var roles = _app.Services.GetRequiredService<RoleStore>();
        MailService.Mailbox Box(long id) => System.Text.Json.JsonSerializer.Deserialize<MailService.Mailbox>(roles.LoadMailbox(id) ?? "{}"u8.ToArray())!;
        for (int i = 0; i < 100 && !Box(aliceId).Letters.Any(l => l.TemplateId == 1001); i++) await Task.Delay(20);
        Assert.Contains(Box(aliceId).Letters, l => l.TemplateId == 1001);
        Assert.Contains(Box(bobId).Letters, l => l.TemplateId == 1005);
        Assert.Contains(Box((long)carol.Self!.Uuid).Letters, l => l.TemplateId == 1006);
        foreach (var bot in bots) await bot.DisposeAsync();
    }
}
