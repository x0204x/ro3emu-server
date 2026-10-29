namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>
/// Compares a committed <c>operation_receipt</c> with the intent being replayed or resolved.
/// - Intent v2 binds the outcome by its SHA-256 instead of embedding it a second time in base64; a receipt written
///   before that holds the v1 payload, so the intent's legacy form is accepted too.
/// - A receipt compacted by <see cref="ReceiptRetention"/> holds <see cref="CompactedPayload"/> instead of its payload
///   and an empty outcome. Its intent hash, which covers the outcome, then decides alone. The marker cannot be a real
///   payload: every intent is a JSON object.
/// </summary>
internal static class ReceiptBlobs
{
    internal static readonly byte[] CompactedPayload = "compacted"u8.ToArray();

    internal static bool IsCompacted(byte[] payload) => payload.AsSpan().SequenceEqual(CompactedPayload);

    /// <summary>The stored intent is this one (v2 or its v1 form), whole or compacted.</summary>
    internal static bool SameIntent(string storedHash, byte[] storedPayload, string hash, byte[] payload,
        Func<(string Hash, byte[] Payload)> legacy)
    {
        bool compacted = IsCompacted(storedPayload);
        if (storedHash == hash) return compacted || storedPayload.AsSpan().SequenceEqual(payload);
        var (legacyHash, legacyPayload) = legacy();
        return storedHash == legacyHash && (compacted || storedPayload.AsSpan().SequenceEqual(legacyPayload));
    }

    /// <summary>A stored payload that never changes format (E03 escrow): equal bytes or compacted.</summary>
    internal static bool SamePayload(byte[] storedPayload, byte[] payload) =>
        IsCompacted(storedPayload) || storedPayload.AsSpan().SequenceEqual(payload);

    /// <summary>The stored outcome equals <paramref name="expected"/>, or the receipt was compacted (outcome emptied).</summary>
    internal static bool SameOutcome(byte[] storedOutcome, byte[] storedPayload, byte[] expected) =>
        IsCompacted(storedPayload) ? storedOutcome.Length == 0 : storedOutcome.AsSpan().SequenceEqual(expected);
}
