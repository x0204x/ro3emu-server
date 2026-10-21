using Google.Protobuf;
using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Server.Game.Persistence;

/// <summary>
/// C/D 17: the role's client settings (DBSettingInfo) are kept on the session and written lazily (see
/// <see cref="LazyRolePart"/>), within one second of the first unsaved change. A settings change coupled to AFK
/// accounting (SettingType.WildAfk) stays durable at once together with its reconciliation; leaving the world
/// flushes synchronously.
/// </summary>
public sealed class SettingsPersistence : LazyRolePart
{
    private readonly RoleStore _roles;

    public SettingsPersistence(RoleStore roles, GameLoop loop, ILogger<SettingsPersistence> log) : base(loop, roles, log)
    {
        this._roles = roles;
        Interval = TimeSpan.FromSeconds(1);
    }

    protected override string RevisionPart => "settings";
    protected override State? Get(GameSession s) => s.SettingsPart;
    protected override void Set(GameSession s, State state) => s.SettingsPart = state;
    protected override bool Eligible(GameSession s) => s.Role != null && s.Settings != null;

    protected override Func<long, long, long, Task<bool>> Capture(GameSession s)
    {
        byte[] bytes = s.Settings!.ToByteArray();
        return (role, epoch, revision) => _roles.SaveBlobLazyAsync(role, epoch, revision, "settings", bytes);
    }

    /// <summary>Barrier: write the session's settings now if a change is still unsaved.</summary>
    public void Flush(GameSession s)
    {
        if (s.Role is not { } role || s.Settings is not { } settings || !IsDirty(s)) return;
        _roles.SaveSettings(role.Id, settings.ToByteArray());
        Persisted(s);
    }
}
