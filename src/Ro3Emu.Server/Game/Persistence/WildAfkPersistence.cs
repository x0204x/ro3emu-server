using System.Text.Json;
using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Server.Game.Persistence;

/// <summary>
/// The running Bonus Time clock is a lazy part (see <see cref="LazyRolePart"/>): while it runs, the saved state changes every
/// second, and a durable commit per player per second stalled the loop. It is written at most 30 s after the first unsaved
/// change (inferred; server tuning), so a crash can hand back up to that much consumed time. Every other AFK save (toggles,
/// kills, the daily reset, leaving the world) stays synchronous and acts as a barrier.
/// </summary>
public sealed class WildAfkPersistence : LazyRolePart
{
    private readonly RoleStore _roles;

    public WildAfkPersistence(RoleStore roles, GameLoop loop, ILogger<WildAfkPersistence> log) : base(loop, roles, log)
    {
        this._roles = roles;
        Interval = TimeSpan.FromSeconds(30);
    }

    protected override string RevisionPart => "wild_afk";
    protected override State? Get(GameSession s) => s.WildAfkPart;
    protected override void Set(GameSession s, State state) => s.WildAfkPart = state;
    protected override bool Eligible(GameSession s) => s.Role != null && s.WildAfk != null;

    protected override Func<long, long, long, Task<bool>> Capture(GameSession s)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(s.WildAfk!);
        return (role, epoch, revision) => _roles.SaveBlobLazyAsync(role, epoch, revision, "wild_afk", bytes);
    }
}
