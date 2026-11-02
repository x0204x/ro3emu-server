using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ro3Emu.Server.Economy;

public sealed partial class EconomyMutation
{
    internal EconomyVigorRewardEvidence? VigorEvidence => EconomyVigorRewardEvidence.Read(outcome);

    internal static bool SupportsVigorReward(string command) =>
        IsRewardCommand(command) && command != "world-revive" || command == "cd13c/life" ||
        command.StartsWith("cd13c/life-", StringComparison.Ordinal) ||
        command is "cd13a/character-box" or "cd13a/character-use" or
            "cd13a/character-gm-grant" or "cd13a/character-starter-kit";

    internal static void ValidateVigorResult(string command, byte[] outcome,
        IEnumerable<EconomyQuantity> costs, IEnumerable<EconomyQuantity> goods, EconomyRewardEvidence? reward)
    {
        if (EconomyVigorRewardEvidence.Read(outcome) is null) return;
        if (!SupportsVigorReward(command) || costs.Concat(goods).Concat(reward?.OriginalGoods ?? [])
                .Concat(reward?.ConvertedInputs ?? []).Concat(reward?.ConvertedOutputs ?? [])
                .Any(q => q.Asset.Id == EconomyVigorRewardEvidence.RewardItem))
            throw new InvalidOperationException("活力證據必須來自已支援的來源，且不可同時宣告 407 貨幣異動。");
    }

    public EconomyMutation WithVigorEvidence(EconomyVigorRewardEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        evidence.Validate();
        if (VigorEvidence is { } existing)
            return existing == evidence ? this : throw new InvalidOperationException("活力獎勵證據不得重綁。");
        if (!SupportsVigorReward(CommandKind))
            throw new InvalidOperationException("此經濟來源未授權活力獎勵轉換。");
        static bool Vigor(EconomyQuantity q) => q.Asset == new EconomyAsset(EconomyAssetKind.Currency,
            EconomyVigorRewardEvidence.RewardItem);
        if (Costs.Any(q => q.Asset.Id == EconomyVigorRewardEvidence.RewardItem) ||
            Goods.Where(Vigor).Sum(q => q.Quantity) != evidence.Requested ||
            RewardEvidence is { } reward && (reward.ConvertedInputs ?? []).Concat(reward.ConvertedOutputs ?? []).Any(Vigor))
            throw new InvalidOperationException("407 的本次獎勵數量與活力轉入證據不符。");
        var result = JsonNode.Parse(outcome) as JsonObject
            ?? throw new InvalidOperationException("活力獎勵來源結果必須保留為 JSON 物件。");
        result.Add("VigorReward", JsonSerializer.SerializeToNode(evidence));
        var nextReward = RewardEvidence is { } original ? original with
        { OriginalGoods = (original.OriginalGoods ?? []).Where(q => !Vigor(q)).ToArray() } : null;
        return new EconomyMutation(OperationId, ActorAccount, ActorRoleId, CommandKind, RequestFingerprint,
            SourceKey, EffectiveUtcMs, Costs, [.. Goods.Where(q => !Vigor(q))],
            JsonSerializer.SerializeToUtf8Bytes(result), nextReward, ProgressionCatalog, PlanFingerprint);
    }
}
