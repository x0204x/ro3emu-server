using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private AccountStore Accounts => _app.Services.GetRequiredService<AccountStore>();
    private RoleStore Roles => _app.Services.GetRequiredService<RoleStore>();

    private long CountReceipts(string kind)
    {
        using var db = Accounts.Open();
        using var read = db.CreateCommand();
        read.CommandText = "SELECT COUNT(*) FROM operation_receipt WHERE command_kind=$kind";
        read.Parameters.AddWithValue("$kind", kind);
        return (long)read.ExecuteScalar()!;
    }

    private async Task Ready(params GameSession[] players)
    {
        using var timeout = new CancellationTokenSource(Wait);
        while (!await Loop.InvokeAsync(() => Task.FromResult(players.All(s => s.GuildOnlinePublished))))
            await Task.Delay(10, timeout.Token);
        // The legacy SceneLoaded publication owns Realm until its final callbacks finish.
        await Loop.InvokeAsync(() => Loop.Owners!.RunAsync("test/ready", [Ro3Emu.Server.Game.Ownership.ReservationKey.Realm],
            () => Task.CompletedTask));
    }

    [Fact]
    public async Task HeldSkillSwitchKeepsPlanAndRuntimeTogetherWhileAnotherRoleCompletes()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        var a = Session("alice"); var b = Session("bob");
        await Ready(a, b);
        var skills = _app.Services.GetRequiredService<SkillService>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Accounts.BeforeOwnedCommitForTest = async label =>
        {
            if (label != "economy/cd13c/skill-switch") return;
            entered.TrySetResult(); await release.Task;
        };
        var switching = Loop.InvokeAsync(() => skills.SwitchPlanAsync(a, new SwitchPlanReq { Plan = 1 }));
        Task? competing = null;
        bool timerApplied = false;
        try
        {
            await entered.Task.WaitAsync(Wait);
            Assert.Equal(0, a.SkillPlans!.CurrentPlanIndex);
            Assert.Equal(0, DBSkillData.Parser.ParseFrom(Roles.LoadSkillPlans(a.Role!.Id)!).CurrentPlanIndex);
            competing = Gm("alice", "!item 1000000 1");
            await Loop.InvokeAsync(() =>
            {
                Loop.PostOwned(a, () => { timerApplied = true; return Task.CompletedTask; });
                return Task.CompletedTask;
            });
            await bob.SendGameAsync(SkillService.RenamePlan, new SkillPlanChangeNameReq { Plan = 0, Name = "Free" });
            Assert.Equal(0, (await Next(bob, SkillService.RenamePlan, SkillPlanChangeNameResp.Parser)).Result);
            Assert.False(competing.IsCompleted);
            Assert.False(timerApplied);
            Assert.Equal(0, CountReceipts("cd13c/skill-switch"));
        }
        finally { Accounts.BeforeOwnedCommitForTest = null; release.TrySetResult(); }
        await switching.WaitAsync(Wait);
        if (competing != null) await competing.WaitAsync(Wait);
        Assert.Equal(1, a.SkillPlans!.CurrentPlanIndex);
        Assert.Equal(1, DBSkillData.Parser.ParseFrom(Roles.LoadSkillPlans(a.Role!.Id)!).CurrentPlanIndex);
        Assert.Equal(1, CountReceipts("cd13c/skill-switch"));
    }

    [ClientDataFact]
    public async Task HeldPaidCastHasOneResolvedDebitSourceAndNoEarlyAction()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Gm("alice", "!item 1002004 2");
        var a = Session("alice"); var b = Session("bob");
        await Ready(a, b);
        await Loop.InvokeAsync(async () => { b.Dead = true; await Character.SetHpSpAsync(b, 0, 0); });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Accounts.BeforeOwnedCommitForTest = async label =>
        {
            if (label != "economy/cd13c/cast-admit") return;
            entered.TrySetResult(); await release.Task;
        };
        Accounts.FailNextEconomyAcknowledgementForTest();
        var opening = EconomySnapshotReader.Read(Accounts.DatabasePath,
            EventEconomy.Catalog(_app.Services.GetRequiredService<Ro3Emu.Server.Data.GameData>()), "auto");
        Task? planChange = null;
        try
        {
            await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2200101, TargetId = b.Role!.Id });
            await entered.Task.WaitAsync(Wait);
            Assert.Equal(2, a.Items.Where(i => i.Entry == 1002004).Sum(i => i.Stack));
            Assert.Equal(2, _app.Services.GetRequiredService<ItemStore>().Load(a.Role!.Id).Where(i => i.Entry == 1002004).Sum(i => i.Stack));
            Assert.Null(a.SkillCast); Assert.True(b.Dead);
            Assert.Equal(0, CountReceipts("cd13c/cast-admit"));
            planChange = Loop.InvokeAsync(() => _app.Services.GetRequiredService<SkillService>()
                .SwitchPlanAsync(a, new SwitchPlanReq { Plan = 1 }));
            await bob.SendGameAsync(SkillService.RenamePlan, new SkillPlanChangeNameReq { Plan = 0, Name = "Free" });
            Assert.Equal(0, (await Next(bob, SkillService.RenamePlan, SkillPlanChangeNameResp.Parser)).Result);
            Assert.False(planChange.IsCompleted);
        }
        finally { Accounts.BeforeOwnedCommitForTest = null; release.TrySetResult(); }
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, BuffService.WorldTreeRevive, ItemWorldTreeReviveNtf.Parser);
        if (planChange != null) await planChange.WaitAsync(Wait);
        Assert.Equal(1, a.SkillPlans!.CurrentPlanIndex);
        Assert.Equal(1, a.Items.Where(i => i.Entry == 1002004).Sum(i => i.Stack));
        Assert.Equal(1, CountReceipts("cd13c/cast-admit"));
        var catalog = EventEconomy.Catalog(_app.Services.GetRequiredService<Ro3Emu.Server.Data.GameData>());
        var closing = EconomySnapshotReader.Read(Accounts.DatabasePath, catalog, "auto");
        var ledger = EconomyLedgerReader.Read(Accounts.DatabasePath, catalog, "auto",
            opening.Cut.CommitSequence!.Value, closing.Cut.CommitSequence!.Value);
        Assert.True(ledger.CompleteHistory, string.Join("; ", ledger.Issues.Select(i => i.Detail)));
        var cast = Assert.Single(ledger.Receipts, r => r.CommandKind == "cd13c/cast-admit");
        var debit = Assert.Single(ledger.Legs, l => l.OperationId == cast.OperationId);
        Assert.Equal((EconomyReason.CastAdmissionCost, 1002004L, -1L), (debit.Reason, debit.Asset.Id, debit.Delta));
        Assert.Single(cast.SourceKeys!);
        var exact = EconomyExactnessChecker.Check(opening, closing, ledger);
        Assert.DoesNotContain(exact.Issues, i => i.Kind == EconomyIssueKind.Corruption);
        Assert.All(exact.Assets, asset => Assert.Equal("0", asset.Residual));
        Assert.All(exact.Locations, location => Assert.Equal("0", location.Residual));
    }

    [ClientDataFact]
    public async Task CancelledAttachmentAfterPaidCommitCannotCastOrRecoverAFreeRetry()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Gm("alice", "!item 1002004 1");
        var a = Session("alice"); var b = Session("bob");
        await Ready(a, b);
        await Loop.InvokeAsync(async () => { b.Dead = true; await Character.SetHpSpAsync(b, 0, 0); });
        var combat = _app.Services.GetRequiredService<CombatService>();
        Accounts.BeforeOwnedApplyForTest = label =>
        {
            if (label == "economy/cd13c/cast-admit") a.Attachment!.Cancelled = true;
            return Task.CompletedTask;
        };
        try
        {
            await Loop.InvokeAsync(() => combat.CastAsync(a, new CastSkillReq { SkillId = 2200101, TargetId = b.Role!.Id }));
            Assert.DoesNotContain(a.Items, i => i.Entry == 1002004);
            Assert.Null(a.SkillCast); Assert.True(b.Dead);
            Assert.Equal(1, CountReceipts("cd13c/cast-admit"));
        }
        finally
        {
            Accounts.BeforeOwnedApplyForTest = null;
            await Loop.InvokeAsync(() => { a.Attachment!.Cancelled = false; return Task.CompletedTask; });
        }
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2200101, TargetId = b.Role!.Id });
        Assert.Equal(ErrorCodes.SkillCostShort, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.Equal(1, CountReceipts("cd13c/cast-admit"));
        Assert.True(b.Dead);
    }
}
