using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>C/D command acknowledgement journal, deliberately separate from the stage-E asset ledger.
/// A writer receipt survives a lost acknowledgement; ordinary repeated protocol requests have new IDs.</summary>
internal static class OperationReceipts
{
    private sealed record Value(string Kind, JsonElement Data);
    private static Value Encode(object? value) => value switch
    {
        null => new("null", JsonSerializer.SerializeToElement(0)),
        bool x => new("bool", JsonSerializer.SerializeToElement(x)),
        int x => new("int", JsonSerializer.SerializeToElement(x)),
        long x => new("long", JsonSerializer.SerializeToElement(x)),
        string x => new("string", JsonSerializer.SerializeToElement(x)),
        long[] x => new("ids", JsonSerializer.SerializeToElement(x)),
        byte[] x => new("bytes", JsonSerializer.SerializeToElement(x)),
        AuctionItemWriteReceipt x => new("auction", JsonSerializer.SerializeToElement(x)),
        FriendStore.ChatGroup x => new("friend-group", JsonSerializer.SerializeToElement(x)),
        object?[] x => new("parts", JsonSerializer.SerializeToElement(x.Select(Encode).ToArray())),
        _ => throw new InvalidOperationException($"Unsupported owner receipt {value.GetType().Name}."),
    };
    private static object? Decode(Value value) => value.Kind switch
    {
        "null" => null,
        "bool" => value.Data.GetBoolean(),
        "int" => value.Data.GetInt32(),
        "long" => value.Data.GetInt64(),
        "string" => value.Data.GetString(),
        "ids" => value.Data.Deserialize<long[]>(),
        "bytes" => value.Data.Deserialize<byte[]>(),
        "auction" => value.Data.Deserialize<AuctionItemWriteReceipt>(),
        "friend-group" => value.Data.Deserialize<FriendStore.ChatGroup>(),
        "parts" => value.Data.Deserialize<Value[]>()!.Select(Decode).ToArray(),
        _ => throw new InvalidOperationException("Unknown owner receipt format."),
    };
    internal static bool TryRead(SqliteConnection db, SqliteTransaction? tx, string id, out object? result)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT result FROM owner_operation_receipt WHERE operation_id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        if (cmd.ExecuteScalar() is not byte[] bytes) { result = null; return false; }
        result = Decode(JsonSerializer.Deserialize<Value>(bytes)!); return true;
    }
    internal static void Save(SqliteConnection db, SqliteTransaction tx, DbPlan plan, object? result)
    {
        using var cmd = DbSql.Command(db, tx,
            "INSERT INTO owner_operation_receipt(operation_id,family,result) VALUES($id,$family,$result)");
        cmd.Parameters.AddWithValue("$id", plan.OperationId!);
        cmd.Parameters.AddWithValue("$family", plan.Label);
        cmd.Parameters.AddWithValue("$result", JsonSerializer.SerializeToUtf8Bytes(Encode(result)));
        cmd.ExecuteNonQuery();
    }
}

internal sealed class OwnerOutcomeUnknownException(string operationId, Exception cause)
    : Exception($"Owner operation {operationId} requires recovery; commit outcome could not be resolved.", cause);
