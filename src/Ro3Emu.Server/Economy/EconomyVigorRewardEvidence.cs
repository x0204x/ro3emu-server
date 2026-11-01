using System.Text.Json;

namespace Ro3Emu.Server.Economy;

/// <summary>407 獎勵轉入限制 1004 的有界證據；保留同交易生活消耗前後的兩個基準。</summary>
public sealed record EconomyVigorRewardEvidence(long Requested, long Accepted, long Before,
    long BeforeReward, long After, string BeforeRestrictSha256, string RestrictSha256)
{
    public const int RewardItem = 407;
    public const int Restriction = 1004;
    public void Validate()
    {
        static bool Hash(string value) => value is { Length: 64 } &&
            value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
        if (Requested <= 0 || Accepted < 0 || Accepted > Requested || Before < 0 || BeforeReward < 0 ||
            BeforeReward > Before || After < 0 || (System.Numerics.BigInteger)BeforeReward + Accepted != After ||
            !Hash(BeforeRestrictSha256) || !Hash(RestrictSha256))
            throw new InvalidOperationException("活力獎勵的額度或限制快照證據無效。");
    }

    internal static EconomyVigorRewardEvidence? Read(byte[] outcome)
    {
        // 舊收據可能是非 JSON 的回傳資料；只對明確宣告的新欄位啟用此契約。
        JsonDocument document;
        try { document = JsonDocument.Parse(outcome); }
        catch (JsonException) { return null; }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("VigorReward", out var value)) return null;
            var evidence = value.Deserialize<EconomyVigorRewardEvidence>()
                ?? throw new InvalidDataException("活力獎勵證據不可為空。");
            evidence.Validate();
            return evidence;
        }
    }
}
