using System.Security.Cryptography;
using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

public sealed partial class CharacterService
{
    /// <summary>E05c: capture the family's resolved payment/result before writer admission. The original wire
    /// has no retry token; each server-admitted intent gets one source, retained across receipt recovery.</summary>
    internal EconomyMutation ProgressionEconomy(GameSession session, string kind,
        IReadOnlyList<(int Entry, long Count)> costs, IReadOnlyList<(int Entry, int Count)> goods, object outcome, string? sourceKey = null)
    {
        if (!EconomyMutation.IsProgressionKind(kind))
            throw new ArgumentException("Expected a C/D 13c family command.", nameof(kind));
        EconomyAsset Asset(int entry) => data.Row("ItemConfig", entry) is { } row
            ? new(GameData.Int(row, "_iType") == ItemTypeCurrency ? EconomyAssetKind.Currency : EconomyAssetKind.Item, entry)
            : throw new InvalidOperationException("Missing family economy item configuration: " + entry);
        byte[] resolved = JsonSerializer.SerializeToUtf8Bytes(outcome);
        string fingerprint = Convert.ToHexString(SHA256.HashData(resolved));
        // Large protobuf projections are already durable in the child blob. Keep a bounded, verifiable receipt.
        byte[] receipt = resolved.Length <= 15 * 1024 ? resolved
            : JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, ResultHash = fingerprint, ResultBytes = resolved.Length });
        string operation = Guid.NewGuid().ToString("N");
        return new EconomyMutation(operation, session.Account!, session.Role!.Id, kind, fingerprint,
            sourceKey ?? kind + "/" + session.Role.Id + "/" + operation, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            [.. costs.Where(x => x.Count > 0).GroupBy(x => x.Entry).Select(g => new EconomyQuantity(Asset(g.Key), g.Sum(x => x.Count)))],
            [.. goods.Where(x => x.Count > 0).GroupBy(x => x.Entry).Select(g => new EconomyQuantity(Asset(g.Key), g.Sum(x => (long)x.Count)))], receipt,
            progressionCatalog: kind == "cd13c/season-reissue" ? EventEconomy.Catalog(data) : null);
    }
}
