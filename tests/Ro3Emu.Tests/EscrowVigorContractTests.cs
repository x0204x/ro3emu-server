using Ro3Emu.Server.Economy;

namespace Ro3Emu.Tests;

/// <summary>只測郵件活力轉換的授權邊界；完整領信流程另由 DailyTests 的實際交易涵蓋。</summary>
public sealed class EscrowVigorContractTests
{
    private static EconomyVigorRewardEvidence Evidence => new(200, 200, 500, 500, 700,
        new string('A', 64), new string('B', 64));
    private static EscrowExternalChange Conversion => new(EconomyLegKind.Sink,
        new(EconomyAssetKind.Currency, 407), 200, EconomyReason.VigorRewardConversion,
        EconomyCompartment.Mail, new(EconomyOwnerKind.Role, "12"));

    [Fact]
    public void ExistingEscrowPayloadKeepsItsOriginalFieldsWithoutNullVigorProperty()
    {
        var intent = new EscrowEconomyIntent("mail/claim", "fixture", 12).BindCopy();
        intent.BindChildRoleWrites([[new(12, "mailbox")]]);
        using var document = System.Text.Json.JsonDocument.Parse(intent.Payload);
        Assert.Equal(new[] { "OperationId", "CommandKind", "ActorAccount", "ActorRoleId", "SourceKey",
            "EffectiveUtcMs", "TransferReason", "Changes", "Realizations", "Evidence", "ChildRoleWrites" },
            document.RootElement.EnumerateObject().Select(x => x.Name).ToArray());
        Assert.Null(EconomyVigorRewardEvidence.Read(intent.Payload));
    }

    [Theory]
    [InlineData("market/buy")]
    [InlineData("mail/send")]
    [InlineData("e03/test-transfer")]
    public void OtherCommandsCannotAddMailVigorConversion(string command)
    {
        var intent = new EscrowEconomyIntent(command, "fixture", 12);
        Assert.Throws<InvalidOperationException>(() => intent.WithVigorEvidence(Evidence));
    }

    [Fact]
    public void MailConversionBindsEvidenceAndCannotBeAddedTwice()
    {
        var intent = new EscrowEconomyIntent("mail/claim", "fixture", 12).WithVigorEvidence(Evidence);
        intent.BindChildRoleWrites([[new(12, "mailbox")], [new(12, "life_restrict")]]);
        EscrowEconomyIntent.ValidateVigorContract(intent.CommandKind, intent.ActorRoleId,
            intent.VigorReward, intent.Changes, intent.ChildRoleWrites);
        Assert.Equal(Evidence, EconomyVigorRewardEvidence.Read(intent.Payload));
        Assert.Equal(Conversion, Assert.Single(intent.Changes));
        Assert.Throws<InvalidOperationException>(() => intent.WithVigorEvidence(Evidence));
    }

    [Theory]
    [InlineData("amount")]
    [InlineData("owner")]
    [InlineData("compartment")]
    [InlineData("denomination")]
    [InlineData("source")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    public void ConversionCannotConsumeOtherHoldingsOrAChangedAmount(string fault)
    {
        var conversion = Conversion;
        conversion = fault switch
        {
            "amount" => conversion with { Quantity = 201 },
            "owner" => conversion with { Owner = new(EconomyOwnerKind.Role, "13") },
            "compartment" => conversion with { Compartment = EconomyCompartment.Currency },
            "denomination" => conversion with { Asset = new(EconomyAssetKind.Currency, 1) },
            "source" => conversion with { Kind = EconomyLegKind.Source },
            _ => conversion
        };
        EscrowExternalChange[] changes = fault == "duplicate" ? [conversion, conversion]
            : fault == "missing" ? [] : [conversion];
        Assert.Throws<InvalidDataException>(() => EscrowEconomyIntent.ValidateVigorContract(
            "mail/claim", 12, Evidence, changes, [[new EscrowRolePart(12, "life_restrict")]]));
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    public void ConversionRequiresExactlyOneRestrictionWriteForItsRecipient(string fault)
    {
        EscrowRolePart[][] writes = fault switch
        {
            "foreign" => [[new(13, "life_restrict")]],
            "missing" => [[new(12, "mailbox")]],
            _ => [[new(12, "life_restrict")], [new(12, "life_restrict")]]
        };
        Assert.Throws<InvalidDataException>(() => EscrowEconomyIntent.ValidateVigorContract(
            "mail/claim", 12, Evidence, [Conversion], writes));
    }

    [Fact]
    public void MissingEvidenceCannotAuthorizeTheNewSinkOrRestrictionBlob()
    {
        Assert.Throws<InvalidDataException>(() => EscrowEconomyIntent.ValidateVigorContract(
            "mail/claim", 12, null, [Conversion], []));
        Assert.Throws<InvalidDataException>(() => EscrowEconomyIntent.ValidateVigorContract(
            "mail/claim", 12, null, [], [[new EscrowRolePart(12, "life_restrict")]]));
    }
}
