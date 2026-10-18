using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

internal sealed class TimedBattleSchemaPlan : DbPlan
{
    internal override int Bytes => 256;
    internal override string Label => "timed-battle/schema";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        SchemaSql.Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS timed_battle_obligation (
                source_key TEXT PRIMARY KEY, match_id INTEGER NOT NULL, entry INTEGER NOT NULL,
                role_id INTEGER NOT NULL, grants BLOB NOT NULL, costs BLOB NOT NULL,
                next_state BLOB NOT NULL, result BLOB NOT NULL,
                delivered INTEGER NOT NULL DEFAULT 0 CHECK(delivered IN (0,1)));
            CREATE INDEX IF NOT EXISTS timed_battle_pending_role
                ON timed_battle_obligation(role_id,delivered);
            INSERT OR IGNORE INTO owner_revision(kind,id,part,revision)
                SELECT 'timed_battle_obligation',source_key,'state',0 FROM timed_battle_obligation;
            """);
        return 1;
    }
}

/// <summary>One timed_battle_obligation row: the grants a Survivor / Dragon Island / Rift settlement froze for a role
/// (minted by the settlement's realm receipt), with the state and result it published. The shared RoleObligation plans
/// read, insert and mark it.</summary>
internal sealed record TimedBattleObligationRow(string SourceKey, long MatchId, int Entry, long RoleId,
    byte[] Grants, byte[] Costs, byte[] NextState, byte[] Result) : IRoleObligationRow<TimedBattleObligationRow>
{
    public static RoleObligationTable Table { get; } = new("timed_battle_obligation", "timed_battle_obligation",
        "role_id", ["source_key", "match_id", "entry", "role_id", "grants", "costs", "next_state", "result"],
        "match_id,source_key", "timed-battle", "timed-battle/read-pending", ScopeColumn: "match_id");
    public static TimedBattleObligationRow Read(SqliteDataReader reader) => new(reader.GetString(0), reader.GetInt64(1),
        reader.GetInt32(2), reader.GetInt64(3), (byte[])reader.GetValue(4), (byte[])reader.GetValue(5),
        (byte[])reader.GetValue(6), (byte[])reader.GetValue(7));
    public object[] Values() => [SourceKey, MatchId, Entry, RoleId, Grants, Costs, NextState, Result];
    public TimedBattleObligationRow Detach() => this with
    {
        Grants = (byte[])Grants.Clone(), Costs = (byte[])Costs.Clone(),
        NextState = (byte[])NextState.Clone(), Result = (byte[])Result.Clone(),
    };
    public bool SameFrozen(TimedBattleObligationRow stored) => stored.RoleId == RoleId && stored.MatchId == MatchId
        && stored.Entry == Entry && stored.Grants.AsSpan().SequenceEqual(Grants) && stored.Costs.AsSpan().SequenceEqual(Costs)
        && stored.NextState.AsSpan().SequenceEqual(NextState) && stored.Result.AsSpan().SequenceEqual(Result);
}
