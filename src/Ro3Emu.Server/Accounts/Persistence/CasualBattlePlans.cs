using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>
/// Mock Battle payout obligations (CasualBattleService): one row per role that stayed to the end of a match, frozen
/// before the match counts as settled. The payout (rating, record, numbers and the DropPools to roll) is paid with
/// the role's timed_battle state in one transaction that also marks the row delivered.
/// </summary>
internal sealed class CasualBattleSchemaPlan : DbPlan
{
    internal override int Bytes => 256;
    internal override string Label => "casual-battle/schema";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        SchemaSql.Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS casual_battle_obligation (
                source_key TEXT PRIMARY KEY, match_id INTEGER NOT NULL, role_id INTEGER NOT NULL,
                payout BLOB NOT NULL,
                delivered INTEGER NOT NULL DEFAULT 0 CHECK(delivered IN (0,1)));
            CREATE INDEX IF NOT EXISTS casual_battle_pending_role
                ON casual_battle_obligation(role_id,delivered);
            INSERT OR IGNORE INTO owner_revision(kind,id,part,revision)
                SELECT 'casual_battle_obligation',source_key,'state',0 FROM casual_battle_obligation;
            """);
        return 1;
    }
}

/// <summary>One casual_battle_obligation row; the shared RoleObligation plans read, insert and mark it.</summary>
internal sealed record CasualBattleObligationRow(string SourceKey, long MatchId, long RoleId, byte[] Payout)
    : IRoleObligationRow<CasualBattleObligationRow>
{
    public static RoleObligationTable Table { get; } = new("casual_battle_obligation", "casual_battle_obligation",
        "role_id", ["source_key", "match_id", "role_id", "payout"], "match_id,source_key", "casual-battle",
        "casual-battle/read-obligations", ScopeColumn: "match_id");
    public static CasualBattleObligationRow Read(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), (byte[])reader.GetValue(3));
    public object[] Values() => [SourceKey, MatchId, RoleId, Payout];
    public CasualBattleObligationRow Detach() => this with { Payout = (byte[])Payout.Clone() };
    public bool SameFrozen(CasualBattleObligationRow stored) =>
        stored.RoleId == RoleId && stored.Payout.AsSpan().SequenceEqual(Payout);
}
