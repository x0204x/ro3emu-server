using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class GuildTests
{
    [ClientDataFact]
    public async Task RallyCostsGuildFundsAndCallsOnlineMembers()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Level("alice");
        await Level("bob");
        long guildId = await CreateGuild(alice, "Astra");
        long bobId = (long)bob.Self!.Uuid;
        await bob.SendGameAsync(GuildService.Apply, new GuildEnterApplyReq { GuildIds = { guildId } });
        Assert.Equal(0, Assert.Single((await Next(bob, GuildService.Apply, GuildEnterApplyResp.Parser)).ApplyResult).Result);
        await alice.SendGameAsync(GuildService.ApplyOpe, new GuildEnterApplyOpeReq { Agree = true, PlayerIds = { bobId } });
        Assert.Equal(0, (await Next(alice, GuildService.ApplyOpe, GuildEnterApplyOpeResp.Parser)).Result);
        var guilds = _app.Services.GetRequiredService<GuildStore>();
        // Guild Funds (104) from an award move into the guild.
        using (var client = new HttpClient())
            (await client.PostAsync($"{_http}/ro3emu/gm?account=alice", new StringContent("!item 104 20"))).EnsureSuccessStatusCode();
        for (int i = 0; i < 250 && guilds.Get(guildId)!.Money < 20; i++) await Task.Delay(20);
        long funds = guilds.Get(guildId)!.Money;
        Assert.True(funds >= 20);

        await alice.SendGameAsync(GuildService.Convene, new GuildConveneReq());
        Assert.Equal(0, (await Next(alice, GuildService.Convene, GuildConveneRes.Parser)).Result);
        Assert.Equal(funds - 5, guilds.Get(guildId)!.Money);
        var call = await Next(bob, GuildService.ConveneNotify, GuildConveneNtf.Parser);
        Assert.Equal((long)alice.Self!.Uuid, call.PlayerId);
        await alice.SendGameAsync(GuildService.Convene, new GuildConveneReq());
        Assert.Equal(GuildService.ErrRallyCooldown, (await Next(alice, GuildService.Convene, GuildConveneRes.Parser)).Result);

        await bob.SendGameAsync(GuildService.ConfirmConvene, new ConfirmConveneReq { Token = call.Token });
        Assert.Equal(0, (await Next(bob, GuildService.ConfirmConvene, ConfirmConveneRes.Parser)).Result);
        await alice.SendGameAsync(GuildService.ConfirmConvene, new ConfirmConveneReq { Token = call.Token });
        Assert.Equal(GuildService.ErrInvitationInvalid, (await Next(alice, GuildService.ConfirmConvene, ConfirmConveneRes.Parser)).Result);
    }
}
