using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using Ro3Emu.ClientTools;
using Ro3Emu.DataExport;

namespace Ro3Emu.Setup;

/// <summary>The player-facing modes: play locally, host, join, restore, check, export, diagnostics.</summary>
internal static partial class Flows
{
    internal static string? HooksDll => ClientDll("Ro3Emu.ClientHooks");
    internal static string? LocDll => ClientDll("Ro3Emu.Loc");

    /// <summary>client\&lt;name&gt;.dll next to RO3Emu.exe; for developers the repository's Release or Debug build.</summary>
    private static string? ClientDll(string project) =>
        SetupHome.Shipped("client", project + ".dll")
        ?? (SetupHome.RepositoryRoot() is { } repo
            ? new[] { "Release", "Debug" }.Select(c => Path.Combine(repo, "src", project, "bin", c, "netstandard2.1", project + ".dll")).FirstOrDefault(File.Exists)
            : null);

    /// <summary>The game folder already chosen (option, saved, environment), without asking; for diagnostics.</summary>
    public static string? KnownClient(Options options)
    {
        if (ClientLocator.IsClient(options.ClientDir)) return Path.GetFullPath(options.ClientDir!);
        var saved = Settings.Load().ClientDir;
        if (ClientLocator.IsClient(saved)) return saved;
        var env = Environment.GetEnvironmentVariable("RO3_CLIENT_DIR");
        return ClientLocator.IsClient(env) ? env : null;
    }

    /// <summary>
    /// The game folder: --client, else the remembered one, else a search; several found means a choice, none means
    /// a folder picker or the official download.
    /// </summary>
    public static async Task<string> ResolveClientAsync(Options options, bool offerInstall)
    {
        var settings = Settings.Load();
        string? chosen = null;
        if (options.ClientDir != null)
        {
            if (!ClientLocator.IsClient(options.ClientDir))
                throw new SetupException($"'{options.ClientDir}' is not a Ragnarok 3 game folder (no ro3.exe and ro3_Data\\Managed in it).",
                    "Give the folder that contains ro3.exe, for example --client \"C:\\Games\\Genesis\\ROO_PC\".");
            chosen = Path.GetFullPath(options.ClientDir);
        }
        else if (ClientLocator.IsClient(settings.ClientDir)) chosen = settings.ClientDir;
        else
        {
            Ui.Step("Looking for the game");
            var found = ClientLocator.Candidates(settings);
            if (found.Count == 1) chosen = found[0];
            else if (found.Count > 1)
            {
                for (int i = 0; i < found.Count; i++) Ui.Info($"[{i + 1}] {found[i]}  ({ClientLocator.InstalledRelease(found[i]) ?? "release unknown"})");
                var answer = Ui.Ask("Which one?", "1");
                chosen = int.TryParse(answer, out var n) && n >= 1 && n <= found.Count ? found[n - 1] : found[0];
            }
            else chosen = await NoClientFoundAsync(options, offerInstall);
        }
        settings = Settings.Load();
        settings.ClientDir = chosen;
        settings.Save();
        Ui.Ok($"Game folder: {chosen}  ({ClientLocator.InstalledRelease(chosen) ?? "release unknown"})");
        return chosen;
    }

    private static async Task<string> NoClientFoundAsync(Options options, bool offerInstall)
    {
        if (!Ui.Interactive)
            throw new SetupException("No Ragnarok 3 game folder was found.",
                "Start RO3Emu.exe with --client \"<folder with ro3.exe>\", or run RO3Emu.exe install to download the game.");
        Ui.Warn("No Ragnarok 3 game folder was found on this PC.");
        Ui.Info("[1] I have the game: pick its folder (the one with ro3.exe)");
        if (offerInstall) Ui.Info("[2] Download the game from the official Genesis servers (about 24 GB)");
        Ui.Info("[3] Quit");
        var answer = Ui.Ask("Your choice:", "1");
        if (answer == "2" && offerInstall)
        {
            var folder = await InstallFlow.InstallNewAsync(options);
            return folder;
        }
        if (answer != "1") throw new SetupException("No game folder chosen.", "Run RO3Emu.exe again when you have the game.");
        var picked = FolderPicker.Pick("Ragnarok 3 game folder (the one with ro3.exe)") ?? Ui.Ask("Game folder:");
        foreach (var candidate in new[] { picked, Path.Combine(picked ?? "", "ROO_PC") })
            if (ClientLocator.IsClient(candidate)) return Path.GetFullPath(candidate!);
        throw new SetupException($"'{picked}' has no ro3.exe.", "Run RO3Emu.exe again and pick the folder that contains ro3.exe.");
    }

