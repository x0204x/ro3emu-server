using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class EmblemTests
{
    [ClientDataFact]
    public async Task HeldSetupKeepsPlanAndItemUnpublishedWhileAnotherRoleCanProgress()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        // Both roles' post-scene-load publication reserves Realm, and a session's later messages queue behind it.
        // Let it finish before holding a commit, or the other role's request waits for the held one (C/D 15).
        for (var until = DateTime.UtcNow.AddSeconds(10); _app.Services.GetRequiredService<SessionRegistry>().All().Any(x => x.Role != null && !x.GuildOnlinePublished);)
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("scene online publication did not finish");
            await Task.Delay(10);
        }
        await Gm("alice", "!quest done 10103040");
        await Gm("bob", "!quest done 10103040");
        await Gm("alice", "!item 1830101");
        long guid = Granted(await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
            m => m.PackInfo.ContainsKey(5)), 1830101).ItemGuid;
        var sessions = _app.Services.GetRequiredService<SessionRegistry>().All();
        var owner = sessions.Single(s => s.Account == "alice");
        var other = sessions.Single(s => s.Account == "bob");
        // The game socket handles requests in order. This no-op reply proves Bob's earlier SceneLoaded
        // and its after-loaded hooks finished before Alice holds a conflicting owner reservation.
        await bob.SendGameAsync(EmblemService.SwitchPlan, new EmbleSwitchPlanReq { TargetIndex = 0 });
        Assert.Equal(0, (await Next(bob, EmblemService.SwitchPlan, EmbleSwitchPlanResp.Parser)).Result);
        Assert.Null(other.Handoff);
        var roles = _app.Services.GetRequiredService<RoleStore>();
        var store = _app.Services.GetRequiredService<ItemStore>();
        var accounts = _app.Services.GetRequiredService<AccountStore>();
        byte[] before = roles.LoadEmblems(owner.Role!.Id)!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        accounts.BeforeOwnedCommitForTest = label =>
        {
            if (label != "economy/cd13b/emblem-setup") return Task.CompletedTask;
            entered.TrySetResult();
            return release.Task;
        };
        try
        {
            await alice.SendGameAsync(EmblemService.SetUp, new SetUpEmblemReq { EmblemPos = 1, EmblemGuid = guid });
            await entered.Task.WaitAsync(Wait);
            Assert.Empty(owner.Emblems.Plans[owner.Emblems.CurrentPlan].Slots);
            Assert.Equal(0, owner.Items.Single(i => i.Guid == guid).Attrs!.GetValueOrDefault(10001));
            Assert.Equal(before, roles.LoadEmblems(owner.Role.Id));
            Assert.Equal(0, store.Load(owner.Role.Id).Single(i => i.Guid == guid).Attrs!.GetValueOrDefault(10001));
            // GM intentionally reserves Realm during the migration. Exercise an independent
            // converted command here; the separate GM tests require it to defer.
            await bob.SendGameAsync(EmblemService.RenamePlan,
                new EmblemPlanChangeNameReq { TargetIndex = 1, Name = "Free" });
            Assert.Equal(0, (await Next(bob, EmblemService.RenamePlan, EmblemPlanChangeNameResp.Parser)).Result);
            Assert.Equal("Free", other.Emblems.Plans[1].Name);
        }
        finally { accounts.BeforeOwnedCommitForTest = null; release.TrySetResult(); }
        Assert.Equal(0, (await Next(alice, EmblemService.SetUp, SetUpEmblemResp.Parser)).Result);
        Assert.Equal(guid, owner.Emblems.Plans[owner.Emblems.CurrentPlan].Slots[1]);
        Assert.Equal(2, store.Load(owner.Role.Id).Single(i => i.Guid == guid).Attrs![10001]);
    }

    [ClientDataFact]
    public async Task FailedSlotBlobWriteRollsBackBothDirectionsAndCanRetry()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!quest done 10103040");
        await Gm("alice", "!item 1830101");
        long guid = Granted(await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
            m => m.PackInfo.ContainsKey(5)), 1830101).ItemGuid;
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == "alice");
        var accounts = _app.Services.GetRequiredService<AccountStore>();
        var roles = _app.Services.GetRequiredService<RoleStore>();
        var store = _app.Services.GetRequiredService<ItemStore>();
        var loop = _app.Services.GetRequiredService<GameLoop>();
        byte[] empty = roles.LoadEmblems(session.Role!.Id)!;
        FixtureSql.Execute(accounts,
            "CREATE TRIGGER reject_emblem_slot BEFORE UPDATE OF emblems ON role BEGIN SELECT RAISE(ABORT,'slot blob'); END");
        try
        {
            await Assert.ThrowsAsync<SqliteException>(() => loop.InvokeAsync(() =>
                _app.Services.GetRequiredService<EmblemService>().SetUpAsync(session,
                    new SetUpEmblemReq { EmblemPos = 1, EmblemGuid = guid })));
            Assert.Empty(session.Emblems.Plans[0].Slots);
            Assert.Equal(empty, roles.LoadEmblems(session.Role.Id));
            Assert.Equal(0, store.Load(session.Role.Id).Single(i => i.Guid == guid).Attrs!.GetValueOrDefault(10001));
        }
        finally { FixtureSql.Execute(accounts, "DROP TRIGGER reject_emblem_slot"); }
        await alice.SendGameAsync(EmblemService.SetUp, new SetUpEmblemReq { EmblemPos = 1, EmblemGuid = guid });
        Assert.Equal(0, (await Next(alice, EmblemService.SetUp, SetUpEmblemResp.Parser)).Result);
        byte[] deployed = roles.LoadEmblems(session.Role.Id)!;
        FixtureSql.Execute(accounts,
            "CREATE TRIGGER reject_emblem_slot BEFORE UPDATE OF emblems ON role BEGIN SELECT RAISE(ABORT,'slot blob'); END");
        try
        {
            await Assert.ThrowsAsync<SqliteException>(() => loop.InvokeAsync(() =>
                _app.Services.GetRequiredService<EmblemService>().SetDownAsync(session,
                    new SetDownEmblemReq { EmblemPos = 1 })));
            Assert.Equal(guid, session.Emblems.Plans[0].Slots[1]);
            Assert.Equal(deployed, roles.LoadEmblems(session.Role.Id));
            Assert.Equal(2, store.Load(session.Role.Id).Single(i => i.Guid == guid).Attrs![10001]);
        }
        finally { FixtureSql.Execute(accounts, "DROP TRIGGER reject_emblem_slot"); }
        await alice.SendGameAsync(EmblemService.SetDown, new SetDownEmblemReq { EmblemPos = 1 });
        Assert.Equal(0, (await Next(alice, EmblemService.SetDown, SetDownEmblemResp.Parser)).Result);
        Assert.Empty(session.Emblems.Plans[0].Slots);
        Assert.Equal(0, store.Load(session.Role.Id).Single(i => i.Guid == guid).Attrs![10001]);
    }

    [ClientDataFact]
    public async Task UpgradeLedgerConsumesMaterialAndFeedWithoutCountingPlanReferenceAsAnotherItem()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!quest done 10103040");
        await Gm("alice", "!item 1830101 2");
        await Gm("alice", "!item 1257001");
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == "alice");
        var guids = session.Items.Where(i => i.Entry == 1830101).Select(i => i.Guid).Order().ToArray();
        long target = guids[0], feed = guids[1];
        var accounts = _app.Services.GetRequiredService<AccountStore>();
        var catalog = EconomyCatalog.Load(Path.Combine(RepoRoot(), "data"));
        var beforeSetup = EconomySnapshotReader.Read(accounts.DatabasePath, catalog, "auto");
        await alice.SendGameAsync(EmblemService.SetUp, new SetUpEmblemReq { EmblemPos = 1, EmblemGuid = target });
        Assert.Equal(0, (await Next(alice, EmblemService.SetUp, SetUpEmblemResp.Parser)).Result);
        var opening = EconomySnapshotReader.Read(accounts.DatabasePath, catalog, "auto");
        var setupLedger = EconomyLedgerReader.Read(accounts.DatabasePath, catalog, "auto",
            beforeSetup.Cut.CommitSequence!.Value, opening.Cut.CommitSequence!.Value);
        Assert.True(setupLedger.CompleteHistory, string.Join("; ", setupLedger.Issues.Select(i => i.Detail)));
        Assert.DoesNotContain(setupLedger.Legs, leg => leg.Kind is EconomyLegKind.Source or EconomyLegKind.Sink);
        Assert.Equal(beforeSetup.Holdings.Where(h => h.Asset.Id == 1830101).Sum(h => h.Quantity),
            opening.Holdings.Where(h => h.Asset.Id == 1830101).Sum(h => h.Quantity));
        var request = new UpgradeEmblemReq { EmblemGuid = target, ConsumeEmblemGuids = { feed } };
        request.ConsumeItems[1257001] = 1;
        await alice.SendGameAsync(EmblemService.Upgrade, request);
        Assert.Equal(0, (await Next(alice, EmblemService.Upgrade, UpgradeEmblemResp.Parser)).Result);
        var closing = EconomySnapshotReader.Read(accounts.DatabasePath, catalog, "auto");
        var ledger = EconomyLedgerReader.Read(accounts.DatabasePath, catalog, "auto",
            opening.Cut.CommitSequence!.Value, closing.Cut.CommitSequence!.Value);
        Assert.True(ledger.CompleteHistory, string.Join("; ", ledger.Issues.Select(i => i.Detail)));
        var costs = ledger.Legs.Where(l => l.Reason == EconomyReason.EmblemCost).ToArray();
        Assert.Equal(2, costs.Length);
        Assert.Contains(costs, l => l.Asset.Id == 1830101 && l.Delta == -1
            && l.Location.Instance == feed.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains(costs, l => l.Asset.Id == 1257001 && l.Delta == -1);
        Assert.DoesNotContain(ledger.Legs, l => l.Asset.Id == 1830101 && l.Delta > 0);
        var check = EconomyExactnessChecker.Check(opening, closing, ledger);
        Assert.All(check.Assets, asset => Assert.Equal("0", asset.Residual));
        Assert.All(check.Locations, location => Assert.Equal("0", location.Residual));
        Assert.Equal(target, session.Emblems.Plans[0].Slots[1]);
        Assert.DoesNotContain(session.Items, i => i.Guid == feed);
    }
}
