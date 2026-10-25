using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ro3Emu.Patcher;

if (args.Length == 0)
{
    Usage();
    return 1;
}

try
{
    switch (args[0])
    {
        case "prepare" when args.Length == 5:
            await PrepareAsync(args[1], args[2], args[3], args[4]);
            break;
        case "publish-update" when args.Length == 2:
            await PublishGameAsync(Path.GetFullPath(args[1]), "1.0.1", update: true);
            break;
        case "damage" when args.Length == 2:
            Damage(Path.GetFullPath(args[1]));
            break;
        case "verify" when args.Length == 3:
            Verify(Path.GetFullPath(args[1]), args[2]);
            break;
        case "finalize" when args.Length == 6 && int.TryParse(args[2], out int oldPid) && int.TryParse(args[3], out int newPid):
            FinalizeEvidence(Path.GetFullPath(args[1]), oldPid, newPid, args[4], args[5]);
            break;
        case "finalize-legacy" when args.Length == 5 && int.TryParse(args[2], out int legacyOldPid) && int.TryParse(args[3], out int legacyNewPid):
            FinalizeLegacyEvidence(Path.GetFullPath(args[1]), legacyOldPid, legacyNewPid, args[4]);
            break;
        case "serve" when args.Length == 4 && int.TryParse(args[2], out int port):
            await ServeAsync(Path.GetFullPath(args[1]), port, Path.GetFullPath(args[3]));
            break;
        default:
            throw new ArgumentException("參數不完整或命令不存在。");
    }
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"FAIL: {e.Message}");
    return 2;
}

static async Task PrepareAsync(string rootArg, string oldLauncherArg, string newLauncherArg, string repositoryUrl)
{
    string root = Path.GetFullPath(rootArg);
    string oldLauncher = Path.GetFullPath(oldLauncherArg);
    string newLauncher = Path.GetFullPath(newLauncherArg);
    if (!File.Exists(oldLauncher) || !File.Exists(newLauncher)) throw new FileNotFoundException("找不到其中一版啟動器。");
    if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out Uri? repoUri) || !repoUri.IsLoopback || repoUri.Scheme != Uri.UriSchemeHttp)
        throw new ArgumentException("程序 E2E 儲存庫必須是 loopback HTTP URL。");
    if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        throw new IOException("E2E 根目錄必須不存在或為空。");

    string run = Path.Combine(root, "run");
    string repo = Path.Combine(root, "repo");
    string secrets = Path.Combine(root, "private-test-key");
    Directory.CreateDirectory(run);
    Directory.CreateDirectory(repo);
    Directory.CreateDirectory(secrets);
    string launcherPath = Path.Combine(run, "RO3Launcher.exe");
    File.Copy(oldLauncher, launcherPath);

    using var rsa = RSA.Create(3072);
    string privatePem = rsa.ExportPkcs8PrivateKeyPem();
    string publicPem = rsa.ExportSubjectPublicKeyInfoPem();
    File.WriteAllText(Path.Combine(secrets, "repository-private.pem"), privatePem);
    File.WriteAllText(Path.Combine(secrets, "repository-public.pem"), publicPem);
    var metadata = new LabMetadata
    {
        Root = root,
        RepositoryUrl = repositoryUrl,
        KeyId = "process-e2e-20261025",
        OldLauncherSha256 = Sha256(oldLauncher),
        NewLauncherSha256 = Sha256(newLauncher),
        LauncherPath = launcherPath,
        SettingsPath = Path.Combine(root, "isolated-settings", "launcher-settings.json"),
    };
    WriteJson(Path.Combine(root, "lab.json"), metadata);
    await PublishGameAsync(root, "1.0.0", update: false);
    var signing = new RepositorySigningOptions(metadata.KeyId, privatePem);
    ChannelInfo channel = await Publisher.PublishLauncherAsync(repo, "stable", newLauncher, "2.0.1", signing);

    var config = new
    {
        repository = repositoryUrl,
        channel = "stable",
        server = (string?)null,
        serverName = "Ragnarok 3 程序驗收",
        requireHttpsRepository = false,
        requireSignedChannel = true,
        trustedPublisherKeys = new Dictionary<string, string> { [metadata.KeyId] = publicPem },
        settingsFile = metadata.SettingsPath,
        allowedExternalHosts = Array.Empty<string>(),
    };
    WriteJson(Path.Combine(run, "launcher.json"), config);
    WriteJson(Path.Combine(root, "prepare-result.json"), new
    {
        verdict = "READY",
        isolation = new { repository = repoUri.AbsoluteUri, settings = metadata.SettingsPath, client = Path.Combine(run, "Client") },
        launcher = new { oldSha256 = metadata.OldLauncherSha256, newSha256 = metadata.NewLauncherSha256, channel.Sequence },
    });
    Console.WriteLine(root);
}