    /// <summary>Stops when the folder cannot work with this RO3Emu, with the reason.</summary>
    internal static void CheckClient(string client, bool needExport)
    {
        var problems = ClientLocator.Problems(client, needExport, HooksDll, LocDll);
        foreach (var p in problems) SetupLog.Write("check: " + p);
        if (problems.Count == 0) return;
        var shown = string.Join("\n    ", problems.Take(6)) + (problems.Count > 6 ? $"\n    ... and {problems.Count - 6} more" : "");
        throw new SetupException($"This game folder does not fit this RO3Emu:\n    {shown}",
            "Run RO3Emu.exe update to repair the game from Genesis' update servers. If it still fails, this game version is newer than " +
            "RO3Emu: get the latest RO3Emu from the releases page.");
    }

    /// <summary>Exports the game data when it is missing, from another client version, or asked for.</summary>
    /// <param name="icons">The web panel's pictures too (--no-icons turns them off); an export made without them gets
    /// them on the next start that asks.</param>
    public static void EnsureGameData(string client, bool force, bool icons = true) =>
        EnsureGameData(client, force, SetupHome.DataDir, (source, target) => { RunExport(source, target, icons); },
            icons ? ExportMissingIcons : null);

    // 兩個匯出邊界可分別注入；測試不載入原生 Lua，也不讀取原版圖片。
    internal static void EnsureGameData(string client, bool force, string dataDir, Action<string, string> export,
        Action<string, string>? exportIcons = null)
    {
        Ui.Step("Game data");
        if (PreparedGameData.UseIfPresent(dataDir, client, force))
        {
            Ui.Ok("已核對匯入資料與原始客戶端來源，沿用此資料，不執行原生匯出。");
        }
        else if (!force && GameDataExport.UpToDate(dataDir, client))
        {
            var stamp = GameDataExport.ReadStamp(dataDir)!;
            Ui.Ok($"ready (exported {stamp.ExportedUtc.ToLocalTime():yyyy-MM-dd HH:mm})");
        }
        else
        {
            Ui.Info("Exporting the game data from your game folder (first run or game update, about a minute).");
            export(client, dataDir);
            return;
        }
        // 圖示不屬於已驗證的核心資料集合；只能在資料 gate 通過後獨立補齊。
        if (exportIcons != null && !GameDataExport.HasIcons(dataDir)) exportIcons(client, dataDir);
    }

    private static void ExportMissingIcons(string client, string dataDir)
    {
        var result = GameDataExport.ExportIcons(client, dataDir, ExportOptions(icons: true));
        Ui.EndProgress();
        ReportIcons(result);
    }

    private static GameDataExport.Options ExportOptions(bool icons)
    {
        var last = Stopwatch.StartNew();
        return new GameDataExport.Options
        {
            Translations = Translations(),
            TranslationQueue = SetupHome.RepositoryRoot() is { } repo ? Path.Combine(repo, "re", "loc", "translation_queue.tsv") : null,
            Log = SetupLog.Write,
            Progress = (stage, done, total) =>
            {
                if (last.ElapsedMilliseconds < 200 && done != total) return;
                last.Restart();
                Ui.Progress(stage, done, total, total > 0 ? $"{stage}: {done * 100 / total}%" : stage + "...");
            },
            Icons = icons,
        };
    }

    private static void ReportIcons(IconExport.Result? icons)
    {
        if (icons == null) Ui.Warn("The web panel's pictures could not be exported (see the log); the panel shows letters instead.");
        else Ui.Ok($"web panel pictures: {icons.Written} new, {icons.Unchanged} unchanged ({Ui.Size(icons.Bytes)})");
    }

