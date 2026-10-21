using System.Text.Json;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.LoadTest;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class GuildTests
{
    [ClientDataFact]
    public async Task HeldOrdinaryJoinDefersTimerAndGmPublishesMembershipTogether()
    {
        await using var founder = await InWorld("HoldFounder");
        await using var recruit = await InWorld("HoldRecruit");
        await using var unrelated = await InWorld("HoldOutsider");
        // InWorld ends at SelfInfoEndFrame, before the remaining SceneLoaded guild hooks.
        foreach (var bot in new[] { founder, recruit, unrelated })
            await Next(bot, GuildService.SceneGuildId, SwitchSceneSyncGuildId.Parser);
        await Level("holdfounder");
        long guildId = await CreateGuild(founder, "HeldOne");
        var store = _app.Services.GetRequiredService<GuildStore>();
        var guild = store.Get(guildId)!;
        guild.AutoAccept = true;
        Assert.True(store.Save(guild));
        var accounts = _app.Services.GetRequiredService<AccountStore>();
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var service = _app.Services.GetRequiredService<GuildService>();
        var roles = _app.Services.GetRequiredService<RoleStore>();
        long recruitId = (long)recruit.Self!.Uuid;
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == recruitId);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int held = 0;
        accounts.BeforeOwnedCommitForTest = async label =>
        {
            if (label != "guild/ordinary-save" || Interlocked.Exchange(ref held, 1) != 0) return;
            reached.TrySetResult(); await release.Task;
        };
        Task? join = null, timer = null;
        Task<HttpResponseMessage>? gm = null;
        using var httpClient = new HttpClient();
        try
        {
            join = loop.InvokeAsync(() => service.ApplyAsync(session, new GuildEnterApplyReq { GuildIds = { guildId } }));
            await reached.Task.WaitAsync(Wait);
            Assert.Null(store.ForRole(recruitId));
            Assert.Equal(0, session.Guild!.GuildId);
            Assert.Equal(0, DBRoleGuildInfo.Parser.ParseFrom(roles.LoadGuildInfo(recruitId)!).GuildId);
            timer = loop.InvokeAsync(service.ReconcileAsync);
            gm = httpClient.PostAsync(_http + "/ro3emu/gm?account=holdrecruit", new StringContent("!item 1 17"));
            await unrelated.SendGameBytesAsync(MsgId.SaveSetting, [0x0a, 1, 5, 0x12, 4, 0x2a, 2, 16, 1]);
            Assert.Equal(0, (await Next(unrelated, MsgId.SaveSetting, SaveSettingResp.Parser)).Result);
            Assert.False(timer.IsCompleted);
            Assert.False(gm.IsCompleted);
            Assert.Null(store.ForRole(recruitId));
        }
        finally { release.TrySetResult(); accounts.BeforeOwnedCommitForTest = null; }
        await join!.WaitAsync(Wait);
        await timer!.WaitAsync(Wait);
        (await gm!.WaitAsync(Wait)).EnsureSuccessStatusCode();
        Assert.Equal(guildId, store.ForRole(recruitId)!.Id);
        Assert.Equal(guildId, session.Guild!.GuildId);
        Assert.Equal(guildId, DBRoleGuildInfo.Parser.ParseFrom(roles.LoadGuildInfo(recruitId)!).GuildId);
        Assert.Equal(1017, roles.LoadProgress(recruitId).Currency[1]);
        Assert.Equal(2, store.Get(guildId)!.Members.Count);
    }

    [ClientDataFact]
    public async Task OrdinaryJoinLastProjectionFailureRollsBackRosterAndIndex()
    {
        await using var founder = await InWorld("FailFounder");
        await using var recruit = await InWorld("FailRecruit");
        await Level("failfounder");
        long guildId = await CreateGuild(founder, "Atomics");
        var store = _app.Services.GetRequiredService<GuildStore>();
        var guild = store.Get(guildId)!;
        guild.AutoAccept = true;
        Assert.True(store.Save(guild));
        long id = (long)recruit.Self!.Uuid;
        var accounts = _app.Services.GetRequiredService<AccountStore>();
        var roles = _app.Services.GetRequiredService<RoleStore>();
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == id);
        var service = _app.Services.GetRequiredService<GuildService>();
        FixtureSql.Execute(accounts, $"CREATE TRIGGER fail_guild_view BEFORE UPDATE OF guild_info ON role WHEN NEW.id={id} BEGIN SELECT RAISE(ABORT,'guild view failure'); END");
        try
        {
            await Assert.ThrowsAsync<SqliteException>(() => loop.InvokeAsync(() => service.ApplyAsync(session,
                new GuildEnterApplyReq { GuildIds = { guildId } })));
            Assert.Null(store.ForRole(id));
            Assert.Single(store.Get(guildId)!.Members);
            Assert.Empty(store.Get(guildId)!.Applications);
            Assert.Equal(0, session.Guild!.GuildId);
            Assert.Equal(0, DBRoleGuildInfo.Parser.ParseFrom(roles.LoadGuildInfo(id)!).GuildId);
        }
        finally { FixtureSql.Execute(accounts, "DROP TRIGGER fail_guild_view"); }
        await recruit.SendGameAsync(GuildService.Apply, new GuildEnterApplyReq { GuildIds = { guildId } });
        Assert.Equal(0, Assert.Single((await Next(recruit, GuildService.Apply, GuildEnterApplyResp.Parser)).ApplyResult).Result);
        Assert.Equal(guildId, store.ForRole(id)!.Id);
        Assert.Equal(guildId, session.Guild!.GuildId);
    }

    [ClientDataTheory]
    [InlineData(0L)]
    [InlineData(10L)]
    public async Task DisbandMovesEveryTreasuryDenominationToDurableRecoveryOnce(long escrow)
    {
        await using var founder = await InWorld("RecoverGuild");
        await Level("recoverguild");
        long guildId = await CreateGuild(founder, "Recover");
        var store = _app.Services.GetRequiredService<GuildStore>();
        var guild = store.Get(guildId)!;
        guild.Money = 101; guild.Contribute = 202; guild.Materials = 303; guild.Reputation = 404; guild.AuctionMaterials = 505;
        Assert.True(store.Save(guild));
        if (escrow > 0)
        {
            var market = store.LoadMarket();
            market.Stalls.Add(new GuildStore.MarketStall
            {
                Id = "disband-held", GuildId = guildId,
                UnitPrice = 500, TotalCount = escrow, RemainCount = escrow, ExpiresMs = long.MaxValue
            });
            Assert.True(store.SaveMarket(market, []));
        }
        var catalog = EconomyCatalog.Load(Path.Combine(RepoRoot(), "data"));
        var opening = S3EconomyReader.Read(_dataDir, catalog);
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == (long)founder.Self!.Uuid);
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() => _app.Services.GetRequiredService<GuildService>().LeaveAsync(session));
        Assert.Equal(0, (await Next(founder, GuildService.Leave, GuildMemberLeaveResp.Parser)).Result);
        var closing = S3EconomyReader.Read(_dataDir, catalog);
        Assert.Null(store.Get(guildId));
        Assert.True(store.HasRecovery(guildId));
        Assert.Null(store.ForRole((long)founder.Self!.Uuid));
        var ledger = S3EconomyReader.ReadLedger(_dataDir, catalog, opening, closing)!;
        Assert.True(ledger.CompleteHistory, string.Join(";", ledger.Issues.Select(x => x.Detail)));
        Assert.Single(ledger.Receipts);
        Assert.Equal(escrow == 0 ? 10 : 12, ledger.Legs.Count);
        Assert.Empty(store.LoadMarket().Stalls);
        foreach (long asset in Enumerable.Range(1, 5).Select(x => (long)x))
        {
            var legs = ledger.Legs.Where(x => x.Asset == new EconomyAsset(EconomyAssetKind.GuildResource, asset)).ToArray();
            Assert.Equal(asset == 3 && escrow > 0 ? 4 : 2, legs.Length);
            Assert.Contains(legs, x => x.Location.Compartment == EconomyCompartment.GuildTreasury && x.Delta == -101 * asset);
            Assert.Contains(legs, x => x.Location.Compartment == EconomyCompartment.Recovery && x.Delta == 101 * asset);
        }
        if (escrow > 0) Assert.Contains(ledger.Legs, x => x.Asset.Id == 3 && x.Location.Compartment == EconomyCompartment.GuildMarket && x.Delta == -escrow);
        var check = EconomyExactnessChecker.Check(opening, closing, ledger);
        Assert.All(check.Assets, x => { Assert.Equal("0", x.Residual); Assert.Equal("0", x.LegResidual); });
        Assert.All(check.Locations, x => Assert.Equal("0", x.Residual));
        await founder.SendGameAsync(GuildService.Leave, new GuildMemberLeaveReq());
        Assert.Equal(ErrorCodes.NotInGuild, (await Next(founder, GuildService.Leave, GuildMemberLeaveResp.Parser)).Result);
        Assert.Equal(closing.Cut.CommitSequence, S3EconomyReader.Read(_dataDir, catalog).Cut.CommitSequence);
    }
}
