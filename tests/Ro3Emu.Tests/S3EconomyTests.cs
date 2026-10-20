using Google.Protobuf;
using Ro3Emu.Bot;
using Ro3Emu.LoadTest;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;
using Romsg;

namespace Ro3Emu.Tests;

/// <summary>Hand-calculated §4.1 oracles. Decoder compartment fixtures are in S3EconomyDecoderTests.</summary>
public sealed class S3EconomyTests
{
    private static readonly EconomyAsset Coin = new(EconomyAssetKind.Currency, 1);
    private static readonly EconomyAsset Item = new(EconomyAssetKind.Item, 1000000);
    private static readonly EconomyLocation Alice = new(new(EconomyOwnerKind.Role, "1"), EconomyCompartment.Currency, "1");
    private static readonly EconomyLocation Bob = new(new(EconomyOwnerKind.Role, "2"), EconomyCompartment.Currency, "1");
    private static readonly EconomyLocation Offline = new(new(EconomyOwnerKind.Recovery, "deleted-role:3"), EconomyCompartment.Mail, "owed");
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-09T10:00:00Z");
    private static EconomySnapshot Cut(long? sequence, params EconomyHolding[] holdings) =>
        new(new("fixture-realm", "schema-1", "catalog-1", "coverage-1", sequence, Start.AddMinutes(sequence ?? 0)), holdings, [], [], [], 0);
    private static EconomyHolding Coins(EconomyLocation location, long quantity) => new(Coin, location, quantity);
    private static EconomyLedgerWindow Window(EconomyLeg[] legs, EconomyReceipt[] receipts, long through = 2) =>
        new("fixture-realm", "catalog-1", "coverage-1", 1, through, true, legs, receipts, [], [], []);
    private static EconomyLeg Mint(long amount, string op = "grant", long seq = 2) =>
        new(seq, op, 0, EconomyLegKind.Source, EconomyReason.GmGrant, Coin, Alice, amount, SourceKey: "gm:1");
    private static EconomyLedgerWindow Grant(long amount = 2) => Window([Mint(amount)], [new(2, "grant", 1, ["gm:1"])]);

    [Fact]
    public void Persisted_type_surface_matches_the_reviewed_economic_classification() =>
        Assert.Equal(EconomySnapshotReader.ExpectedFormatManifestHash, EconomySnapshotReader.FormatManifestHash);

    [Fact]
    public void Source_sink_and_real_owner_equations_are_exact_and_namespaced()
    {
        var opening = Cut(1, Coins(Alice, 100), new(Item, Alice, 3, "plain"));
        var closing = Cut(2, Coins(Alice, 90), new(Item, Alice, 5, "plain"));
        var legs = new EconomyLeg[]
        {
            new(2, "shop", 0, EconomyLegKind.Sink, EconomyReason.NpcShopPayment, Coin, Alice, -10),
            new(2, "shop", 1, EconomyLegKind.Source, EconomyReason.NpcShopGoods, Item, Alice, 2, BeforeFingerprint: "plain", AfterFingerprint: "plain", SourceKey: "admission:shop1")
        };
        var result = EconomyExactnessChecker.Check(opening, closing, Window(legs, [new(2, "shop", 2, ["admission:shop1"])]));
        Assert.Equal(EconomyVerdict.Pass, result.Verdict);
        var coins = Assert.Single(result.Assets, x => x.Asset == Coin);
        Assert.Equal("10", coins.Sinks);
        Assert.Equal("0", coins.Sources);
        Assert.All(result.Assets, x => { Assert.Equal("0", x.Residual); Assert.Equal("0", x.LegResidual); });
        Assert.Equal("2", Assert.Single(result.Assets, x => x.Asset == Item).Sources);
    }

    [Fact]
    public void Transfers_cover_disappearing_owners_and_deleted_offline_recipients()
    {
        var legs = Transfer(Alice, Offline, 20);
        var result = EconomyExactnessChecker.Check(Cut(1, Coins(Alice, 20)), Cut(2, Coins(Offline, 20)), Window(legs, [new(2, "mail", 2)]));
        Assert.Equal(EconomyVerdict.Pass, result.Verdict);
        Assert.Equal(2, result.Locations.Count);
        Assert.Equal("0", Assert.Single(result.Assets).Residual);
    }

    [Fact]
    public void Balanced_wrong_recipient_is_failure_even_with_zero_realm_residual()
    {
        var result = EconomyExactnessChecker.Check(Cut(1, Coins(Alice, 20)), Cut(2, Coins(Bob, 20)),
            Window(Transfer(Alice, Offline, 20), [new(2, "mail", 2)]));
        Assert.Equal(EconomyVerdict.Fail, result.Verdict);
        Assert.Equal("0", Assert.Single(result.Assets).Residual);
        Assert.Equal(2, result.Locations.Count(x => x.Residual != "0"));
    }