    private static GameDataExport.Result RunExport(string client, string dataDir, bool icons)
    {
        var watch = Stopwatch.StartNew();
        var result = GameDataExport.Run(client, dataDir, ExportOptions(icons));
        Ui.EndProgress();
        Ui.Ok($"exported in {Ui.Duration(watch.Elapsed)}: {result.ConfigTables} tables, {result.SceneEditor} maps, {result.Skills} skills, " +
              $"{result.NavFiles} walk maps, {result.Messages} messages, {result.LocEntries} English texts");
        if (icons) ReportIcons(result.Icons);
        if (result.FailedTables.Count > 0)
            Ui.Warn($"{result.FailedTables.Count} game table(s) could not be exported ({string.Join(", ", result.FailedTables.Take(5))}" +
                (result.FailedTables.Count > 5 ? ", ..." : "") + "). The server may miss some content; RO3Emu will try again next start. " +
                "If this keeps happening, your game is newer than this RO3Emu: get the latest one from the releases page.");
        return result;
    }

    /// <summary>Our translations, embedded in RO3Emu.exe, in file name order.</summary>
    public static List<string> Translations()
    {
        var assembly = Assembly.GetExecutingAssembly();
        return assembly.GetManifestResourceNames().Where(n => n.StartsWith("translations/", StringComparison.Ordinal)).Order(StringComparer.Ordinal)
            .Select(n =>
            {
                using var reader = new StreamReader(assembly.GetManifestResourceStream(n)!);
                return reader.ReadToEnd();
            }).ToList();
    }

