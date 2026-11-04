using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class GuildTests
{
    private const int Emperium = 1250714;

    private async Task GiveEmperium(string account)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={account}", Operator.Command($"!item {Emperium} 1"))).EnsureSuccessStatusCode();
    }

    private MailService.Letter? FoundingLetter(Ro3Emu.Bot.BotClient bot, int template) =>
        _app.Services.GetRequiredService<Ro3Emu.Server.Accounts.RoleStore>().LoadMailbox((long)bot.Self!.Uuid) is { } bytes
            ? System.Text.Json.JsonSerializer.Deserialize<MailService.Mailbox>(bytes)!.Letters.FirstOrDefault(m => m.TemplateId == template)
            : null;

    // The create widget shows GuildCreateCostShow [Emperium, 1] and the client never checks it: the server refuses.
    [ClientDataFact]
    public async Task FoundingCostsAnEmperiumAndACancelledOneSendsItBack()
    {
        _app.Services.GetRequiredService<IOptions<ServerOptions>>().Value.GuildFounding = "responders";
        await using var founder = await InWorld("Founder");
        await Level("founder");
        await founder.SendGameAsync(GuildService.Create, Create("Helpers"));
        Assert.Equal(ErrorCodes.ItemsShort, (await Next(founder, GuildService.Create, GuildCreateResp.Parser)).Result);

        await GiveEmperium("founder");
        await founder.SendGameAsync(GuildService.Create, Create("Helpers"));
        Assert.Equal(0, (await Next(founder, GuildService.Create, GuildCreateResp.Parser)).Result);
        var pre = await Next(founder, GuildService.Full, GuildFullInfoNtf.Parser, x => x.Guid > 0);
        Assert.Equal(GuildStatus.PreCreate, pre.Status);
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(x => x.Role?.Id == (long)founder.Self!.Uuid);
        Assert.Equal(0, _app.Services.GetRequiredService<CharacterService>().CountOf(session, Emperium));

        await founder.SendGameAsync(GuildService.CancelCreate, new GuildCancelCreateReq());
        Assert.Equal(0, (await Next(founder, GuildService.CancelCreate, GuildCancleCreateResp.Parser)).Result);
        Assert.Null(_app.Services.GetRequiredService<Ro3Emu.Server.Accounts.GuildStore>().Get(pre.Guid));
        var letter = FoundingLetter(founder, 1018);
        Assert.NotNull(letter);
        var returned = Assert.Single(letter!.Attachments);
        Assert.Equal((Emperium, 1), (returned.Entry, returned.Count));
    }

    [ClientDataFact]
    public async Task AFoundingOutOfTimeSendsTheEmperiumBackAndAFormedOneKeepsIt()
    {
        var options = _app.Services.GetRequiredService<IOptions<ServerOptions>>().Value;
        options.GuildFounding = "responders";
        await using var founder = await InWorld("Founder");
        await using var late = await InWorld("Late");
        await using var anna = await InWorld("Anna");
        await using var ben = await InWorld("Ben");
        foreach (string name in new[] { "founder", "late", "anna", "ben" }) await Level(name);
        await GiveEmperium("founder");
        await GiveEmperium("late");
        var store = _app.Services.GetRequiredService<Ro3Emu.Server.Accounts.GuildStore>();

        await late.SendGameAsync(GuildService.Create, Create("Toolate"));
        Assert.Equal(0, (await Next(late, GuildService.Create, GuildCreateResp.Parser)).Result);
        long lateId = (await Next(late, GuildService.Full, GuildFullInfoNtf.Parser, x => x.Guid > 0)).Guid;
        _clock.Freeze();
        _clock.Advance(TimeSpan.FromSeconds(86401));
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() => _app.Services.GetRequiredService<GuildService>().ReconcileAsync());
        Assert.Null(store.Get(lateId));
        Assert.Equal(Emperium, Assert.Single(FoundingLetter(late, 1018)!.Attachments).Entry);

        var create = Create("Helpers");
        create.AutoCreateHelper = true; // No approval: two responses form it.
        await founder.SendGameAsync(GuildService.Create, create);
        Assert.Equal(0, (await Next(founder, GuildService.Create, GuildCreateResp.Parser)).Result);
        long id = (await Next(founder, GuildService.Full, GuildFullInfoNtf.Parser, x => x.Guid > 0)).Guid;
        foreach (var helper in new[] { anna, ben })
        {
            await helper.SendGameAsync(GuildService.HelperOpe, new GuildCreateHelperOpeReq { GuildIds = { id } });
            Assert.True(Assert.Single((await Next(helper, GuildService.HelperOpe, GuildCreateHelperOpeResp.Parser)).HelperResult).Success);
        }
        var formed = store.Get(id)!;
        Assert.Equal((int)GuildStatus.Normal, formed.Status);
        Assert.Equal(0, formed.FoundingItem);
        Assert.NotNull(FoundingLetter(founder, 1017));
        Assert.Null(FoundingLetter(founder, 1018));
    }

    [ClientDataFact]
    public async Task AnInstantHostIgnoresTheToggle()
    {
        await using var founder = await InWorld("Founder");
        await Level("founder");
        var create = Create("Helpers");
        create.AutoCreateHelper = true;
        await founder.SendGameAsync(GuildService.Create, create);
        Assert.Equal(0, (await Next(founder, GuildService.Create, GuildCreateResp.Parser)).Result);
        Assert.Equal(GuildStatus.Normal, (await Next(founder, GuildService.Full, GuildFullInfoNtf.Parser, x => x.Guid > 0)).Status);
    }

    [ClientDataFact]
    public async Task FounderApprovesResponsesAndInvitesUntilTheGuildForms()
    {
        _app.Services.GetRequiredService<IOptions<ServerOptions>>().Value.GuildFounding = "responders";
        await using var founder = await InWorld("Founder");
        await using var anna = await InWorld("Anna");
        await using var ben = await InWorld("Ben");
        await using var cleo = await InWorld("Cleo");
        foreach (string name in new[] { "founder", "anna", "ben", "cleo" }) await Level(name);
        await GiveEmperium("founder");
        long annaId = (long)anna.Self!.Uuid, benId = (long)ben.Self!.Uuid;

        // Without the toggle every guild is still founded through responders, each response needing approval.
        await founder.SendGameAsync(GuildService.Create, Create("Helpers"));
        Assert.Equal(0, (await Next(founder, GuildService.Create, GuildCreateResp.Parser)).Result);
        var pre = await Next(founder, GuildService.Full, GuildFullInfoNtf.Parser, x => x.Guid > 0);
        Assert.Equal(GuildStatus.PreCreate, pre.Status);
        Assert.False(pre.AutoCreateHelper);

        async Task<GuildCreateHelperOpeResp> Respond(Ro3Emu.Bot.BotClient bot)
        {
            await bot.SendGameAsync(GuildService.HelperOpe, new GuildCreateHelperOpeReq { GuildIds = { pre.Guid } });
            return await Next(bot, GuildService.HelperOpe, GuildCreateHelperOpeResp.Parser);
        }
        async Task<CreateHelperApplyOpeRes> Decide(bool agree, params long[] ids)
        {
            var req = new CreateHelperApplyOpeReq { Agree = agree, IsBatch = ids.Length == 0 };
            req.PlayerIDList.AddRange(ids);
            await founder.SendGameAsync(GuildService.HelperApplyOpe, req);
            return await Next(founder, GuildService.HelperApplyOpe, CreateHelperApplyOpeRes.Parser);
        }

        // Anna responds: queued, the founder's red dot counts it, the list shows her; refused, she responds again and
        // Accept All takes her.
        Assert.True(Assert.Single((await Respond(anna)).HelperResult).Success);
        Assert.Equal(1, (await Next(founder, GuildService.RedDot, GuildRedotNotify.Parser)).Count);
        await founder.SendGameAsync(GuildService.PullHelperApply, new PullCreateHelperApplyReq());
        Assert.Equal(annaId, Assert.Single((await Next(founder, GuildService.PullHelperApply,
            PullCreateHelperApplyRes.Parser)).CreateHelperApplyData).PlayerID);
        Assert.Single(_app.Services.GetRequiredService<Ro3Emu.Server.Accounts.GuildStore>().Get(pre.Guid)!.Members);
        Assert.Equal([annaId], (await Decide(false, annaId)).PlayerIDList);
        Assert.True(Assert.Single((await Respond(anna)).HelperResult).Success);
        var approved = await Decide(true);
        Assert.Equal(0, approved.Result);
        Assert.Equal([annaId], approved.PlayerIDList);
        var joined = await Next(anna, GuildService.Full, GuildFullInfoNtf.Parser, x => x.Guid == pre.Guid);
        // createHelperInfo: responders, then the founder last.
        Assert.Equal([annaId, (long)founder.Self!.Uuid], joined.CreateHelperInfo.Select(h => h.PlayerID));

        // Ben is invited by the founder (2 of 3 so far) and joins without approval, which founds the guild; a second
        // invite inside the window is refused, the used token is gone.
        await founder.SendGameAsync(GuildService.InviteHelperToken, new InviteCreateHelperTokenReq { InvitePlayerId = benId });
        var token = await Next(founder, GuildService.InviteHelperToken, InviteCreateHelperTokenRes.Parser);
        Assert.Equal(0, token.Result);
        var notice = await Next(ben, GuildService.InviteHelperNotify, InviteCreateHelperNotify.Parser);
        Assert.Equal((pre.Guid, token.Token, 2), (notice.GuildId, notice.Token, notice.HelperCount));
        await founder.SendGameAsync(GuildService.InviteHelperToken, new InviteCreateHelperTokenReq { InvitePlayerId = benId });
        Assert.Equal(GuildService.ErrInviteAgainLater,
            (await Next(founder, GuildService.InviteHelperToken, InviteCreateHelperTokenRes.Parser)).Result);
        await ben.SendGameAsync(GuildService.InviteHelperOpe, new InviteCreateHelperOpeReq { Token = token.Token, Agree = true });
        Assert.Equal(0, (await Next(ben, GuildService.InviteHelperOpe, InviteCreateHelperOpeRes.Parser)).Result);
        await ben.SendGameAsync(GuildService.InviteHelperOpe, new InviteCreateHelperOpeReq { Token = token.Token, Agree = true });
        Assert.Equal(GuildService.ErrInvitationInvalid, (await Next(ben, GuildService.InviteHelperOpe, InviteCreateHelperOpeRes.Parser)).Result);
        var formed = await Next(founder, GuildService.Full, GuildFullInfoNtf.Parser,
            x => x.Guid == pre.Guid && x.Status == GuildStatus.Normal);
        Assert.Equal(3, formed.MemberInfo.Count);
        Assert.False(Assert.Single((await Respond(cleo)).HelperResult).Success);

        var journal = await Journal(founder, 0, 20);
        var founded = Assert.Single(journal.Datas, d => d.Data.EventCfgID == 13).Data;
        Assert.Equal(["Founder", "Anna", "Ben"], founded.Param.Param.Select(p => p.StrParam));
    }
}
