using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Ro3Emu.Setup;

/// <summary>
/// The server's web panel (docs/guide/panel.md) as a link: the launcher's "Web panel" entry and RO3Emu.exe panel.
/// In order: the operator's own address (launcher.json "panelUrl"), else the panel of the server this PC plays on: the
/// local server's, or a joined server's when it answers.
/// </summary>
internal static class PanelLink
{
    /// <summary>The panel's default port (Ro3Emu:Panel:Port).</summary>
    public const int DefaultPort = 5381;

    /// <summary>The launcher's entry: opens <see cref="Entry.Url"/>.</summary>
    public const string Open = "open";

    /// <summary>The launcher's entry: the local server is not running; the entry starts it, then opens its panel.</summary>
    public const string Start = "start";

    /// <summary>The launcher's entry: looking for a joined server's panel.</summary>
    public const string Checking = "checking";

    /// <summary>The launcher's entry: dimmed, <see cref="Entry.Hint"/> says why.</summary>
    public const string Unavailable = "unavailable";

    /// <summary>What the launcher's Web panel entry does: <see cref="Open"/>, <see cref="Start"/>, <see cref="Checking"/> or <see cref="Unavailable"/>.</summary>
    internal sealed record Entry(string State, string? Url, string? Hint);

    /// <summary>
    /// The launcher's entry, or null when it has none. <paramref name="configured"/> (launcher.json "panelUrl") wins in
    /// either mode. Otherwise an operator's launcher shows no entry; RO3Emu's own app shows the joined server's panel
    /// after a join (<paramref name="joinedPanel"/>, found by <see cref="FirstAnsweringAsync"/>), else the local server's.
    /// </summary>
    public static Entry? ForLauncher(string? configured, bool ro3emuMode, bool joined, string? joinedPanel, bool joinProbing,
        string? localUrl, bool localRunning)
    {
        if (Configured(configured) is { } url) return new Entry(Open, url, null);
        if (!ro3emuMode) return null;
        if (joined)
            return joinedPanel != null ? new Entry(Open, joinedPanel, null)
                : joinProbing ? new Entry(Checking, null, "Looking for the web panel of the server you joined.")
                : new Entry(Unavailable, null, "The server you joined has not shared its web panel.");
        if (localUrl == null) return new Entry(Unavailable, null, "Your server runs without its web panel.");
        return localRunning ? new Entry(Open, localUrl, null)
            : new Entry(Start, localUrl, "Starts your server on this PC and opens its web panel.");
    }

    /// <summary>launcher.json "panelUrl": an absolute http or https address, else null.</summary>
    public static string? Configured(string? url)
    {
        url = url?.Trim();
        return !string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            && uri.Host.Length > 0 ? url : null;
    }

    /// <summary>
    /// The panel of a server on this PC, or null when it is off (port 0). It listens on <paramref name="bindAddress"/>
    /// (see <see cref="BindAddress"/>): none and every interface are reached on loopback, a single address on itself.
    /// </summary>
    public static string? Local(ServerProcess.Ports ports, string? bindAddress = null)
    {
        if (ports.Panel <= 0) return null;
        string host = "127.0.0.1";
        if (IPAddress.TryParse(bindAddress, out var bind) && !bind.Equals(IPAddress.Any))
            host = bind.Equals(IPAddress.IPv6Any) ? "[::1]"
                : bind.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{bind}]" : bind.ToString();
        return $"http://{host}:{ports.Panel.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// The interface the bundled server's panel listens on, as the server reads it (ServerHost.PanelAddress): the
    /// Ro3Emu:Panel:BindAddress setting, else Ro3Emu:BindAddress, each from appsettings.json, then
    /// appsettings.&lt;environment&gt;.json next to the server, then the Ro3Emu__... environment variables (the launcher and
    /// RO3Emu.exe pass neither on the command line). Null: none is set (loopback for local play, every interface on a host).
    /// </summary>
    public static string? BindAddress(string? serverDir, string environment, Func<string, string?> variable)
    {
        string? panel = null, server = null;
        if (serverDir != null)
            foreach (var name in new[] { "appsettings.json", $"appsettings.{environment}.json" })
                ReadBind(Path.Combine(serverDir, name), ref panel, ref server);
        if (variable("Ro3Emu__Panel__BindAddress") is { Length: > 0 } envPanel) panel = envPanel;
        if (variable("Ro3Emu__BindAddress") is { Length: > 0 } envServer) server = envServer;
        return panel ?? server;
    }

