using System.Text.Json;
using Ro3Emu.Panel;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Maintenance mode (docs/guide/panel.md, Server control): while it is on, a game login of an account without a staff
/// role is refused at the game gate with ConnectServerResp ClusterShutdown. The client maps a cluster shutdown to its
/// own maintenance notice (LC_LoginManager: enum_ConnectServerState.ClusterShutdown => UIWordsDefine.ServerMaintenancing,
/// UIWords 28108); that the gate's ClusterShutdown answer reaches that state is inferred. Players already in the game
/// stay. The state is kept in panel_setting, so it survives a restart.
/// </summary>
public sealed class MaintenanceMode(AccountStore accounts, StaffRoles staff, TimeProvider clock)
{
    public sealed record State(bool On, string Note, string? By, long Since);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private volatile State? _state;

    public State Current => _state ?? Load();

    /// <summary>True when maintenance is on and <paramref name="account"/> has no staff role.</summary>
    public bool Refuses(string account) => Current.On && staff.RoleOf(account) == PanelRole.None;

    /// <summary>Reads the stored state (the host does it at start).</summary>
    public State Load()
    {
        var text = accounts.ReadSync<string?>(new PanelSettingRead(PanelSettingKeys.Maintenance));
        State state;
        try { state = text == null ? new State(false, "", null, 0) : JsonSerializer.Deserialize<State>(text, Json) ?? new State(false, "", null, 0); }
        catch (JsonException) { state = new State(false, "", null, 0); }
        return _state = state;
    }

    /// <summary>Turns it on or off, stored with its audit row first, then in force.</summary>
    public async Task SetAsync(string admin, bool on, string note)
    {
        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        var next = new State(on, note, admin, now);
        await accounts.WriteAsync<bool>(new PanelSettingWritePlan(PanelSettingKeys.Maintenance, JsonSerializer.Serialize(next, Json), admin,
            AdminAudit.Maintenance, (on ? "maintenance on" : "maintenance off") + (note.Length > 0 ? ": " + note : ""), now));
        _state = next;
    }
}