    private static EconomyLeg[] Transfer(EconomyLocation from, EconomyLocation to, long quantity) =>
    [
        new(2, "mail", 0, EconomyLegKind.Transfer, EconomyReason.MailClaim, Coin, from, -quantity, 0, to),
        new(2, "mail", 1, EconomyLegKind.Transfer, EconomyReason.MailClaim, Coin, to, quantity, 0, from)
    ];

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    public void One_unit_loss_or_duplication_is_detected(long closing) =>
        Assert.Equal(EconomyVerdict.Fail, EconomyExactnessChecker.Check(Cut(1, Coins(Alice, 3)), Cut(2, Coins(Alice, closing)), Grant()).Verdict);

    [Fact]
    public void Existing_assets_are_baseline_and_no_ledger_means_not_measured()
    {
        var old = Cut(null, Coins(Alice, 1000));
        var result = EconomyExactnessChecker.Check(old, old, null);
        Assert.Equal(EconomyVerdict.NotMeasured, result.Verdict);
        Assert.Empty(result.Assets); // No synthesized starter/migration source to balance the baseline.
        Assert.Contains(result.Issues, x => x.Key == "cut/watermark");
    }

    [Fact]
    public void Unknown_coverage_does_not_hide_corruption_or_a_demonstrated_residual()
    {
        var opening = Cut(1, Coins(Alice, 3)) with { Issues = [new(EconomyIssueKind.UnknownSchema, "future", "unknown compartment")] };
        Assert.Equal(EconomyVerdict.NotMeasured, EconomyExactnessChecker.Check(opening, Cut(2, Coins(Alice, 5)), Grant()).Verdict);
        var failure = EconomyExactnessChecker.Check(opening, Cut(2, Coins(Alice, 6)), Grant());
        Assert.Equal(EconomyVerdict.Fail, failure.Verdict);
        Assert.Contains(failure.Issues, x => x.Kind == EconomyIssueKind.UnknownSchema);
        Assert.Equal(EconomyVerdict.Fail, EconomyExactnessChecker.Check(opening with
        { Issues = [new(EconomyIssueKind.Corruption, "blob", "malformed")] }, Cut(null), null).Verdict);
    }

    [Theory]
    [InlineData("missing receipt")]
    [InlineData("leg count")]
    [InlineData("duplicate leg")]
    [InlineData("duplicate receipt")]
    [InlineData("receipt sequence")]
    [InlineData("uncommitted receipt")]
    [InlineData("unknown reason")]
    [InlineData("illegal reason")]
    [InlineData("illegal sign")]
    [InlineData("missing source")]
    [InlineData("replayed source")]
    [InlineData("outside range")]
    public void Bad_ledger_evidence_fails_closed(string fault)
    {
        var ledger = Grant();
        var leg = ledger.Legs[0];
        var receipt = ledger.Receipts[0];
        ledger = fault switch
        {
            "missing receipt" => ledger with { Receipts = [] },
            "leg count" => ledger with { Receipts = [receipt with { LegCount = 2 }] },
            "duplicate leg" => ledger with { Legs = [leg, leg] },
            "duplicate receipt" => ledger with { Receipts = [receipt, receipt] },
            "receipt sequence" => ledger with { Receipts = [receipt with { CommitSequence = 1 }] },
            "uncommitted receipt" => ledger with { Receipts = [receipt with { Committed = false }] },
            "unknown reason" => ledger with { Legs = [leg with { Reason = (EconomyReason)9999 }] },
            "illegal reason" => ledger with { Legs = [leg with { Reason = EconomyReason.RefineCost }] },
            "illegal sign" => ledger with { Legs = [leg with { Delta = -2 }] },
            "missing source" => ledger with { Receipts = [receipt with { SourceKeys = [] }] },
            "replayed source" => ledger with { PriorSourceClaims = [new("gm:1", "old-op")] },
            "outside range" => ledger with { Legs = [leg with { CommitSequence = 3 }] },
            _ => throw new ArgumentException(fault)
        };
        Assert.Equal(EconomyVerdict.Fail, EconomyExactnessChecker.Check(Cut(1, Coins(Alice, 3)), Cut(2, Coins(Alice, 5)), ledger).Verdict);
    }

