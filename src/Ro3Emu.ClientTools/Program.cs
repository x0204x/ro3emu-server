using Ro3Emu.ClientTools;

// Ro3Emu.ClientTools — everything a player needs to use the RO3 client (the PC build) with an Ro3Emu server.
if (args.Length < 2)
{
    Console.Error.WriteLine("""
        usage:
          play    <client dir> <server url>     patch for that server (if needed) and start the game
          patch   <client dir> <server url> [gate_rsa_public.pem] [Ro3Emu.ClientHooks.dll] [--verbose]
          restore <client dir>                  put the original DLLs back
          launch  <client dir> [ro3.exe args...]
          release <client dir> <server url> <gate_rsa_public.pem> <repo dir> <version> [channel] [zstd level] [--loc-map <file>]
                                                publish the client patched for that server for Ro3Emu.Launcher
          release-launcher <repo dir> <channel> <Ro3EmuLauncher.exe> <version>
          scan    <client dir> <text>...        list string literals in the client DLLs
          members <client dir> <dll> <type> [method]  list a type's methods with their parameters
          delete-role <server url> <account> <password> <character name or id>
        <client dir> is the folder with ro3.exe, <server url> e.g. http://203.0.113.5:5380.
        Without a .pem the gate key is downloaded from the server.
        """);
    return 1;
}

try
{
    ClientPatcher.VerboseLogs = args.Contains("--verbose");
    args = args.Where(a => a != "--verbose").ToArray();
    if (args.Length == 0) throw new ArgumentException("missing command");

    string? locMap = null;
    if (args[0] == "release")
    {
        var filtered = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--loc-map")
            {
                if (++i >= args.Length) throw new ArgumentException("--loc-map needs <file>");
                locMap = args[i];
                continue;
            }
            filtered.Add(args[i]);
        }
        args = [.. filtered];
    }
    switch (args[0])
    {
        case "play":
            if (args.Length < 3) throw new ArgumentException("play needs <client dir> <server url>");
            ClientPatcher.Patch(args[1], args[2].TrimEnd('/'), await ServerKey(args[2]));
            return Launcher.Launch(args[1], args[3..]);
        case "patch":
            if (args.Length < 3) throw new ArgumentException("patch needs <client dir> <server url>");
            string pem = args.Length > 3 ? File.ReadAllText(args[3]) : await ServerKey(args[2]);
            ClientPatcher.Patch(args[1], args[2].TrimEnd('/'), pem, args.Length > 4 ? args[4] : null);
            return 0;
        case "release":
            if (args.Length < 6) throw new ArgumentException("release needs <client dir> <server url> <pem> <repo dir> <version>");
            return await ReleaseBuilder.PublishAsync(args[1], args[2].TrimEnd('/'), File.ReadAllText(args[3]), args[4], args[5],
                args.Length > 6 ? args[6] : "test", args.Length > 7 ? int.Parse(args[7]) : 9, locMap);
        case "release-launcher":
            if (args.Length < 5) throw new ArgumentException("release-launcher needs <repo dir> <channel> <Ro3EmuLauncher.exe> <version>");
            var withLauncher = await Ro3Emu.Patcher.Publisher.PublishLauncherAsync(args[1], args[2], args[3], args[4]);
            Console.WriteLine($"channel {withLauncher.Channel}: game {withLauncher.Version}, launcher {withLauncher.Launcher!.Version}");
            return 0;
        case "scan":
            ClientPatcher.Scan(args[1], args[2..]);
            return 0;
        case "delete-role":
            if (args.Length < 5) throw new ArgumentException("delete-role needs <server url> <account> <password> <character>");
            return await DeleteRole(args[1], args[2], args[3], args[4]);
        case "members":
            ClientPatcher.Members(args[1], args[2], args[3], args.Length > 4 ? args[4] : null);
            return 0;
        case "restore":
            ClientPatcher.Restore(args[1]);
            return 0;
        case "launch":
            return Launcher.Launch(args[1], args[2..]);
        default:
            throw new ArgumentException($"unknown command {args[0]}");
    }
}
catch (Exception e)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 2;
}

// The RO3 client has no character deletion (its role screen button is hidden); the server offers it over HTTP.
static async Task<int> DeleteRole(string serverUrl, string account, string password, string role)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    var resp = await http.PostAsync(serverUrl.TrimEnd('/') + "/ro3emu/account/delete-role", new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["identity"] = account, ["credential"] = password, ["role"] = role,
    }));
    using var json = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    int code = json.RootElement.GetProperty("code").GetInt32();
    Console.WriteLine(json.RootElement.GetProperty("message").GetString());
    return code == 0 ? 0 : 3;
}

static async Task<string> ServerKey(string serverUrl)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    var url = serverUrl.TrimEnd('/') + "/ro3emu/gate-key.pem";
    Console.WriteLine($"fetching gate key from {url}");
    return await http.GetStringAsync(url);
}
