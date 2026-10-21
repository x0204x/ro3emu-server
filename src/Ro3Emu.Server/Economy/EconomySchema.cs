using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Economy;

/// <summary>
/// The economy readers run against databases of any age. SQLite reports a missing table or column only as the generic
/// SQLITE_ERROR with a message, so the readers check the schema before a query instead of matching that message.
/// </summary>
internal static class EconomySchema
{
    /// <summary>True when the table exists and has every one of the columns.</summary>
    internal static bool Has(SqliteConnection db, SqliteTransaction tx, string table, params string[] columns)
    {
        using var command = EconomyLedgerReader.Command(db, tx, "SELECT name FROM pragma_table_info($table)");
        command.Parameters.AddWithValue("$table", table);
        using var rows = command.ExecuteReader();
        var present = new HashSet<string>(StringComparer.Ordinal);
        while (rows.Read()) present.Add(rows.GetString(0));
        return present.Count > 0 && columns.All(present.Contains);
    }

    /// <summary>Throws <see cref="EconomySchemaMissingException"/> unless the table has every column.</summary>
    internal static void Require(SqliteConnection db, SqliteTransaction tx, string table, params string[] columns)
    {
        if (!Has(db, tx, table, columns)) throw new EconomySchemaMissingException(table);
    }
}

/// <summary>A table or column an economy reader needs is absent from the database it reads.</summary>
internal sealed class EconomySchemaMissingException(string table)
    : Exception($"Economy table {table} or one of its required columns is absent.")
{
    public string Table { get; } = table;
}
