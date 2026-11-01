namespace Ro3Emu.Server;

/// <summary>
/// Limits on what one game session may send, and the thresholds of the web panel's suspicious-activity list
/// (Ro3Emu:AntiCheat, CheatMonitor, docs/systems/anticheat.md). Every value is (inferred): the original's limits are
/// not in the client, so they sit well above what the client sends in normal play.
/// </summary>
public sealed class AntiCheatOptions
{
    /// <summary>
    /// Game messages one session may send per second on average (any message); 0 turns the limit off. A walk goes out at
    /// every turn, so steering with the mouse can send dozens a second: the default leaves room for that.
    /// </summary>
    public int MessagesPerSecond { get; set; } = 120;

    /// <summary>Game messages one session may send at once above that rate.</summary>
    public int MessageBurst { get; set; } = 600;

    /// <summary>
    /// Tighter budgets of single message ids, "msgId": "perSecond/burst": the ones whose work is large (a full role
    /// login, a database insert). Unset ids only share the session's budget.
    /// </summary>
    public Dictionary<string, string> MessageLimits { get; set; } = new()
    {
        ["1006"] = "0.5/5",  // MsgId.LoginRoleCreate: a role insert
        ["1007"] = "0.5/5",  // MsgId.LoginRoleSelect: a full handover and role login
    };

    /// <summary>Chat lines (group channels and whispers) one session may send, "perSecond/burst".</summary>
    public string ChatLines { get; set; } = "1/10";

    /// <summary>Characters of one chat line; longer lines are refused.</summary>
    public int ChatMaxLength { get; set; } = 500;

    /// <summary>Roles one account may hold: LC_SelectRoleManager.m_iMaxRoleNumber (6), the role screen's slots.</summary>
    public int MaxRolesPerAccount { get; set; } = 6;

    /// <summary>Seconds between writes of the refusal counts to the database.</summary>
    public int FlushSeconds { get; set; } = 30;

    /// <summary>Days refusal counts and EXP samples are kept.</summary>
    public int KeepDays { get; set; } = 30;

    /// <summary>The panel's suspicious-activity list.</summary>
    public AntiCheatAlertOptions Alerts { get; set; } = new();
}

/// <summary>When the panel lists an account or character as suspicious (Ro3Emu:AntiCheat:Alerts).</summary>
public sealed class AntiCheatAlertOptions
{
    /// <summary>The window the list looks back over, in hours.</summary>
    public int WindowHours { get; set; } = 1;

    /// <summary>Refused requests of one account in the window that list it.</summary>
    public int Refusals { get; set; } = 20;

    /// <summary>
    /// A character's gain in the window is listed when it is this many times the median gain of the characters that
    /// gained any in the same window, and at least the floor below.
    /// </summary>
    public double GainFactor { get; set; } = 10;

    /// <summary>Zeny (currency 1) gained from the game (ledger sources) in the window, at least.</summary>
    public long ZenyFloor { get; set; } = 1_000_000;

    /// <summary>Items gained from the game (ledger sources, all items counted together) in the window, at least.</summary>
    public long ItemFloor { get; set; } = 2_000;

    /// <summary>Base EXP gained in the window, at least.</summary>
    public long ExpFloor { get; set; } = 5_000_000;
}