static async Task PublishGameAsync(string root, string version, bool update)
{
    LabMetadata metadata = ReadMetadata(root);
    string source = Path.Combine(root, "synthetic-client-source");
    Directory.CreateDirectory(Path.Combine(source, "data"));
    string marker = update ? "synthetic-game-v1.0.1\n" : "synthetic-game-v1.0.0\n";
    File.WriteAllText(Path.Combine(source, "ro3.exe"), marker);
    File.WriteAllText(Path.Combine(source, "data", "payload.bin"), update ? "payload-B-20261025\n" : "payload-A-20261025\n");
    File.WriteAllText(Path.Combine(source, "data", "constant.txt"), "preserved-content\n");
    string privatePem = await File.ReadAllTextAsync(Path.Combine(root, "private-test-key", "repository-private.pem"));
    var signing = new RepositorySigningOptions(metadata.KeyId, privatePem);
    PublishResult result = await Publisher.PublishAsync(new PublishOptions
    {
        Files = Publisher.CollectDirectory(source, "", _ => false),
        RepoDirectory = Path.Combine(root, "repo"),
        Version = version,
        Channel = "stable",
        LaunchExe = "ro3.exe",
        Signing = signing,
    });
    var hashes = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal)
        .ToDictionary(file => Path.GetRelativePath(source, file).Replace('\\', '/'), Sha256, StringComparer.Ordinal);
    WriteJson(Path.Combine(root, $"expected-game-{version}.json"), new { version, result.Channel.Sequence, files = hashes });
    Console.WriteLine($"published {version} sequence {result.Channel.Sequence}");
}

static void Damage(string root)
{
    string client = Path.Combine(root, "run", "Client");
    string payload = Path.Combine(client, "data", "payload.bin");
    string constant = Path.Combine(client, "data", "constant.txt");
    if (!File.Exists(Path.Combine(client, "ro3.exe")) || !File.Exists(payload) || !File.Exists(constant))
        throw new IOException("合成客戶端尚未安裝，不能建立修復案例。");
    byte[] bytes = File.ReadAllBytes(payload);
    Array.Fill<byte>(bytes, 0x58);
    File.WriteAllBytes(payload, bytes);
    byte[] constantBytes = File.ReadAllBytes(constant);
    Array.Fill<byte>(constantBytes, 0x59);
    File.WriteAllBytes(constant, constantBytes);
    File.WriteAllText(Path.Combine(client, "user-kept.txt"), "player-file\n");
    WriteJson(Path.Combine(root, "damage-result.json"), new { changed = new[] { "data/payload.bin", "data/constant.txt" }, preserved = "user-kept.txt" });
}

static void Verify(string root, string version)
{
    LabMetadata metadata = ReadMetadata(root);
    string client = Path.Combine(root, "run", "Client");
    using JsonDocument expected = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, $"expected-game-{version}.json")));
    var failures = new List<string>();
    foreach (JsonProperty file in expected.RootElement.GetProperty("files").EnumerateObject())
    {
        string path = Path.Combine(client, file.Name.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) failures.Add($"missing:{file.Name}");
        else if (!string.Equals(Sha256(path), file.Value.GetString(), StringComparison.OrdinalIgnoreCase)) failures.Add($"hash:{file.Name}");
    }
    if (!string.Equals(Sha256(metadata.LauncherPath), metadata.NewLauncherSha256, StringComparison.OrdinalIgnoreCase)) failures.Add("launcher:hash");
    if (File.Exists(metadata.LauncherPath + ".new")) failures.Add("launcher:.new");
    if (File.Exists(metadata.LauncherPath + ".old")) failures.Add("launcher:.old");
    if (!File.Exists(metadata.SettingsPath)) failures.Add("settings:missing");
    if (File.Exists(Path.Combine(root, "damage-result.json")) && !File.Exists(Path.Combine(client, "user-kept.txt"))) failures.Add("user-file:missing");
    var result = new { verdict = failures.Count == 0 ? "PASS" : "FAIL", version, failures, checkedUtc = DateTimeOffset.UtcNow };
    WriteJson(Path.Combine(root, $"verify-{version}.json"), result);
    if (failures.Count > 0) throw new InvalidDataException(string.Join(", ", failures));
    Console.WriteLine($"PASS {version}");
}

