using System.Text.Json;
using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Server.Economy;

/// <summary>The offline checker validates the event intent independently of writer-side delta construction.</summary>
internal static class EventEconomyEvidence
{
    internal static EventEconomyMutation ReadIntent(EconomyCatalog catalog, byte[] payload, string hash,
        string operation, string account, long actor, string command, string fingerprint, string source,
        long effective, byte[] outcome)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        int version = root.GetProperty("Version").GetInt32();
        if (version is not (1 or 2) || root.GetProperty("CatalogVersion").GetString() != catalog.DenominationsVersion)
            throw new InvalidDataException("Unsupported event intent/catalog version.");
        var result = new EventEconomyMutation(catalog, operation, account, actor, command, source, effective,
            root.GetProperty("Costs").Deserialize<EconomyQuantity[]>()!,
            root.GetProperty("Goods").Deserialize<EconomyQuantity[]>()!,
            root.GetProperty("OwnerDeltas").Deserialize<EventEconomyOwnerDelta[]>()!, outcome,
            root.GetProperty("SourceReason").Deserialize<EconomyReason>(),
            root.GetProperty("SinkReason").Deserialize<EconomyReason>(),
            root.GetProperty("TransferReason").Deserialize<EconomyReason>(), root.GetProperty("ChildPlanHash").GetString(),
            version);
        if (result.ChildPlanHash is null) throw new InvalidDataException("Event receipt has no detached plan fingerprint.");
        if (result.RequestFingerprint != fingerprint || result.IntentHash != hash ||
            !result.IntentPayload.AsSpan().SequenceEqual(payload))
            throw new InvalidDataException("Event receipt differs from canonical frozen intent.");
        return result;
    }

    internal static void ValidateRevisions(string beforeJson, string afterJson, long actor)
    {
        using var before = JsonDocument.Parse(beforeJson);
        using var after = JsonDocument.Parse(afterJson);
        static Dictionary<(long Role, string Part), (long Epoch, long Revision)> Roles(JsonElement root) =>
            root.GetProperty("Roles").EnumerateArray().ToDictionary(
                value => (value.GetProperty("RoleId").GetInt64(), value.GetProperty("Part").GetString()!),
                value => (value.GetProperty("Epoch").GetInt64(), value.GetProperty("Revision").GetInt64()));
        var b = Roles(before.RootElement); var a = Roles(after.RootElement);
        static Dictionary<long, long> Pins(JsonElement root) => root.GetProperty("Pins").EnumerateArray()
            .ToDictionary(value => value.GetProperty("RoleId").GetInt64(), value => value.GetProperty("Epoch").GetInt64());
        var pb = Pins(before.RootElement); var pa = Pins(after.RootElement);
        if (actor > 0 && !pb.ContainsKey(actor) || pb.Count != pa.Count ||
            pb.Any(p => p.Key <= 0 || p.Value <= 0 || !pa.TryGetValue(p.Key, out long epoch) || epoch != p.Value))
            throw new InvalidDataException("Invalid event lifecycle pin evidence.");
        if (a.Count != b.Count || b.Any(p =>
            p.Key.Role <= 0 || string.IsNullOrWhiteSpace(p.Key.Part) || p.Value.Epoch <= 0 || p.Value.Revision < 0 ||
            !a.TryGetValue(p.Key, out var value) || value.Epoch != p.Value.Epoch ||
            value.Revision < p.Value.Revision || value.Revision > checked(p.Value.Revision + 1)))
            throw new InvalidDataException("Invalid event role revision evidence.");
        var eb = before.RootElement.GetProperty("Entities").Deserialize<OwnerRevisionValue[]>()!.ToDictionary(v => v.Key);
        var ea = after.RootElement.GetProperty("Entities").Deserialize<OwnerRevisionValue[]>()!.ToDictionary(v => v.Key);
        if (eb.Count != ea.Count || eb.Any(p => p.Value.Revision < 0 ||
            !ea.TryGetValue(p.Key, out var value) || p.Value.Exists && !value.Exists ||
            value.Revision < p.Value.Revision || value.Revision > checked(p.Value.Revision + 1)))
            throw new InvalidDataException("Invalid event entity revision evidence.");
    }
}
