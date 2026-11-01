using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Tests;

public sealed class EconomyVigorRewardTests : IDisposable
{
    private readonly string _directory;
    private readonly AccountStore _accounts;
    private readonly RoleStore _roles;
    private readonly long _role;
    private static readonly EconomyCatalog Catalog = new(new Dictionary<long, EconomyAssetKind>
    { [1] = EconomyAssetKind.Currency, [407] = EconomyAssetKind.Currency });

    public EconomyVigorRewardTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "ro3-vigor-ledger-" + Guid.NewGuid().ToString("N"));
        _accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = _directory }));
        _roles = new RoleStore(_accounts);
        _role = _roles.Create("vigor-account", 1, "Vigor" + Guid.NewGuid().ToString("N")[..8], 1, 1000, 1)!.Id;
        var progress = _roles.LoadProgress(_role);
        progress.Currency[407] = 321;
        _roles.SaveProgress(_role, progress);
        var seed = _roles.NewTransaction();
        seed.RoleBlob(_role, "life_restrict", State(500).ToByteArray());
        seed.Commit(_accounts, "fixture/vigor-ledger");
    }

    private static DBRestrictInfo State(long count) => new()
    {
        RestrictCount = { [1004] = new RestrictInfo { Count = count, LimitCount = 1000 },
        [1005] = new RestrictInfo { Count = 5, LimitCount = 10 } }
    };
    private static string Hash(DBRestrictInfo state) => Convert.ToHexString(SHA256.HashData(state.ToByteArray()));
    private static EconomyVigorRewardEvidence Evidence(DBRestrictInfo after) => new(200, 200, 500, 500, 700,
        Hash(State(500)), Hash(after));
    private EconomyMutation Mutation(string operation) => new(operation, "vigor-account", _role,
        "cd13a/character-box", "request/" + operation, "source/" + operation, 1_000_000, [],
        [new(new(EconomyAssetKind.Currency, 407), 200)],
        JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Reward = new { Entry = 407, Count = 200 } }));
    private RoleItemPlanBuilder Plan(string operation, DBRestrictInfo next, bool evidence = true)
    {
        var tx = _roles.NewTransaction();
        tx.ItemDelta(_role, [], []);
        tx.Progress(_role, _roles.LoadProgress(_role));
        tx.RoleBlob(_role, "life_restrict", next.ToByteArray());
        tx.WithEconomy(Mutation(operation));
        if (evidence) tx.WithVigorReward(Evidence(next));
        return tx;
    }

    [Fact]
    public void ConversionPreservesSourceRewardAndRemovesOnlyThisGrantFromTradeableGoods()
    {
        var mutation = Mutation("source-preserved").WithVigorEvidence(Evidence(State(700)));
        Assert.Empty(mutation.Goods);
        using var outcome = JsonDocument.Parse(mutation.Outcome);
        Assert.Equal(407, outcome.RootElement.GetProperty("Reward").GetProperty("Entry").GetInt32());
        Assert.Equal(200, outcome.RootElement.GetProperty("VigorReward").GetProperty("Accepted").GetInt64());
        Assert.Same(mutation, mutation.WithVigorEvidence(Evidence(State(700))));
    }

    [Theory]
    [InlineData(199, 199, 699)]
    [InlineData(200, 201, 701)]
    [InlineData(200, 200, 701)]
    public void DeclaredGrantCannotAuthorizeDifferentOrExcessVigor(long requested, long accepted, long after)
    {
        var evidence = Evidence(State(700)) with { Requested = requested, Accepted = accepted, After = after };
        Assert.Throws<InvalidOperationException>(() => Mutation("bad-amount").WithVigorEvidence(evidence));
    }

    [Theory]
    [InlineData("cd13b/pet-feed", false)]
    [InlineData("cd13c/skill-reset", false)]
    [InlineData("cd13a/character-box", true)]
    public void DirectConstructorCannotBypassSourceAllowlistOrDoubleMint407(string command, bool currency)
    {
        var outcome = JsonSerializer.SerializeToUtf8Bytes(new { VigorReward = Evidence(State(700)) });
        EconomyQuantity[] goods = currency ? [new(new(EconomyAssetKind.Currency, 407), 200)] : [];
        Assert.Throws<InvalidOperationException>(() => new EconomyMutation("constructor", "vigor-account", _role,
            command, "constructor", "constructor", 1_000_000, [], goods, outcome));
    }

    [Fact]
    public async Task SuccessfulGrantIsAtomicReplayableAndDoesNotMintCurrency407()
    {
        string operation = Guid.NewGuid().ToString("N");
        var first = Plan(operation, State(700));
        var replay = Plan(operation, State(700));
        long cut = EconomySnapshotReader.Read(_accounts.DatabasePath, Catalog, "auto").Cut.CommitSequence!.Value;
        _accounts.FailNextEconomyAcknowledgementForTest();
        await _roles.CommitAsync(first, "test/vigor-grant");
        await _roles.CommitAsync(replay, "test/vigor-replay");
        Assert.Equal(700, ReadState().RestrictCount[1004].Count);
        Assert.Equal(321, _roles.LoadProgress(_role).Currency[407]);
        var ledger = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", cut, cut + 1);
        Assert.True(ledger.CompleteHistory, string.Join(";", ledger.Issues.Select(x => x.Detail)));
        Assert.Single(ledger.Receipts);
        Assert.Empty(ledger.Legs);
    }

    [Fact]
    public async Task RestrictionBlobWithoutConversionEvidenceIsRejected()
    {
        string operation = Guid.NewGuid().ToString("N");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _roles.CommitAsync(
            Plan(operation, State(700), evidence: false), "test/unauthorized-vigor"));
        Assert.Null(EconomyReceiptReader.Read(_accounts.DatabasePath, operation));
        Assert.Equal(500, ReadState().RestrictCount[1004].Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RewardCannotAlterUnrelatedRestrictionOrInflateItsFinalAmount(bool unrelated)
    {
        string operation = Guid.NewGuid().ToString("N");
        var next = State(unrelated ? 700 : 701);
        if (unrelated) next.RestrictCount[1005].Count++;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _roles.CommitAsync(
            Plan(operation, next), "test/vigor-blob-overreach"));
        Assert.Null(EconomyReceiptReader.Read(_accounts.DatabasePath, operation));
        Assert.Equal(State(500), ReadState());
        Assert.Equal(321, _roles.LoadProgress(_role).Currency[407]);
    }

    private DBRestrictInfo ReadState() => DBRestrictInfo.Parser.ParseFrom(
        _roles.LoadLifeRestrict(_role) ?? throw new InvalidOperationException("Missing fixture restriction."));

    public void Dispose()
    {
        _accounts.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }
}