    [Fact]
    public void Duplicate_natural_source_in_window_fails_despite_balanced_authorized_looking_sources()
    {
        var ledger = Window([Mint(2, "a", 2) with { SourceKey = "kill:7" }, Mint(2, "b", 3) with { SourceKey = "kill:7" }], [new(2, "a", 1, ["kill:7"]), new(3, "b", 1, ["kill:7"])], 3);
        var result = EconomyExactnessChecker.Check(Cut(1, Coins(Alice, 3)), Cut(3, Coins(Alice, 7)), ledger);
        Assert.Equal("0", Assert.Single(result.Assets).Residual);
        Assert.Equal(EconomyVerdict.Fail, result.Verdict);
    }

    [Fact]
    public void Every_source_in_a_multi_source_receipt_is_bound_and_checked_against_retained_claims()
    {
        var ledger = Window([Mint(2) with { SourceKey = "a" }, Mint(3) with { LegNumber = 1, SourceKey = "b" }],
            [new(2, "grant", 2, ["a", "b"])]);
        var opening = Cut(1, Coins(Alice, 10)); var closing = Cut(2, Coins(Alice, 15));
        Assert.Equal(EconomyVerdict.Pass, EconomyExactnessChecker.Check(opening, closing, ledger).Verdict);
        Assert.Equal(EconomyVerdict.Fail, EconomyExactnessChecker.Check(opening, closing,
            ledger with { PriorSourceClaims = [new("b", "old-operation")] }).Verdict);
        Assert.Equal(EconomyVerdict.Fail, EconomyExactnessChecker.Check(opening, closing,
            ledger with { Receipts = [new(2, "grant", 2, ["a"])] }).Verdict);
    }

    [Fact]
    public void Item_transfer_without_fingerprints_cannot_qualify()
    {
        var legs = Transfer(Alice, Bob, 1).Select(x => x with { Asset = Item }).ToArray();
        Assert.Equal(EconomyVerdict.NotMeasured, EconomyExactnessChecker.Check(Cut(1, new EconomyHolding(Item, Alice, 1)),
            Cut(2, new EconomyHolding(Item, Bob, 1)), Window(legs, [new(2, "mail", 2)])).Verdict);
    }

    [Fact]
    public void Same_commit_group_uses_explicit_operation_order_for_fingerprint_chain()
    {
        var a = Cut(1, new EconomyHolding(Item, Alice, 1, "old")); var b = Cut(2, new EconomyHolding(Item, Alice, 1, "new"));
        var ledger = Window([
            new(2, "z-first", 0, EconomyLegKind.Metadata, EconomyReason.ItemMetadata, Item, Alice, 0, BeforeFingerprint: "old", AfterFingerprint: "middle"),
            new(2, "a-second", 0, EconomyLegKind.Metadata, EconomyReason.ItemMetadata, Item, Alice, 0, BeforeFingerprint: "middle", AfterFingerprint: "new")
        ], [new(2, "z-first", 1, CommitOrder: 0), new(2, "a-second", 1, CommitOrder: 1)]);
        Assert.Equal(EconomyVerdict.Pass, EconomyExactnessChecker.Check(a, b, ledger).Verdict);
        Assert.Equal(EconomyVerdict.Fail, EconomyExactnessChecker.Check(a, b,
            ledger with { Receipts = [new(2, "z-first", 1), new(2, "a-second", 1)] }).Verdict);
    }

    [Fact]
    public void Missing_archive_or_versions_or_uncovered_writes_cannot_pass()
    {
        var a = Cut(1, Coins(Alice, 3)); var b = Cut(2, Coins(Alice, 5));
        foreach (var ledger in new[]
        {
            Grant() with { CompleteHistory = false }, Grant() with { CatalogVersion = "new" },
            Grant() with { RealmId = "other" }, Grant() with { FromExclusive = 0 },
            Grant() with { CoverageVersion = "new" }, Grant() with { ReasonVersion = "new" },
            Grant() with { UncoveredMutationFamilies = ["shop"] }
        }) Assert.NotEqual(EconomyVerdict.Pass, EconomyExactnessChecker.Check(a, b, ledger).Verdict);
        Assert.Equal(EconomyVerdict.NotMeasured, EconomyExactnessChecker.Check(a, b with
        { Issues = [new(EconomyIssueKind.RecoveryDebt, "paid:1", "unresolved grant")] }, Grant()).Verdict);
    }