    public static async Task<int> PlayLocalAsync(Options options, bool host)
    {
        Ui.Title(host ? "RO3Emu - host a Ragnarok 3 server" : "RO3Emu - Ragnarok 3 on your own server");
        var localSettings = Settings.Load();
        string client;
        if (localSettings.ClientSync.Enabled)
        {
            // 冷安裝也必須先通過發行配對，不可先由 ResolveClientAsync 安裝未配對的 latest。
            client = SelectPairedClient(options.ClientDir, localSettings.ClientDir);
            localSettings.ClientDir = client;
            localSettings.Save();
        }
        else client = await ResolveClientAsync(options, offerInstall: true);
        if (!localSettings.ClientSync.Enabled)
        {
            Ui.Warn("尚未啟用 ClientSync 發行配對；沿用既有客戶端檢查模式。");
            Ui.Step("Checking the game");
            CheckClient(client, needExport: true);
            Ui.Ok("this game version works with RO3Emu");
            EnsureGameData(client, options.Reexport, !options.NoIcons);
        }

        var gms = options.GmAccounts.ToList();
        if (host && gms.Count == 0 && Ui.Interactive)
        {
            // Several accounts, comma separated: a host and the friends it plays with (issue #97).
            var gm = Ui.Ask("Account names that get GM commands in game, separated by commas (Enter for nobody):");
            gms.AddRange(gm.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        var local = await StartServerAsync(client, host, options.PublicHost, gms, options.Ports, options.PurchaseUrl, options);
        var server = local.Process;
        try
        {
            if (host)
            {
                Ui.Step("Friends can join");
                Ui.Info($"They run:  RO3Emu.exe join {local.PublicHost}{(local.Ports.Http == 5380 ? "" : ":" + local.Ports.Http)}");
                Ui.Info($"Windows may ask whether Ro3Emu.Server may use the network: allow it.");
                Ui.Info($"For players outside your network, forward TCP ports {ServerProcess.PortList(local.Ports)} on your router");
                Ui.Info("to this PC and start with --public <your public IP>.");
                if (local.Ports.Panel > 0)
                    Ui.Info($"Web panel for players and admins: http://{local.PublicHost}:{local.Ports.Panel} (docs/guide/panel.md).");
            }
            if (server != null) Program.OnClose = () => server.Stop(TimeSpan.FromSeconds(4));
            return await PlayAndWaitAsync(client, server, options.NoLaunch, local.VerifiedGenesisSequence);
        }
        finally
        {
            Program.OnClose = null;
            StopServer(server);
        }
    }

    /// <summary>A started (or found) local server: the address the game connects to, and the process (null = one was already running).</summary>
    internal sealed record LocalServer(ServerProcess? Process, string ServerUrl, string PublicHost, ServerProcess.Ports Ports,
        long? VerifiedGenesisSequence = null);

    /// <summary>
    /// Starts the bundled server on this PC (or uses one already answering, for local play) and patches the game for
    /// it. Local play binds loopback only (PublicHost 127.0.0.1); a host binds every interface for its friends.
    /// </summary>
    internal static async Task<LocalServer> StartServerAsync(string client, bool host, string? publicHostOption, IReadOnlyList<string> gms,
        ServerProcess.Ports ports, string? purchaseUrl = null, Options? options = null)
    {
        var serverExe = ServerProcess.FindExecutable()
            ?? throw new SetupException("The server is missing (server\\Ro3Emu.Server.exe next to RO3Emu.exe).",
                SetupHome.AppDir.StartsWith(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                    ? "RO3Emu.exe was started from inside the zip. Extract the whole zip first (right-click it, Extract All), then run RO3Emu.exe from the extracted folder."
                    : "Extract the whole RO3Emu zip again, keeping its folders.");
        string publicHost = "127.0.0.1";
        var args = new List<string>();
        var env = new Dictionary<string, string>();
        if (host)
        {
            publicHost = publicHostOption ?? LanAddress()
                ?? throw new SetupException("This PC has no network address to host on.", "Connect to a network, or give the address with --public <ip>.");
            if (!IPAddress.TryParse(publicHost, out _))
                throw new SetupException($"'{publicHost}' is not an IP address.", "Give an address like 192.168.1.20 or your public IP with --public.");
            env["ASPNETCORE_ENVIRONMENT"] = "Production";
            args.AddRange([$"--Ro3Emu:PublicHost={publicHost}", "--Ro3Emu:OpenContent:Enabled=true", "--Ro3Emu:Pass:Play=true", "--Ro3Emu:Pass:FreePremium=true"]);
            if (options != null) args.AddRange(ServerDisplayArgs(options));
            for (int i = 0; i < gms.Count; i++) args.Add($"--Ro3Emu:GmAccounts:{i}={gms[i]}");
        }
        else
        {
            // Local play: every account has GM commands (the server's own rule for a non-production loopback server).
            env["ASPNETCORE_ENVIRONMENT"] = "Development";
            args.Add("--Ro3Emu:PublicHost=127.0.0.1");
        }

        var syncPolicy = Settings.Load().ClientSync;
        Ro3Emu.ClientRelease.ClientReleaseRequirement? preparedRequirement = null;
        if (syncPolicy.Enabled)
        {
            if (options is null) throw new SetupException("ClientSync 缺少啟動選項。", "請由 RO3Emu.exe start 或 host 啟動。");
            if (ServerProcess.BusyPort(ports) is { } active)
                throw new SetupException($"連接埠 {active} 已使用，未更新客戶端或切換發布。", "請先正常停止舊伺服器，再啟動同步流程。");
            string pairedUrl = $"http://{(publicHost.Contains(':') ? "[" + publicHost + "]" : publicHost)}:{ports.Http}";
            string version = FileVersionInfo.GetVersionInfo(serverExe).ProductVersion
                ?? throw new InvalidDataException("無法讀取伺服器發行版本。");
            try
            {
                preparedRequirement = await ClientSync.PrepareAsync(syncPolicy, version, client, pairedUrl, options,
                    (folder, url, pem) => PatchClient(folder, url, pem!, purchaseUrl));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or System.Security.Cryptography.CryptographicException)
            {
                throw new SetupException("客戶端同步未完成，已停止開服：" + e.Message,
                    "請核對發行配對、官方來源、已整理資料及玩家更新簽署設定；修好後再啟動，不需刪除舊更新站或候選。");
            }
            args.Add("--Ro3Emu:ClientRelease:Required=true");
            args.Add("--Ro3Emu:ClientRelease:RequirementPath=" + Path.Combine(Path.GetFullPath(syncPolicy.RepoDirectory!),
                Ro3Emu.ClientRelease.ClientReleaseRequirement.FileName));
        }
        Ui.Step("Starting the server");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        ServerProcess? server = null;
        if (!host && await ServerProcess.Answers(http, ports.Http))
            Ui.Ok($"a server is already running on port {ports.Http}; using it");
        else
        {
            if (ServerProcess.BusyPort(ports) is { } busy)
                throw new SetupException($"Port {busy} is already used by another program, so the server cannot start.",
                    "Close the other program (or another RO3Emu window), or start with other ports, for example --http-port 5390 --micro-port 20001 --game-port 33102 --panel-port 5391 (or --no-panel).");
            server = ServerProcess.Start(serverExe, SetupHome.DataDir, ports, args, env);
            var watch = Stopwatch.StartNew();
            bool ready = await server.WaitReadyAsync(TimeSpan.FromMinutes(5),
                t => Ui.Progress("Loading the game data", 0, 0, $"loading the game data... {(int)t.TotalSeconds} s"));
            Ui.EndProgress();
            if (!ready)
            {
                var tail = server.Tail();
                SetupLog.Write("server output:\n" + tail);
                bool exited = server.HasExited;
                server.Dispose();
                throw new SetupException(exited ? "The server stopped while starting:\n" + Indent(LastLines(tail, 6)) : "The server did not answer within 5 minutes.",
                    "Run RO3Emu.exe again. If it fails the same way, run RO3Emu.exe start --reexport to export the game data again.");
            }
            Ui.Ok($"running (ready in {Ui.Duration(watch.Elapsed)}, log {server.LogPath})");
        }

        try
        {
            var serverUrl = $"http://{(publicHost.Contains(':') ? "[" + publicHost + "]" : publicHost)}:{ports.Http}";
            if (preparedRequirement is null)
            {
                var pem = await http.GetStringAsync($"http://127.0.0.1:{ports.Http}/ro3emu/gate-key.pem");
                PatchClient(client, serverUrl, pem, purchaseUrl);
            }
            return new LocalServer(server, serverUrl, publicHost, ports, preparedRequirement?.BaseClient?.Sequence);
        }
        catch
        {
            StopServer(server);
            throw;
        }
    }

    internal static string SelectPairedClient(string? explicitPath, string? savedPath)
    {
        string? selected = explicitPath ?? savedPath;
        if (string.IsNullOrWhiteSpace(selected)) throw new SetupException("ClientSync 尚未設定客戶端目錄，未下載或啟動服務。",
            "請以 --client 明確指定獨立目錄；可使用尚未安裝遊戲的新目錄。");
        return Path.GetFullPath(selected);
    }

    /// <summary>Optional login-screen labels passed through to the server's standard .NET configuration.</summary>
    internal static List<string> ServerDisplayArgs(Options options)
    {
        var args = new List<string>();
        if (options.ClusterName is not null) args.Add($"--Ro3Emu:ClusterName={options.ClusterName}");
        if (options.AreaName is not null) args.Add($"--Ro3Emu:AreaName={options.AreaName}");
        return args;
    }

    /// <summary>Stops a server this program started (a clean shutdown first), with the console's messages.</summary>
    internal static void StopServer(ServerProcess? server)
    {
        if (server == null) return;
        bool running = !server.HasExited;
        if (running) Ui.Step("Stopping the server");
        server.Stop(TimeSpan.FromSeconds(30));
        server.Dispose();
        if (running) Ui.Ok($"stopped. Your characters are saved in {SetupHome.DataDir}.");
    }

    /// <summary>Starts the game (unless asked not to) and keeps the server up until the player is done.</summary>
    private static async Task<int> PlayAndWaitAsync(string client, ServerProcess? server, bool noLaunch, long? verifiedGenesisSequence = null)
    {
        Process? game = null;
        if (!noLaunch) game = LaunchGame(client, verifiedGenesisSequence);
        Ui.Step("Server running");
        Ui.Info("Keep this window open while you play.");
        if (Ui.Interactive) Ui.Info(noLaunch ? "Press Q to stop the server." : "Press Q to stop the server, or R to start the game again.");
        else Ui.Info("Type quit (or close the window) to stop the server.");
        SetupLog.Write("ready");
        Console.Out.Flush();

        Task<string?>? line = Ui.Interactive ? null : Task.Run(Console.In.ReadLine);
        bool gameAnnounced = false;
        Program.Waiting = true;
        try
        {
            while (!Program.StopRequested)
            {
                if (server is { HasExited: true })
                {
                    SetupLog.Write("server output:\n" + server.Tail(40));
                    throw new SetupException("The server stopped unexpectedly:\n" + Indent(LastLines(server.Tail(), 6)), "Run RO3Emu.exe again.");
                }
                if (game != null && !gameAnnounced && game.HasExited && !GameRunning())
                {
                    gameAnnounced = true;
                    Ui.Info(Ui.Interactive ? "The game was closed. Press R to start it again, or Q to stop the server." : "The game was closed.");
                }
                if (line != null && line.IsCompleted)
                {
                    var text = line.Result;
                    if (text == null || text.Trim().Equals("quit", StringComparison.OrdinalIgnoreCase)) break;
                    line = Task.Run(Console.In.ReadLine);
                }
                if (Ui.Interactive && Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true).Key;
                    if (key is ConsoleKey.Q or ConsoleKey.Escape) break;
                    if (key == ConsoleKey.R && !GameRunning())
                    {
                        game = LaunchGame(client, verifiedGenesisSequence);
                        gameAnnounced = false;
                    }
                }
                await Task.Delay(300);
            }
        }
        finally { Program.Waiting = false; }
        return 0;
    }

    private static bool GameRunning() => Process.GetProcessesByName("ro3").Any();

    internal static Process LaunchGame(string client, long? verifiedGenesisSequence = null)
    {
        Ui.Step("Starting the game");
        var game = Quiet(() => Launcher.Start(client, [], verifiedGenesisSequence));
        if (game.HasExited)
        {
            int code = game.ExitCode;
            throw new SetupException($"The game closed right after starting (exit code {code}{(code == 70 ? ", launch check refused" : "")}).",
                "Run RO3Emu.exe again. If it keeps happening, run RO3Emu.exe update to repair the game.");
        }
        Ui.Ok("the game is starting. Log in with any account name and password: the account is created on first login.");
        return game;
    }

    /// <summary>所有入口先準備玩家自身的語系配對，再修補 DLL；不覆寫玩家自訂檔案。</summary>
    internal static void PatchClient(string client, string serverUrl, string pem, string? purchaseUrl)
    {
        EnsureLocMap(client);
        CopyLocalizationMaps(PlayerLocalizationDir, client);
        Ui.Step("Patching the game for this server");
        try { Quiet(() => { ClientPatcher.Patch(client, serverUrl, pem, HooksDll, LocDll, purchaseUrl); return 0; }); }
        catch (UnauthorizedAccessException e)
        {
            throw new SetupException("The game folder cannot be written: " + e.Message,
                "Close the game if it is running. If the game is under Program Files, run RO3Emu.exe as administrator once, or move the game to another folder.");
        }
        catch (IOException e) when (e.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021))
        {
            throw new SetupException("A game file is in use: " + e.Message, "Close the game, then run RO3Emu.exe again.");
        }
        Ui.Ok($"the game now connects to {serverUrl} (RO3Emu.exe restore undoes this)");
        Ui.Warn("Do not use the Genesis launcher to repair this game folder while playing on RO3Emu: it undoes the patch.");
    }

    /// <summary>Runs patcher code whose Console output belongs in the log, not on the player's screen.</summary>
    private static T Quiet<T>(Func<T> action)
    {
        var original = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try { return action(); }
        finally
        {
            Console.SetOut(original);
            foreach (var line in captured.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)) SetupLog.Write("  " + line.TrimEnd('\r'));
        }
    }

