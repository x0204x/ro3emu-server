using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

public sealed partial class GenreTests
{
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All()
        .Single(s => s.Role?.Id == (long)bot.Self!.Uuid);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TimeSpan OwnerWait = TimeSpan.FromSeconds(10);

    private async Task<string> Share(BotClient seeker)
    {
        await seeker.SendGameAsync(82807, new GenreSeekerCodeReq());
        var reply = await Next(seeker, 82807, GenreSeekerCodeResp.Parser);
        Assert.Equal(0, reply.Result);
        Assert.NotEmpty(reply.Code);
        return reply.Code;
    }

    [ClientDataFact]
    public async Task PairCommitPublishesBothRolesBeforeWaitingRenameAndPreservesWholeBlob()
    {
        await using var donor = await Enter("PairDonor");
        await using var seeker = await Enter("PairSeeker");
        await using var bystander = await Enter("PairOther");
        await Level("pairdonor", 40); await Level("pairseeker", 40); await Level("pairother", 40);
        // EnterWorld returns before SceneLoaded's final Realm online publication. Drain that
        // same-socket work before holding the pair, so the independence check measures rename.
        await bystander.SendGameAsync(82816, new GenreChangePlanNameReq { TargetIndex = 1, Name = "Ready" });
        Assert.Equal(0, (await Next(bystander, 82816, GenreChangePlanNameResp.Parser)).Result);
        string code = await Share(seeker);
        var mine = Session(donor); var other = Session(seeker);
        var reached = Signal(); var release = Signal(); var deferred = Signal();
        var accounts = Service<AccountStore>(); var owners = Service<OwnerRegistry>();
        accounts.BeforeOwnedApplyForTest = async label =>
        {
            if (label != "cd13b/genre-pair") return;
            reached.TrySetResult(); await release.Task.WaitAsync(OwnerWait);
        };
        owners.DeferredForTest = origin => { if (origin == "genre/command") deferred.TrySetResult(); };
        try
        {
            await donor.SendGameAsync(82812, new GenreResponseCodeReq { Code = code });
            await reached.Task.WaitAsync(OwnerWait);
            await Service<GameLoop>().InvokeAsync(() =>
            {
                Assert.Equal(0, other.Genre!.ResponseId);
                Assert.True(mine.Genre!.MyRecommend?.MyRecommendPlans.Count is null or 0);
                return Task.CompletedTask;
            });
            // The other participant is reached through another socket; it must wait on the same role key.
            await seeker.SendGameAsync(82816, new GenreChangePlanNameReq { TargetIndex = 1, Name = "Kept" });
            await deferred.Task.WaitAsync(OwnerWait);
            await bystander.SendGameAsync(82816, new GenreChangePlanNameReq { TargetIndex = 1, Name = "Free" });
            Assert.Equal(0, (await Next(bystander, 82816, GenreChangePlanNameResp.Parser)).Result);
        }
        finally
        {
            release.TrySetResult(); accounts.BeforeOwnedApplyForTest = null; owners.DeferredForTest = null;
        }
        var response = await Next(donor, 82812, GenreResponseCodeResp.Parser);
        Assert.Equal(0, response.Result);
        Assert.Equal(0, (await Next(seeker, 82816, GenreChangePlanNameResp.Parser)).Result);
        var saved = DBGenre.Parser.ParseFrom(Service<RoleStore>().LoadGenre(other.Role!.Id)!);
        Assert.Equal("Kept", saved.GenreMap.SystemMap[1].PlanName);
        Assert.Equal(response.ResponseId, saved.ResponseId);
        Assert.Contains(response.ResponseId, saved.OtherRecommend.Recommend[mine.Role!.Id].GenrePlans.Keys);
        Assert.Contains(response.ResponseId, mine.Genre!.MyRecommend.MyRecommendPlans[other.Role.Id].GenrePlans.Keys);
    }

    private async Task<long> GearRecommendation(BotClient donor, BotClient seeker)
    {
        string code = await Share(seeker);
        await donor.SendGameAsync(82812, new GenreResponseCodeReq { Code = code });
        var response = await Next(donor, 82812, GenreResponseCodeResp.Parser);
        Assert.Equal(0, response.Result);
        long dagger = Session(donor).Items.Single(i => i.Entry == 2010121).Guid;
        await donor.SendGameAsync(82803, new GenreResponserSaveReq
        {
            SeekUserId = response.SeekUserId, ResponseId = response.ResponseId,
            Plan = new GenrePlanConcrete { EquipPlan = new DBEquipPlan { ItemsEquipGrid = { [1] = dagger } } },
        });
        Assert.Equal(0, (await Next(donor, 82803, GenreResponserSaveResp.Parser)).Result);
        await donor.SendGameAsync(82802, new GenreResponserSendPlanReq
        { SeekUserId = response.SeekUserId, ResponseId = response.ResponseId });
        Assert.Equal(0, (await Next(donor, 82802, GenreResponserSendPlanResp.Parser)).Result);
        return response.ResponseId;
    }