    [Fact]
    public void Transfer_edges_require_reciprocity_and_each_asset_balances_separately()
    {
        var legs = Transfer(Alice, Bob, 20);
        foreach (var wrong in new[]
        {
            legs[1] with { Asset = new(EconomyAssetKind.Item, 1) },
            legs[1] with { Counterparty = Offline }, legs[1] with { EdgeNumber = 1 }, legs[1] with { Delta = 19 }
        })
            Assert.Equal(EconomyVerdict.Fail, EconomyExactnessChecker.Check(Cut(1, Coins(Alice, 20)), Cut(2, Coins(Bob, 20)),
                Window([legs[0], wrong], [new(2, "mail", 2)])).Verdict);
    }

    [Fact]
    public void Metadata_requires_a_complete_fingerprint_chain_even_when_quantity_is_unchanged()
    {
        var location = Alice with { Compartment = EconomyCompartment.Inventory, Instance = "item:77" };
        var a = Cut(1, new EconomyHolding(Item, location, 1, "roll:old"));
        var b = Cut(2, new EconomyHolding(Item, location, 1, "roll:new"));
        var leg = new EconomyLeg(2, "refine", 0, EconomyLegKind.Metadata, EconomyReason.ItemMetadata, Item, location, 0,
            BeforeFingerprint: "roll:old", AfterFingerprint: "roll:new");
        var ledger = Window([leg], [new(2, "refine", 1)]);
        Assert.Equal(EconomyVerdict.Pass, EconomyExactnessChecker.Check(a, b, ledger).Verdict);
        Assert.Equal(EconomyVerdict.Fail, EconomyExactnessChecker.Check(a, b, Window([], [])).Verdict);
        Assert.Equal(EconomyVerdict.Fail, EconomyExactnessChecker.Check(a, b, ledger with { Legs = [leg with { BeforeFingerprint = "invented" }] }).Verdict);
    }

    [Fact]
    public void Transfer_tracks_new_item_identity_without_assuming_guid_survives_delivery()
    {
        var from = Alice with { Compartment = EconomyCompartment.Mail, Instance = "old:7" };
        var to = Bob with { Compartment = EconomyCompartment.Inventory, Instance = "new:8" };
        var legs = new EconomyLeg[]
        {
            new(2, "claim", 0, EconomyLegKind.Transfer, EconomyReason.MailClaim, Item, from, -1, 0, to, "bound-roll", null),
            new(2, "claim", 1, EconomyLegKind.Transfer, EconomyReason.MailClaim, Item, to, 1, 0, from, null, "bound-roll")
        };
        Assert.Equal(EconomyVerdict.Pass, EconomyExactnessChecker.Check(Cut(1, new EconomyHolding(Item, from, 1, "bound-roll")),
            Cut(2, new EconomyHolding(Item, to, 1, "bound-roll")), Window(legs, [new(2, "claim", 2)])).Verdict);
    }

    [Fact]
    public void Negative_intermediate_committed_balance_is_detected_even_if_closing_was_replenished()
    {
        var ledger = Window([
            new(2, "spend", 0, EconomyLegKind.Sink, EconomyReason.GmRemove, Coin, Alice, -4), Mint(4, "grant", 3)
        ], [new(2, "spend", 1), new(3, "grant", 1, ["gm:1"])], 3);
        var result = EconomyExactnessChecker.Check(Cut(1, Coins(Alice, 3)), Cut(3, Coins(Alice, 3)), ledger);
        Assert.Equal("0", Assert.Single(result.Assets).Residual);
        Assert.Equal(EconomyVerdict.Fail, result.Verdict);
        Assert.Contains(result.Issues, x => x.Key.StartsWith("balance/2/"));
    }

    [Fact]
    public void Wide_arithmetic_reports_overflow_without_wrapping_and_deleted_balances_are_checked()
    {
        var result = EconomyExactnessChecker.Check(Cut(1, Coins(Alice, long.MaxValue)), Cut(2), Window([], []));
        Assert.Equal(EconomyVerdict.Fail, result.Verdict);
        Assert.Equal("-9223372036854775807", Assert.Single(result.Assets).Residual);
        Assert.Equal(EconomyVerdict.Fail, EconomyExactnessChecker.Check(Cut(1, Coins(Alice, long.MaxValue), Coins(Alice, 1)), Cut(2), null).Verdict);
    }

    [Fact]
    public void Epochs_and_asset_namespaces_never_net_against_each_other()
    {
        var old = new EconomyAsset(EconomyAssetKind.EventResource, 1, "season1");
        var newer = old with { Epoch = "season2" };
        var result = EconomyExactnessChecker.Check(Cut(1, new EconomyHolding(old, Alice, 5)), Cut(2, new EconomyHolding(newer, Alice, 5)), Window([], []));
        Assert.Equal(EconomyVerdict.Fail, result.Verdict);
        Assert.Equal(2, result.Assets.Count);
    }