    public static async Task<int> JoinAsync(Options options)
    {
        Ui.Title("RO3Emu - join a server");
        var address = options.Positional.FirstOrDefault() ?? Ui.Ask("Server address (ask the server owner, for example 203.0.113.10):");
        var serverUrl = ServerUrl(address)
            ?? throw new SetupException($"'{address}' is not a server address.", "Use the address the server owner gave you, for example RO3Emu.exe join 203.0.113.10");
        var client = await ResolveClientAsync(options, offerInstall: true);
        Ui.Step("Checking the game");
        CheckClient(client, needExport: false);
        Ui.Ok("this game version works with RO3Emu");

        await PrepareJoinAsync(client, serverUrl, options.PurchaseUrl);
        if (!options.NoLaunch) LaunchGame(client);
        Ui.Info($"Next time, run RO3Emu.exe join {address} again. This window can be closed.");
        if (Ui.OwnsConsole() && Ui.Interactive) await Task.Delay(8000);
        return 0;
    }

    /// <summary>Contacts someone else's server and patches the game for it (its gate key, our English texts).</summary>
    internal static async Task PrepareJoinAsync(string client, string serverUrl, string? purchaseUrl = null)
    {
        Ui.Step($"Contacting {serverUrl}");
        string pem;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            pem = await http.GetStringAsync(serverUrl + "/ro3emu/gate-key.pem");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new SetupException($"The server at {serverUrl} does not answer ({e.Message}).",
                "Check the address with the server owner, and that the server is running with TCP ports 5380, 20000 and 33100 open to you.");
        }
        Ui.Ok("the server answers");
        PatchClient(client, serverUrl, pem, purchaseUrl);
    }

    /// <summary>"host", "host:port", "[v6]:port" or "http(s)://host[:port]" as a base URL; the port is 5380 unless written.</summary>
    public static string? ServerUrl(string address)
    {
        address = address.Trim().TrimEnd('/');
        if (address.Length == 0) return null;
        var withScheme = address.Contains("://") ? address : "http://" + address;
        if (!Uri.TryCreate(withScheme, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0
            || uri.AbsolutePath != "/" || uri.Query.Length > 0) return null;
        bool portWritten = System.Text.RegularExpressions.Regex.IsMatch(withScheme, @"^[A-Za-z]+://(\[[^\]]+\]|[^/:\[]+):\d+");
        return $"{uri.Scheme}://{uri.Host}:{(portWritten ? uri.Port : 5380)}";
    }

    public static int Restore(Options options)
    {
        Ui.Title("RO3Emu - restore the game");
        var client = KnownClient(options) ?? throw new SetupException("No game folder is known.", "Run RO3Emu.exe restore --client \"<folder with ro3.exe>\".");
        Quiet(() => { ClientPatcher.Restore(client); return 0; });
        Ui.Ok($"{client} is back to the original files: it talks to Genesis' servers again.");
        return 0;
    }

    public static int Check(Options options)
    {
        Ui.Title("RO3Emu - check the game");
        var known = KnownClient(options);
        var client = known ?? ResolveClientAsync(options, offerInstall: false).GetAwaiter().GetResult();
        if (known != null) Ui.Info($"{client}  ({ClientLocator.InstalledRelease(client) ?? "release unknown"})");
        var problems = ClientLocator.Problems(client, needExport: true, HooksDll, LocDll);
        foreach (var p in problems) Ui.Warn(p);
        if (problems.Count == 0) Ui.Ok("this game version works with RO3Emu (patch targets and every hook reference found)");
        return problems.Count == 0 ? 0 : 1;
    }

    public static int Export(Options options)
    {
        if (options.Positional.Count < 2) throw new SetupException("export needs <game folder> <data folder>.", "Example: RO3Emu export /srv/ro3/ROO_PC /srv/ro3emu/data");
        var (client, data) = (Path.GetFullPath(options.Positional[0]), Path.GetFullPath(options.Positional[1]));
        if (GameDataExport.Missing(client) is { Count: > 0 } missing)
            throw new SetupException($"'{client}' is not a complete game folder: {string.Join(", ", missing)} missing.",
                "Copy the whole game folder (at least ro3_Data/StreamingAssets) and give the folder that contains ro3_Data.");
        Ui.Step($"Exporting the game data from {client} to {data}");
        var result = RunExport(client, data, !options.NoIcons);
        if (options.ListInputs)
            foreach (var f in result.ClientFilesRead) Console.WriteLine("input " + f);
        return 0;
    }

    public static int Diag(Options options)
    {
        var zip = DiagnosticsZip.Write(KnownClient(options));
        Ui.Ok($"Wrote {zip}");
        Ui.Info($"Attach it to an issue at {Program.IssuesUrl}.");
        Ui.Reveal(zip);
        return 0;
    }

    /// <summary>This PC's LAN IPv4 address: an up interface with a gateway, not loopback or a virtual adapter.</summary>
    public static string? LanAddress()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var props = nic.GetIPProperties();
            if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))) continue;
            var address = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address));
            if (address != null) return address.Address.ToString();
        }
        return null;
    }

    private static string LastLines(string text, int n) => string.Join('\n', text.Split('\n').TakeLast(n));

    private static string Indent(string text) => string.Join('\n', text.Split('\n').Select(l => "    " + l.TrimEnd('\r')));
}
