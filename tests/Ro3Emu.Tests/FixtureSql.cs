using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Tests;

/// <summary>Test fixture SQL admitted as a copied command on the database writer.</summary>
internal static class FixtureSql
{
    internal static int Execute(AccountStore accounts, string sql, params (string Name, object? Value)[] parameters) =>
        accounts.WriteSync<int>(new FixtureSqlPlan(sql, parameters));

    private sealed class FixtureSqlPlan : DbPlan
    {
        private readonly string _sql;
        private readonly (string Name, object Value)[] _parameters;

        internal FixtureSqlPlan(string sql, (string Name, object? Value)[] parameters)
        {
            _sql = sql;
            _parameters = [.. parameters.Select(p => (p.Name, Copy(p.Value)))];
        }

        internal override int Bytes => 128 + _sql.Length * sizeof(char) + _parameters.Sum(p =>
            p.Name.Length * sizeof(char) + (p.Value is byte[] bytes ? bytes.Length : 32));
        internal override string Label => "test/fixture-sql";
        // The test fixture intentionally injects raw SQL and failures into the writer.
        internal override bool AllowsUnversionedRoleMutation => true;

        internal override object Execute(SqliteConnection db, SqliteTransaction tx)
        {
            using var command = DbSql.Command(db, tx, _sql);
            foreach (var (name, value) in _parameters) command.Parameters.AddWithValue(name, value);
            return command.ExecuteNonQuery();
        }

        private static object Copy(object? value) => value switch
        {
            null => DBNull.Value,
            byte[] bytes => (byte[])bytes.Clone(),
            string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => value!,
            _ => throw new ArgumentException($"Unsupported fixture SQL parameter type: {value.GetType().FullName}")
        };
    }
}
