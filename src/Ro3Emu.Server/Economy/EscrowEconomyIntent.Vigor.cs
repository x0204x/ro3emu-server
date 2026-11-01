using System.Globalization;
using System.Text.Json;

namespace Ro3Emu.Server.Economy;

public sealed partial class EscrowEconomyIntent
{
    internal EconomyVigorRewardEvidence? VigorReward { get; private set; }

    internal EscrowEconomyIntent WithVigorEvidence(EconomyVigorRewardEvidence value)
    {
        value.Validate();
        if (CommandKind != "mail/claim" || ActorRoleId <= 0 || VigorReward is not null ||
            value.BeforeReward != value.Before)
            throw new InvalidOperationException("僅角色本人領取郵件可轉入活力，且不能合併活力消耗。");
        var copy = BindCopy();
        copy.VigorReward = value;
        copy.changes.Add(new(EconomyLegKind.Sink,
            new(EconomyAssetKind.Currency, EconomyVigorRewardEvidence.RewardItem), value.Requested,
            EconomyReason.VigorRewardConversion, EconomyCompartment.Mail,
            new(EconomyOwnerKind.Role, ActorRoleId.ToString(CultureInfo.InvariantCulture))));
        return copy;
    }

    // 未含活力轉換的既有意圖維持原始序列化格式，避免改變舊操作的重送雜湊。
    private byte[] VigorPayload => JsonSerializer.SerializeToUtf8Bytes(new
    {
        OperationId, CommandKind, ActorAccount, ActorRoleId, SourceKey, EffectiveUtcMs,
        TransferReason, Changes = changes, Realizations = realizations, Evidence = evidence,
        ChildRoleWrites = ChildRoleWrites, VigorReward
    });

    internal static void ValidateVigorContract(string command, long actor,
        EconomyVigorRewardEvidence? value, IEnumerable<EscrowExternalChange> changes,
        IEnumerable<IEnumerable<EscrowRolePart>> writes)
    {
        var conversions = changes.Where(x => x.Reason == EconomyReason.VigorRewardConversion).ToArray();
        var restrictions = writes.SelectMany(x => x).Where(x => x.Part == "life_restrict").ToArray();
        if (value is null)
        {
            if (conversions.Length != 0 || restrictions.Length != 0)
                throw new InvalidDataException("郵件活力轉換缺少限制快照證據。");
            return;
        }
        value.Validate();
        if (command != "mail/claim" || actor <= 0 || value.BeforeReward != value.Before ||
            restrictions.Length != 1 || restrictions[0].RoleId != actor || conversions.Length != 1 ||
            conversions[0] != new EscrowExternalChange(EconomyLegKind.Sink,
                new(EconomyAssetKind.Currency, EconomyVigorRewardEvidence.RewardItem), value.Requested,
                EconomyReason.VigorRewardConversion, EconomyCompartment.Mail,
                new(EconomyOwnerKind.Role, actor.ToString(CultureInfo.InvariantCulture))))
            throw new InvalidDataException("郵件活力轉換的角色、來源或額度不符。");
    }
}