    [ClientDataFact]
    public async Task OfflineSeekerLoginWaitsForPairApplyAndLoadsCommittedRecommendation()
    {
        await using var donor = await Enter("LoginDonor");
        var seeker = await Enter("LoginSeeker");
        await Level("logindonor", 40); await Level("loginseeker", 40);
        string code = await Share(seeker);
        long seekerId = Session(seeker).Role!.Id;
        var detached = Signal();
        Service<RoleHandover>().PhaseForTest = (phase, session) =>
        { if (phase == "Detached" && session.Account == "loginseeker") detached.TrySetResult(); };
        try { await seeker.DisposeAsync(); await detached.Task.WaitAsync(OwnerWait); }
        finally { Service<RoleHandover>().PhaseForTest = null; }
        var reached = Signal(); var release = Signal(); var loginWaiting = Signal();
        Service<AccountStore>().BeforeOwnedApplyForTest = async label =>
        {
            if (label != "cd13b/genre-pair") return;
            reached.TrySetResult(); await release.Task.WaitAsync(OwnerWait);
        };
        Service<OwnerRegistry>().DeferredForTest = origin =>
        { if (origin == "role select") loginWaiting.TrySetResult(); };
        Task<BotClient>? login = null;
        try
        {
            await donor.SendGameAsync(82812, new GenreResponseCodeReq { Code = code });
            await reached.Task.WaitAsync(OwnerWait);
            login = Enter("LoginSeeker", create: false);
            await loginWaiting.Task.WaitAsync(OwnerWait);
            Assert.False(login.IsCompleted);
            Assert.Equal(RoleOwnerState.Detached, await Service<GameLoop>().InvokeAsync(() =>
                Task.FromResult(Service<OwnerRegistry>().Cached(seekerId)!.State)));
        }
        finally
        {
            release.TrySetResult(); Service<AccountStore>().BeforeOwnedApplyForTest = null;
            Service<OwnerRegistry>().DeferredForTest = null;
        }
        var response = await Next(donor, 82812, GenreResponseCodeResp.Parser);
        Assert.Equal(0, response.Result);
        await using var restored = await login!.WaitAsync(OwnerWait);
        var state = Session(restored).Genre!;
        Assert.Equal(response.ResponseId, state.ResponseId);
        Assert.Contains(response.ResponseId, state.OtherRecommend.Recommend[Session(donor).Role!.Id].GenrePlans.Keys);
        Assert.Equal(state.ToByteArray(), Service<RoleStore>().LoadGenre(seekerId));
    }

    [ClientDataFact]
    public async Task OfflineDonorLoginWaitsUntilRecommendationSnapshotIsApplied()
    {
        var donor = await Enter("OfflineDonor");
        await using var seeker = await Enter("OfflineSeeker");
        await Level("offlinedonor", 40); await Level("offlineseeker", 40);
        long response = await GearRecommendation(donor, seeker);
        long donorId = Session(donor).Role!.Id;
        long ownGuid = Session(seeker).Items.Single(i => i.Entry == 2010121).Guid;
        var detached = Signal();
        Service<RoleHandover>().PhaseForTest = (phase, session) =>
        { if (phase == "Detached" && session.Account == "offlinedonor") detached.TrySetResult(); };
        try { await donor.DisposeAsync(); await detached.Task.WaitAsync(OwnerWait); }
        finally { Service<RoleHandover>().PhaseForTest = null; }
        var reached = Signal(); var release = Signal(); var loginWaiting = Signal();
        Service<AccountStore>().BeforeOwnedApplyForTest = async label =>
        {
            if (!label.EndsWith("cd13b/genre-apply", StringComparison.Ordinal)) return;
            reached.TrySetResult(); await release.Task.WaitAsync(OwnerWait);
        };
        Service<OwnerRegistry>().DeferredForTest = origin =>
        { if (origin == "role select") loginWaiting.TrySetResult(); };
        Task<BotClient>? login = null;
        try
        {
            await seeker.SendGameAsync(82813, new GenreSeekerApplyPlanReq
            { PlanId = 1, ResponseId = response, ResponseUserId = donorId, SystemType = { [3] = 1 } });
            await reached.Task.WaitAsync(OwnerWait);
            login = Enter("OfflineDonor", create: false);
            await loginWaiting.Task.WaitAsync(OwnerWait);
            Assert.False(login.IsCompleted);
            Assert.Equal(0, Session(seeker).Items.Single(i => i.Guid == ownGuid).Grid);
        }
        finally
        {
            release.TrySetResult(); Service<AccountStore>().BeforeOwnedApplyForTest = null;
            Service<OwnerRegistry>().DeferredForTest = null;
        }
        Assert.Equal(0, (await Next(seeker, 82813, GenreSeekerApplyPlanResp.Parser)).Result);
        await using var restored = await login!.WaitAsync(OwnerWait);
        Assert.Equal(donorId, Session(restored).Role!.Id);
        Assert.Equal(ownGuid, Session(seeker).EquipmentPlans.EquipPlans[1].ItemsEquipGrid[1]);
        Assert.Equal(1, Service<ItemStore>().Load(Session(seeker).Role!.Id).Single(i => i.Guid == ownGuid).Grid);
    }