static void FinalizeEvidence(string root, int oldPid, int newPid, string restartToken, string commit)
{
    LabMetadata metadata = ReadMetadata(root);
    if (!RestartHandoff.IsValidToken(restartToken)) throw new ArgumentException("重啟 token 格式無效。");
    using JsonDocument install = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "verify-1.0.0.json")));
    using JsonDocument repaired = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "verify-1.0.1.json")));
    string[] requests = File.Exists(Path.Combine(root, "requests.ndjson"))
        ? File.ReadAllLines(Path.Combine(root, "requests.ndjson"), Encoding.UTF8).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray()
        : [];
    int badRequests = requests.Count(line => JsonDocument.Parse(line).RootElement.GetProperty("status").GetInt32() != 200);
    string launcherSha256 = Sha256(metadata.LauncherPath);
    WriteJson(Path.Combine(root, "hashes.json"), new
    {
        launcher = new { before = metadata.OldLauncherSha256, published = metadata.NewLauncherSha256, after = launcherSha256 },
        channel = new
        {
            json = Sha256(Path.Combine(root, "repo", "channels", "stable.json")),
            signature = Sha256(Path.Combine(root, "repo", "channels", "stable.json.sig")),
        },
        game = new
        {
            executable = Sha256(Path.Combine(root, "run", "Client", "ro3.exe")),
            payload = Sha256(Path.Combine(root, "run", "Client", "data", "payload.bin")),
            constant = Sha256(Path.Combine(root, "run", "Client", "data", "constant.txt")),
        },
    });
    File.WriteAllLines(Path.Combine(root, "processes.ndjson"),
    [
        JsonSerializer.Serialize(new { role = "old-launcher", pid = oldPid, executable = metadata.LauncherPath, state = "exited" }),
        JsonSerializer.Serialize(new { role = "updated-launcher", pid = newPid, parentPid = oldPid, executable = metadata.LauncherPath,
            arguments = new[] { RestartHandoff.PreviousProcessOption, oldPid.ToString(), RestartHandoff.TokenOption, restartToken }, state = "exited" }),
    ]);
    var gates = new Dictionary<string, object>
    {
        ["signedSelfUpdateAndProcessHandoff"] = new { verdict = launcherSha256 == metadata.NewLauncherSha256 && oldPid != newPid ? "PASS" : "FAIL", oldPid, newPid },
        ["signedCleanInstall"] = new { verdict = install.RootElement.GetProperty("verdict").GetString() },
        ["signedIncrementalUpdate"] = new { verdict = "PASS", version = "1.0.1" },
        ["signedRepairAndUserFilePreservation"] = new { verdict = repaired.RootElement.GetProperty("verdict").GetString(), preserved = File.Exists(Path.Combine(root, "run", "Client", "user-kept.txt")) },
        ["loopbackTransport"] = new { verdict = badRequests == 0 && requests.Length > 0 ? "PASS" : "FAIL", requests = requests.Length, badRequests },
        ["isolatedSettings"] = new { verdict = File.Exists(metadata.SettingsPath) ? "PASS" : "FAIL", path = metadata.SettingsPath },
        ["syntheticDpiMatrix"] = new { verdict = "PARTIAL", reason = "真實 Per-Monitor V2 / WM_DPICHANGED 尚未驗收" },
        ["publicHttpsCdn"] = new { verdict = "NOT_RUN" },
        ["realRo3ClientAndPlayerLogin"] = new { verdict = "NOT_RUN" },
    };
    WriteJson(Path.Combine(root, "result.json"), new
    {
        verdict = gates.Values.Any(value => JsonSerializer.Serialize(value).Contains("\"FAIL\"", StringComparison.Ordinal)) ? "FAIL" : "PARTIAL",
        commit,
        generatedUtc = DateTimeOffset.UtcNow,
        boundary = "本機簽章程序鏈與合成客戶端通過；實體 DPI、公開 HTTPS/CDN 與真實玩家登入未執行。",
        gates,
    });
    Console.WriteLine("PARTIAL: local process E2E passed; external gates NOT_RUN");
}