    [Fact]
    public void S3_packets_and_fixture_labels_cannot_certify_exactness_and_corruption_wins_over_incomplete_workload()
    {
        var report = new RunReport
        {
            Scenario = "S3", RequestedBots = 500, TargetBots = 500, RequestedSeconds = 1800, StartedUtc = Start,
            S3OpeningEconomy = Cut(null, Coins(Alice, 3)), S3ClosingEconomy = Cut(null, Coins(Alice, 5)),
            S3ExpectedExternalChange = new() { [1] = 2 }, S3CompletedRun = true, S3SetupFixtures = ["finite setup"],
            S3TowerEntries = 1, S3GatherCompletions = 1
        };
        foreach (var name in new[] { "exchange-list", "exchange-buy", "mail-claim", "npc-shop-buy", "tower-entry", "gather-start" })
        {
            var tally = new ActionTally(); tally.Plan(); tally.Offer(); tally.Complete(1); report.Actions[name] = tally.Snapshot();
        }
        Assert.Equal("NOT MEASURED", report.EvaluateLedgerRow().Status);
        report.S3OpeningEconomy = Cut(1, Coins(Alice, 3));
        report.S3ClosingEconomy = Cut(2, Coins(Alice, 6)); report.S3CommittedLedger = Grant();
        report.S3UntrackedEconomySources = ["unknown gathering grant"];
        Assert.Equal("FAIL", report.EvaluateLedgerRow().Status);
        report.S3ClosingEconomy = Cut(2, Coins(Alice, 5));
        Assert.Equal("NOT MEASURED", report.EvaluateLedgerRow().Status);
    }

    [Fact]
    public void S3_retains_corruption_failure_even_when_the_other_cut_is_missing()
    {
        var corrupt = Cut(null) with { Issues = [new(EconomyIssueKind.Corruption, "item/7", "negative stack")] };
        Assert.Equal("FAIL", new RunReport
        {
            Scenario = "S3", TargetBots = 500, RequestedBots = 20,
            RequestedSeconds = 60, StartedUtc = Start, S3OpeningEconomy = corrupt
        }.EvaluateLedgerRow().Status);
        Assert.Equal("FAIL", new RunReport
        {
            Scenario = "S3", TargetBots = 500, RequestedBots = 20,
            RequestedSeconds = 60, StartedUtc = Start, S3ClosingEconomy = corrupt
        }.EvaluateLedgerRow().Status);
    }

    [Fact]
    public void S3_requires_real_ledger_and_complete_workload_but_not_a_fixture_label()
    {
        var closing = Cut(2, Coins(Alice, 5));
        var report = new RunReport
        {
            Scenario = "S3", TargetBots = 500, RequestedBots = 500, RequestedSeconds = 1800, StartedUtc = Start, S3CompletedRun = true,
            S3OpeningEconomy = Cut(1, Coins(Alice, 3)),
            S3ClosingEconomy = closing with { Cut = closing.Cut with { CapturedUtc = Start.AddMinutes(32) } },
            S3CommittedLedger = Grant(), S3TowerEntries = 1, S3GatherCompletions = 1
        };
        foreach (var name in new[] { "exchange-list", "exchange-buy", "mail-claim", "npc-shop-buy", "tower-entry", "gather-start" })
        {
            var tally = new ActionTally(); tally.Plan(); tally.Offer(); tally.Complete(1); report.Actions[name] = tally.Snapshot();
        }
        Assert.Empty(report.S3SetupFixtures);
        Assert.Equal("PASS", report.EvaluateLedgerRow().Status);
        report.UnsupportedBehaviours = ["missing workload"];
        Assert.Equal("NOT MEASURED", report.EvaluateLedgerRow().Status);
        report.UnsupportedBehaviours = [];
        report.S3CommittedLedger = null;
        Assert.Equal("NOT MEASURED", report.EvaluateLedgerRow().Status);
    }

    [Fact]
    public void Tower_end_callback_receives_embedded_reward_from_protocol_packet()
    {
        var reducer = new WorldReducer();
        ClimTowerEndNotify? received = null;
        reducer.OnTowerEnd = value => received = value;
        var packet = new ClimTowerEndNotify { Result = 0, ClimTowerResult = true, RewardNotify = new GetRewardNotify { TargetUid = 17 } };
        reducer.Observe(new BotEvent(BotEventKind.Game) { MsgId = TowerService.End, Body = packet.ToByteArray() });
        Assert.NotNull(received);
        Assert.True(received.ClimTowerResult);
        Assert.Equal(17, received.RewardNotify.TargetUid);
    }
}