    [ClientDataFact]
    public async Task RecommendationHoldsDonorSnapshotAgainstGmThroughApplyAndHasNeutralExactLedger()
    {
        await using var donor = await Enter("SnapDonor");
        await using var seeker = await Enter("SnapSeeker");
        await Level("snapdonor", 40); await Level("snapseeker", 40);
        long response = await GearRecommendation(donor, seeker);
        var donorSession = Session(donor); var target = Session(seeker);
        long donorGuid = donorSession.Items.Single(i => i.Entry == 2010121).Guid;
        long ownGuid = target.Items.Single(i => i.Entry == 2010121).Guid;
        var catalog = EconomyCatalog.Load(Path.Combine(RepoRoot(), "data"));
        var database = Service<AccountStore>().DatabasePath;
        var opening = EconomySnapshotReader.Read(database, catalog, "auto");
        var reached = Signal(); var release = Signal(); var deferred = Signal();
        Service<AccountStore>().BeforeOwnedApplyForTest = async label =>
        {
            if (!label.EndsWith("cd13b/genre-apply", StringComparison.Ordinal)) return;
            reached.TrySetResult(); await release.Task.WaitAsync(OwnerWait);
        };
        Service<OwnerRegistry>().DeferredForTest = _ => deferred.TrySetResult();
        using var client = new HttpClient();
        Task<HttpResponseMessage>? gm = null;
        try
        {
            await seeker.SendGameAsync(82813, new GenreSeekerApplyPlanReq
            { PlanId = 1, ResponseId = response, ResponseUserId = donorSession.Role!.Id, SystemType = { [3] = 1 } });
            await reached.Task.WaitAsync(OwnerWait);
            Assert.Equal(0, target.Items.Single(i => i.Guid == ownGuid).Grid);
            gm = client.PostAsync($"{_http}/ro3emu/gm?account=snapdonor", new StringContent("!delitem 2010121 1"));
            await deferred.Task.WaitAsync(OwnerWait);
            Assert.False(gm.IsCompleted);
            Assert.Contains(donorSession.Items, i => i.Guid == donorGuid);
            // Cut before the deliberately uncovered GM deletion; recommendation itself is a neutral relocation.
            var closing = EconomySnapshotReader.Read(database, catalog, "auto");
            var ledger = EconomyLedgerReader.Read(database, catalog, "auto", opening.Cut.CommitSequence!.Value,
                closing.Cut.CommitSequence!.Value);
            Assert.True(ledger.CompleteHistory, string.Join(";", ledger.Issues.Select(i => i.Detail)));
            Assert.Single(ledger.Receipts);
            Assert.DoesNotContain(ledger.Legs, l => l.Kind is EconomyLegKind.Source or EconomyLegKind.Sink);
            Assert.Contains(ledger.Legs, l => l.Kind == EconomyLegKind.Transfer && l.Location.Instance == ownGuid.ToString());
            Assert.DoesNotContain(ledger.Legs, l => l.Location.Instance == donorGuid.ToString());
            var result = EconomyExactnessChecker.Check(opening, closing, ledger);
            Assert.All(result.Assets, asset => Assert.Equal("0", asset.Residual));
            Assert.All(result.Locations, location => Assert.Equal("0", location.Residual));
            Assert.DoesNotContain(result.Issues, issue => issue.Kind == EconomyIssueKind.Corruption);
        }
        finally
        {
            release.TrySetResult(); Service<AccountStore>().BeforeOwnedApplyForTest = null;
            Service<OwnerRegistry>().DeferredForTest = null;
        }
        Assert.Equal(0, (await Next(seeker, 82813, GenreSeekerApplyPlanResp.Parser)).Result);
        (await gm!.WaitAsync(OwnerWait)).EnsureSuccessStatusCode();
        Assert.Equal(1, target.Items.Single(i => i.Guid == ownGuid).Grid);
        Assert.DoesNotContain(donorSession.Items, i => i.Guid == donorGuid);
        Assert.Equal(ownGuid, target.EquipmentPlans.EquipPlans[1].ItemsEquipGrid[1]);
    }