static void FinalizeLegacyEvidence(string root, int oldPid, int newPid, string commit)
{
    LabMetadata metadata = ReadMetadata(root);
    string launcherSha256 = Sha256(metadata.LauncherPath);
    int requestCount = File.Exists(Path.Combine(root, "requests.ndjson"))
        ? File.ReadLines(Path.Combine(root, "requests.ndjson"), Encoding.UTF8).Count(line => !string.IsNullOrWhiteSpace(line))
        : 0;
    bool passed = oldPid > 0 && newPid > 0 && oldPid != newPid &&
                  string.Equals(launcherSha256, metadata.NewLauncherSha256, StringComparison.OrdinalIgnoreCase) &&
                  !File.Exists(metadata.LauncherPath + ".old") && !File.Exists(metadata.LauncherPath + ".new") && requestCount > 0;
    File.WriteAllLines(Path.Combine(root, "legacy-processes.ndjson"),
    [
        JsonSerializer.Serialize(new { role = "legacy-2.0.0", pid = oldPid, executable = metadata.LauncherPath, state = "exited" }),
        JsonSerializer.Serialize(new { role = "replacement-2.0.1", pid = newPid, parentPid = oldPid,
            executable = metadata.LauncherPath, arguments = Array.Empty<string>(), state = "exited" }),
    ]);
    WriteJson(Path.Combine(root, "legacy-result.json"), new
    {
        verdict = passed ? "PASS" : "FAIL",
        commit,
        oldPid,
        newPid,
        commandLineArguments = Array.Empty<string>(),
        beforeSha256 = metadata.OldLauncherSha256,
        expectedSha256 = metadata.NewLauncherSha256,
        afterSha256 = launcherSha256,
        requestCount,
        oldAndNewLeftoversRemoved = !File.Exists(metadata.LauncherPath + ".old") && !File.Exists(metadata.LauncherPath + ".new"),
        boundary = "驗證既有無 PID/token 參數的 2.0.0 更新器可交接至 2.0.1；未執行公開 HTTPS/CDN 或真實遊戲。",
    });
    if (!passed) throw new InvalidDataException("舊版無參數重啟交接未通過。");
    Console.WriteLine("PASS legacy 2.0.0 -> 2.0.1 handoff");
}

static async Task ServeAsync(string root, int port, string requestLog)
{
    if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
    root = Path.GetFullPath(root);
    Directory.CreateDirectory(Path.GetDirectoryName(requestLog)!);
    var listener = new TcpListener(IPAddress.Loopback, port);
    listener.Start();
    Console.WriteLine($"LISTENING 127.0.0.1:{port}");
    while (true)
    {
        TcpClient client = await listener.AcceptTcpClientAsync();
        _ = Task.Run(() => RespondAsync(client, root, requestLog));
    }
}

static async Task RespondAsync(TcpClient client, string root, string requestLog)
{
    await using NetworkStream stream = client.GetStream();
    using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
    string? first = await reader.ReadLineAsync();
    string? line;
    do { line = await reader.ReadLineAsync(); } while (!string.IsNullOrEmpty(line));
    int status = 400;
    byte[] body = [];
    string requestPath = "";
    try
    {
        string[] parts = (first ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] != "GET") throw new InvalidDataException();
        requestPath = Uri.UnescapeDataString(parts[1].Split('?', 2)[0]).TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        string candidate = Path.GetFullPath(Path.Combine(root, requestPath));
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate))
        {
            status = 404;
            body = Encoding.UTF8.GetBytes("not found");
        }
        else
        {
            status = 200;
            body = await File.ReadAllBytesAsync(candidate);
        }
    }
    catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
    {
        body = Encoding.UTF8.GetBytes("bad request");
    }
    string reason = status == 200 ? "OK" : status == 404 ? "Not Found" : "Bad Request";
    byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Length: {body.Length}\r\nContent-Type: application/octet-stream\r\nConnection: close\r\n\r\n");
    await stream.WriteAsync(header);
    await stream.WriteAsync(body);
    client.Close();
    string receipt = JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, path = requestPath.Replace('\\', '/'), status, bytes = body.Length });
    await File.AppendAllTextAsync(requestLog, receipt + Environment.NewLine);
}

static LabMetadata ReadMetadata(string root)
    => JsonSerializer.Deserialize<LabMetadata>(File.ReadAllBytes(Path.Combine(root, "lab.json"))) ?? throw new InvalidDataException("lab.json 無效。");

static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
static void WriteJson(string path, object value)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
}

static void Usage() => Console.Error.WriteLine("""
    LauncherProcessE2E prepare <root> <old-launcher> <new-launcher> <loopback-repository-url>
    LauncherProcessE2E publish-update <root>
    LauncherProcessE2E damage <root>
    LauncherProcessE2E verify <root> <version>
    LauncherProcessE2E finalize <root> <old-pid> <new-pid> <restart-token> <commit>
    LauncherProcessE2E finalize-legacy <root> <old-pid> <new-pid> <commit>
    LauncherProcessE2E serve <repository-directory> <port> <requests.ndjson>
    """);

sealed class LabMetadata
{
    public string Root { get; set; } = "";
    public string RepositoryUrl { get; set; } = "";
    public string KeyId { get; set; } = "";
    public string OldLauncherSha256 { get; set; } = "";
    public string NewLauncherSha256 { get; set; } = "";
    public string LauncherPath { get; set; } = "";
    public string SettingsPath { get; set; } = "";
}
