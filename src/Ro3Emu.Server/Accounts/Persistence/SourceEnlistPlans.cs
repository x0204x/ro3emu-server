using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>The festival source claim is guarded in the same transaction as the Character award.</summary>
internal sealed class FestivalClaimPlan(long cycle, long role, long source) : DbPlan
{
    internal override int Bytes => 128;
    internal override string Label => "festival/claim";
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites => [RevisionStore.Role(role, RevisionStore.FestivalReward)];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx,
            "UPDATE festival_reward SET claimed=1 WHERE cycle=$c AND role_id=$r AND source=$s AND claimed=0");
        cmd.Parameters.AddWithValue("$c", cycle); cmd.Parameters.AddWithValue("$r", role);
        cmd.Parameters.AddWithValue("$s", source);
        if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("Festival reward was already settled.");
        return true;
    }
}

internal sealed class GuildCampfireStatePlan : DbPlan
{
    private readonly long _guild;
    private readonly byte[] _state;
    internal GuildCampfireStatePlan(long guild, byte[] state)
    {
        _guild = guild; _state = (byte[])state.Clone();
    }
    internal override int Bytes => 128 + _state.Length;
    internal override string Label => "guild_campfire/save";
    internal override IReadOnlyList<OwnerRevisionKey> EntityWrites => [RevisionStore.Entity("guild_campfire", _guild)];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx,
            "INSERT INTO guild_campfire(guild_id,state) VALUES($guild,$state) ON CONFLICT(guild_id) DO UPDATE SET state=excluded.state");
        cmd.Parameters.AddWithValue("$guild", _guild); cmd.Parameters.AddWithValue("$state", _state);
        return cmd.ExecuteNonQuery();
    }
}

internal sealed class ActivityEveGlobalPlan : DbPlan
{
    private readonly long _activity, _cycle;
    private readonly byte[] _blob;
    internal ActivityEveGlobalPlan(long activity, long cycle, byte[] blob)
    {
        _activity = activity; _cycle = cycle; _blob = (byte[])blob.Clone();
    }
    internal override int Bytes => 128 + _blob.Length;
    internal override string Label => "activity/eve-global";
    internal override IReadOnlyList<OwnerRevisionKey> EntityWrites =>
        [RevisionStore.Entity("activity_eve_global", $"{_activity}:{_cycle}")];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, """
            INSERT INTO activity_eve_global(activity_id,cycle_start,blob) VALUES($activity,$cycle,$blob)
            ON CONFLICT(activity_id,cycle_start) DO UPDATE SET blob=excluded.blob
            """);
        cmd.Parameters.AddWithValue("$activity", _activity); cmd.Parameters.AddWithValue("$cycle", _cycle);
        cmd.Parameters.AddWithValue("$blob", _blob);
        return cmd.ExecuteNonQuery();
    }
}

internal sealed class ActivityPreDonationPlan(long activity, long cycle, long area, long day,
    long role, long score, long amount) : DbPlan
{
    internal override int Bytes => 128;
    internal override string Label => "activity/pre-donation";
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites => [RevisionStore.Role(role, RevisionStore.ActivityDonation)];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, """
            INSERT INTO activity_pre_progress(activity_id,cycle_start,area,score)
            VALUES($activity,$cycle,$area,$score)
            ON CONFLICT(activity_id,cycle_start,area) DO UPDATE SET score=score+excluded.score;
            INSERT INTO activity_pre_donation_area(activity_id,cycle_start,area,day,role_id,amount)
            VALUES($activity,$cycle,$area,$day,$role,$amount)
            ON CONFLICT(activity_id,cycle_start,area,day,role_id) DO UPDATE SET amount=amount+excluded.amount
            """);
        cmd.Parameters.AddWithValue("$activity", activity); cmd.Parameters.AddWithValue("$cycle", cycle);
        cmd.Parameters.AddWithValue("$area", area); cmd.Parameters.AddWithValue("$day", day);
        cmd.Parameters.AddWithValue("$role", role); cmd.Parameters.AddWithValue("$score", score);
        cmd.Parameters.AddWithValue("$amount", amount);
        return cmd.ExecuteNonQuery();
    }
}

internal sealed class GuildChallengeClearPlan : DbPlan
{
    private readonly string _id;
    private readonly long _guild, _cycle;
    private readonly int _season, _difficulty;
    private readonly bool _pending;
    private readonly byte[] _state;
    internal GuildChallengeClearPlan(Ro3Emu.Server.Game.GuildChallengeService.Clear clear)
    {
        _id = clear.Id; _guild = clear.Guild; _cycle = clear.Cycle;
        _season = clear.Season; _difficulty = clear.Difficulty;
        _pending = !clear.AuctionPublished || clear.Delivered.Count < clear.Personal.Count;
        _state = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(clear);
    }
    internal override int Bytes => 128 + _id.Length * 2 + _state.Length;
    internal override string Label => "guild_challenge/clear";
    internal override IReadOnlyList<OwnerRevisionKey> EntityWrites => [RevisionStore.Entity("guild_challenge_clear", _id)];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, """
            INSERT INTO guild_challenge_clear(id,guild_id,season,cycle,difficulty,pending,state)
            VALUES($id,$guild,$season,$cycle,$difficulty,$pending,$state)
            ON CONFLICT(id) DO UPDATE SET pending=excluded.pending,state=excluded.state
            """);
        cmd.Parameters.AddWithValue("$id", _id); cmd.Parameters.AddWithValue("$guild", _guild);
        cmd.Parameters.AddWithValue("$season", _season); cmd.Parameters.AddWithValue("$cycle", _cycle);
        cmd.Parameters.AddWithValue("$difficulty", _difficulty); cmd.Parameters.AddWithValue("$pending", _pending);
        cmd.Parameters.AddWithValue("$state", _state);
        return cmd.ExecuteNonQuery();
    }
}

internal enum RealmStateKind { TerritoryWars, TerritoryEscort, GuildLeague, Tavern }

internal sealed class RealmStatePlan : DbPlan
{
    private readonly RealmStateKind _kind;
    private readonly byte[] _state;
    internal RealmStatePlan(RealmStateKind kind, byte[] state)
    {
        _kind = kind; _state = (byte[])state.Clone();
    }
    internal override int Bytes => 128 + _state.Length;
    internal override string Label => "realm/" + _kind;
    internal override IReadOnlyList<OwnerRevisionKey> EntityWrites => [RevisionStore.Entity("realm", _kind)];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        string table = _kind switch
        {
            RealmStateKind.TerritoryWars => "territory_wars",
            RealmStateKind.TerritoryEscort => "territory_escort",
            RealmStateKind.GuildLeague => "guild_league",
            RealmStateKind.Tavern => "tavern",
            _ => throw new ArgumentOutOfRangeException()
        };
        using var cmd = DbSql.Command(db, tx,
            $"INSERT INTO {table}(id,state) VALUES(1,$state) ON CONFLICT(id) DO UPDATE SET state=excluded.state");
        cmd.Parameters.AddWithValue("$state", _state);
        return cmd.ExecuteNonQuery();
    }
}