    [ClientDataFact]
    public async Task DonorEquipmentCommandWaitsForRecommendationSnapshotReservation()
    {
        await using var donor = await Enter("EquipDonor");
        await using var seeker = await Enter("EquipSeeker");
        await Level("equipdonor", 40); await Level("equipseeker", 40);
        long response = await GearRecommendation(donor, seeker);
        var donorSession = Session(donor); var recipient = Session(seeker);
        long donorGuid = donorSession.Items.Single(i => i.Entry == 2010121).Guid;
        long ownGuid = recipient.Items.Single(i => i.Entry == 2010121).Guid;
        var reached = Signal(); var release = Signal(); var deferred = Signal();
        Service<AccountStore>().BeforeOwnedApplyForTest = async label =>
        {
            if (!label.EndsWith("cd13b/genre-apply", StringComparison.Ordinal)) return;
            reached.TrySetResult(); await release.Task.WaitAsync(OwnerWait);
        };
        Service<OwnerRegistry>().DeferredForTest = origin =>
        { if (origin == "dispatcher") deferred.TrySetResult(); };
        try
        {
            await seeker.SendGameAsync(82813, new GenreSeekerApplyPlanReq
            { PlanId = 1, ResponseId = response, ResponseUserId = donorSession.Role!.Id, SystemType = { [3] = 1 } });
            await reached.Task.WaitAsync(OwnerWait);
            // This ordinary command reserves only the donor role, unlike the guarded Realm GM route.
            await donor.SendGameAsync(CharacterService.EquipItemOpr,
                new EquipItemOprReq { Opr = 0, Pos = (EquipGridType)1, ItemGUID = donorGuid });
            await deferred.Task.WaitAsync(OwnerWait);
            Assert.Equal(0, donorSession.Items.Single(i => i.Guid == donorGuid).Grid);
            Assert.Equal(0, recipient.Items.Single(i => i.Guid == ownGuid).Grid);
        }
        finally
        {
            release.TrySetResult(); Service<AccountStore>().BeforeOwnedApplyForTest = null;
            Service<OwnerRegistry>().DeferredForTest = null;
        }
        Assert.Equal(0, (await Next(seeker, 82813, GenreSeekerApplyPlanResp.Parser)).Result);
        Assert.Equal(0, (await Next(donor, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);
        Assert.Equal(1, donorSession.Items.Single(i => i.Guid == donorGuid).Grid);
        Assert.Equal(ownGuid, recipient.EquipmentPlans.EquipPlans[1].ItemsEquipGrid[1]);
        Assert.NotEqual(donorGuid, ownGuid);
    }

    [ClientDataFact]
    public async Task GenreLedgerFaultRefusesWithoutPublishingEquipmentOrPlan()
    {
        await using var donor = await Enter("FaultDonor");
        await using var seeker = await Enter("FaultSeeker");
        await Level("faultdonor", 40); await Level("faultseeker", 40);
        long response = await GearRecommendation(donor, seeker);
        var session = Session(seeker);
        var old = session.Genre!.ToByteArray();
        FixtureSql.Execute(Service<AccountStore>(), "CREATE TRIGGER genre_ledger_fault BEFORE INSERT ON economy_entry BEGIN SELECT RAISE(ABORT,'genre ledger fault'); END");
        try
        {
            await seeker.SendGameAsync(82813, new GenreSeekerApplyPlanReq
            { PlanId = 1, ResponseId = response, ResponseUserId = Session(donor).Role!.Id, SystemType = { [3] = 1 } });
            Assert.Equal(ErrorCodes.Unknown, (await Next(seeker, 82813, GenreSeekerApplyPlanResp.Parser)).Result);
            Assert.Equal(old, session.Genre.ToByteArray());
            Assert.Equal(old, Service<RoleStore>().LoadGenre(session.Role!.Id));
            Assert.Equal(0, session.Items.Single(i => i.Entry == 2010121).Grid);
            Assert.Equal(0, Service<ItemStore>().Load(session.Role.Id).Single(i => i.Entry == 2010121).Grid);
        }
        finally { FixtureSql.Execute(Service<AccountStore>(), "DROP TRIGGER genre_ledger_fault"); }
    }
}
