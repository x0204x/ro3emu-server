namespace Ro3Emu.Panel;

/// <summary>The web control panel (Ro3Emu:Panel, docs/guide/panel.md).</summary>
public sealed class PanelOptions
{
    /// <summary>Serve the panel. Off: its port is not opened at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The panel's own HTTP port, next to the server's HttpPort.</summary>
    public int Port { get; set; } = 5381;

    /// <summary>Interface the panel listens on. Empty: the one the server's other ports use (loopback for local
    /// play). A reverse proxy on the same machine sets 127.0.0.1 so only the proxy reaches the panel.</summary>
    public string? BindAddress { get; set; }

    /// <summary>
    /// Addresses of reverse proxies whose X-Forwarded-For and X-Forwarded-Proto are believed: the client address for
    /// the login budget and https for the cookie's Secure flag. Empty: both headers are ignored, whoever sends them.
    /// </summary>
    public string[] TrustedProxies { get; set; } = [];

    /// <summary>Hours a panel login stays valid without use; each request extends it. Sessions live in memory and end
    /// when the server restarts.</summary>
    public int SessionHours { get; set; } = 12;

    /// <summary>Accounts can be created on the panel's sign-up page. Off: only the game's first login creates them.</summary>
    public bool Registration { get; set; } = true;

    /// <summary>
    /// The folder of the pictures the pages show (item, monster, class and map images), decoded from the operator's own
    /// client by the data export. Empty: icons/ in the game data folder. Without it the pages draw letters instead.
    /// </summary>
    public string? ImageDir { get; set; }

    /// <summary>White-label branding: the server's own name, tagline, logo, footer and colour on every page.</summary>
    public PanelBranding Branding { get; set; } = new();
}

/// <summary>Ro3Emu:Panel:Branding. Every field is optional; an empty one keeps RO3Emu's own.</summary>
public sealed class PanelBranding
{
    /// <summary>The name in the header, the title bar and the home page (at most 64 characters). Empty: Ro3Emu:ClusterName.</summary>
    public string? ServerName { get; set; }

    /// <summary>A line under the name on the home page (at most 160 characters).</summary>
    public string? Tagline { get; set; }

    /// <summary>A PNG, JPG or WebP file in the server's data folder (a path relative to it), shown instead of the star.</summary>
    public string? LogoPath { get; set; }

    /// <summary>The footer's text instead of RO3Emu's (at most 300 characters).</summary>
    public string? FooterText { get; set; }

    /// <summary>The colour of links, focus rings and highlights, as #rrggbb.</summary>
    public string? AccentColor { get; set; }
}