    private static void ReadBind(string file, ref string? panel, ref string? server)
    {
        try
        {
            if (!File.Exists(file)) return;
            using var json = JsonDocument.Parse(File.ReadAllBytes(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!json.RootElement.TryGetProperty("Ro3Emu", out var ro3emu) || ro3emu.ValueKind != JsonValueKind.Object) return;
            if (Text(ro3emu, "BindAddress") is { } s) server = s;
            if (ro3emu.TryGetProperty("Panel", out var p) && p.ValueKind == JsonValueKind.Object && Text(p, "BindAddress") is { } b) panel = b;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            SetupLog.Write($"panel bind address: {file}: {e.Message}");
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    /// <summary>
    /// Where a joined server's panel may be: its host on the HTTP port + 1 (5381 next to 5380, 5391 next to 5390, the
    /// pairs docs/guide/panel.md and RO3Emu.exe suggest), then on the default <see cref="DefaultPort"/>. (inferred: the
    /// server does not announce its panel, so the launcher looks where RO3Emu puts it)
    /// </summary>
    public static IReadOnlyList<string> JoinCandidates(string serverUrl)
    {
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) || uri.Host.Length == 0) return [];
        var ports = new List<int>();
        if (uri.Port is > 0 and < 65535) ports.Add(uri.Port + 1);
        if (!ports.Contains(DefaultPort) && uri.Port != DefaultPort) ports.Add(DefaultPort);
        return [.. ports.Select(p => $"{uri.Scheme}://{uri.Host}:{p.ToString(System.Globalization.CultureInfo.InvariantCulture)}")];
    }

    /// <summary>
    /// Where a joined server's panel may be: the address the server announces (GET /ro3emu/panel, Ro3Emu:Panel:PublicUrl,
    /// a panel behind a reverse proxy) first, then <see cref="JoinCandidates"/>.
    /// </summary>
    public static async Task<IReadOnlyList<string>> JoinCandidatesAsync(string serverUrl, TimeSpan timeout, CancellationToken token = default)
    {
        var list = new List<string>();
        try
        {
            using var http = new HttpClient { Timeout = timeout };
            using var response = await http.GetAsync(serverUrl.TrimEnd('/') + "/ro3emu/panel", token);
            if (response.IsSuccessStatusCode)
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                if (json.RootElement.ValueKind == JsonValueKind.Object && Configured(Text(json.RootElement, "url")) is { } announced)
                    list.Add(announced.TrimEnd('/'));
            }
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or UriFormatException)
        {
        }
        list.AddRange(JoinCandidates(serverUrl).Where(c => !list.Contains(c)));
        return list;
    }

    /// <summary>The first of <paramref name="candidates"/> (in their order) whose panel answers within <paramref name="timeout"/>, or null.</summary>
    public static async Task<string?> FirstAnsweringAsync(IEnumerable<string> candidates, TimeSpan timeout, CancellationToken token = default)
    {
        using var http = new HttpClient { Timeout = timeout };
        var probes = candidates.Select(url => (url, answers: AnswersAsync(http, url, token))).ToList();
        foreach (var (url, answers) in probes)
            if (await answers) return url;
        return null;
    }

    /// <summary>An RO3Emu web panel answers at <paramref name="panelUrl"/>: its public /api/status (a server status with an uptime).</summary>
    public static async Task<bool> AnswersAsync(HttpClient http, string panelUrl, CancellationToken token = default)
    {
        try
        {
            using var response = await http.GetAsync(panelUrl.TrimEnd('/') + "/api/status", token);
            if (!response.IsSuccessStatusCode) return false;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("uptimeSeconds", out _);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or UriFormatException)
        {
            return false;
        }
    }
}
