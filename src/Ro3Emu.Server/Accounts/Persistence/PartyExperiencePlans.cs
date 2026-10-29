using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>
/// Durable EXP shares frozen by an ordinary field kill.  The kill transaction creates one row for every
/// eligible party member except the killer; delivery advances that role and marks the row in one transaction.
/// </summary>
internal sealed class PartyExperienceSchemaPlan : DbPlan
{
    internal override int Bytes => 256;
    internal override string Label => "party-experience/schema";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        SchemaSql.Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS party_experience_obligation (
                source_key TEXT PRIMARY KEY, role_id INTEGER NOT NULL,
                base_exp INTEGER NOT NULL CHECK(base_exp >= 0),
                job_exp INTEGER NOT NULL CHECK(job_exp >= 0),
                delivered INTEGER NOT NULL DEFAULT 0 CHECK(delivered IN (0,1)));
            CREATE INDEX IF NOT EXISTS party_experience_pending_role
                ON party_experience_obligation(role_id,delivered);
            INSERT OR IGNORE INTO owner_revision(kind,id,part,revision)
                SELECT 'party_experience_obligation',source_key,'state',0 FROM party_experience_obligation;
            """);
        return 1;
    }
}

/// <summary>One frozen party EXP share.  Its source is derived from the monster kill source and beneficiary role.</summary>
internal sealed record PartyExperienceObligationRow(string SourceKey, long RoleId, long BaseExp, long JobExp)
    : IRoleObligationRow<PartyExperienceObligationRow>
{
    public static RoleObligationTable Table { get; } = new("party_experience_obligation",
        "party_experience_obligation", "role_id", ["source_key", "role_id", "base_exp", "job_exp"],
        "source_key", "party-experience", "party-experience/read-pending");

    public static PartyExperienceObligationRow Read(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));

    public object[] Values() => [SourceKey, RoleId, BaseExp, JobExp];
    public PartyExperienceObligationRow Detach() => this;
    public bool SameFrozen(PartyExperienceObligationRow stored) =>
        stored.RoleId == RoleId && stored.BaseExp == BaseExp && stored.JobExp == JobExp;
}
