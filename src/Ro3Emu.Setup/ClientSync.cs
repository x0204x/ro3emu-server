using System.Security.Cryptography;
using System.Text.Json;
using Ro3Emu.ClientRelease;
using Ro3Emu.ClientTools;
using Ro3Emu.Patcher;

namespace Ro3Emu.Setup;

/// <summary>只有明確啟用才使用完整配對流程；私鑰路徑不會傳給伺服器或 HTTP。</summary>
public sealed class ClientSyncOptions
{
    public bool Enabled { get; set; }
    public string? ReleaseSpecPath { get; set; }
    public string? RepoDirectory { get; set; }
    public string Channel { get; set; } = "stable";
    public string? SigningKeyPath { get; set; }
    public string? KeyId { get; set; }
    public string? LocalizationMap { get; set; }
    public string? TraditionalChinesePack { get; set; }
}

/// <summary>發行者提供的伺服器／Genesis 配對，不從資料夾名稱或官方 latest 猜版本。</summary>
public sealed record ClientCompatibilitySpec(string ServerVersion, long RequiredGenesisSequence,
    string? RequiredGenesisReleaseId = null, string? RequiredGenesisBuildId = null)
{
    public void Validate(string actualServerVersion)
    {
        if (!ClientReleaseRequirement.IsText(ServerVersion) || RequiredGenesisSequence <= 0
            || (RequiredGenesisReleaseId is not null && !ClientReleaseRequirement.IsText(RequiredGenesisReleaseId))
            || (RequiredGenesisBuildId is not null && !ClientReleaseRequirement.IsText(RequiredGenesisBuildId))
            || !(new ClientReleaseRequirement { ServerVersion = ServerVersion }).MatchesServerVersion(actualServerVersion))
            throw new InvalidDataException("發行配對設定缺失或與目前伺服器版本不符，拒絕自動更新及開服。");
    }

    public bool Matches(GenesisIdentity identity) => identity.Sequence == RequiredGenesisSequence
        && (RequiredGenesisReleaseId is null || identity.ReleaseId == RequiredGenesisReleaseId)
        && (RequiredGenesisBuildId is null || identity.BuildId == RequiredGenesisBuildId);

    public void ValidateOffered(long sequence, string releaseId, string buildId)
    {
        if (sequence != RequiredGenesisSequence || (RequiredGenesisReleaseId is not null && releaseId != RequiredGenesisReleaseId)
            || (RequiredGenesisBuildId is not null && buildId != RequiredGenesisBuildId))
            throw new InvalidDataException("Genesis 更新來源提供的版本與本伺服器配對不符；尚未寫入客戶端，已停止。");
    }
}

/// <summary>測試只需記錄這些邊界，不必下載遊戲、載入原生 Lua 或產生真正的 24 GB 套件。</summary>
internal sealed class ClientSyncOperations
{
    public required Func<GenesisIdentity?> ReadIdentity { get; init; }
    public required Func<ClientCompatibilitySpec, CancellationToken, Task> UpdateOfficial { get; init; }
    public required Action ValidateSources { get; init; }
    public required Action Repatch { get; init; }
    public required Action ValidateData { get; init; }
    public required Func<string> PatchFingerprint { get; init; }
    public required Func<ClientReleaseRequirement?> ReadPublished { get; init; }
    public required Func<GenesisIdentity, string, CancellationToken, Task<ClientReleaseRequirement>> PrepareCandidate { get; init; }
    public required Action ActivateCandidate { get; init; }
}

internal static class ClientSyncCoordinator
{
    public static async Task<ClientReleaseRequirement> PrepareAsync(ClientCompatibilitySpec spec, string serverVersion,
        ClientSyncOperations steps, CancellationToken ct = default)
    {
        spec.Validate(serverVersion);
        var identity = steps.ReadIdentity();
        bool update = identity is null || !spec.Matches(identity);
        if (!update)
        {
            try { steps.ValidateSources(); }
            catch (Exception e) when (e is IOException or InvalidDataException) { update = true; }
        }
        if (update)
        {
            await steps.UpdateOfficial(spec, ct);
            identity = steps.ReadIdentity();
            if (identity is null || !spec.Matches(identity)) throw new InvalidDataException("更新後的 Genesis 版本仍不符，拒絕繼續。");
            steps.ValidateSources();
        }
        identity!.Validate();
        steps.Repatch();
        steps.ValidateData();
        string patch = steps.PatchFingerprint();
        if (!ClientReleaseRequirement.IsHash(patch)) throw new InvalidDataException("客戶端修補指紋格式錯誤。");
        var published = steps.ReadPublished();
        if (published is not null && Matches(published, identity, patch, serverVersion)) return published;
        var candidate = await steps.PrepareCandidate(identity, patch, ct);
        candidate.Validate();
        if (!Matches(candidate, identity, patch, serverVersion)
            || (published is not null && candidate.Sequence <= published.Sequence))
            throw new InvalidDataException("候選玩家套件與本次已驗來源不符，保留舊更新通道，拒絕開服。");
        steps.ActivateCandidate();
        return candidate;
    }

    private static bool Matches(ClientReleaseRequirement r, GenesisIdentity identity, string patch, string version)
    {
        r.Validate();
        return r.BaseClient == identity && r.PatchSha256 == patch && r.MatchesServerVersion(version);
    }
}

internal static class ClientSync
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public static async Task<ClientReleaseRequirement> PrepareAsync(ClientSyncOptions policy, string serverVersion,
        string client, string serverUrl, Options options, Action<string, string, string?> patchClient, CancellationToken ct = default)
    {
        // 所有必要設定先查齊，錯誤不會以 legacy 模式悄悄繼續。
        if (!policy.Enabled || string.IsNullOrWhiteSpace(policy.ReleaseSpecPath) || string.IsNullOrWhiteSpace(policy.RepoDirectory)
            || string.IsNullOrWhiteSpace(policy.SigningKeyPath) || !ClientReleaseRequirement.IsText(policy.KeyId)
            || string.IsNullOrWhiteSpace(policy.Channel) || policy.Channel.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
            throw new InvalidDataException("ClientSync 啟用時必須提供發行配對、更新站、通道及簽署金鑰設定。");
        policy.ReleaseSpecPath = AtHome(policy.ReleaseSpecPath);
        policy.RepoDirectory = AtHome(policy.RepoDirectory);
        policy.SigningKeyPath = AtHome(policy.SigningKeyPath);
        if (policy.LocalizationMap is not null) policy.LocalizationMap = AtHome(policy.LocalizationMap);
        if (policy.TraditionalChinesePack is not null) policy.TraditionalChinesePack = AtHome(policy.TraditionalChinesePack);
        var spec = JsonSerializer.Deserialize<ClientCompatibilitySpec>(File.ReadAllBytes(policy.ReleaseSpecPath), Json)
            ?? throw new InvalidDataException("發行配對不可為 null。");
        spec.Validate(serverVersion);
        string key = File.ReadAllText(policy.SigningKeyPath);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(key);
        if (rsa.KeySize < 3072) throw new InvalidDataException("玩家更新簽署金鑰至少需要 RSA 3072 位元。");
        _ = rsa.ExportRSAPrivateKey();
        var signing = new RepositorySigningOptions(policy.KeyId!, key);
        var trust = new RepositoryTrustOptions
        {
            RequireSignedChannel = true,
            TrustedPublicKeys = new Dictionary<string, string> { [policy.KeyId!] = rsa.ExportSubjectPublicKeyInfoPem() }
        };
        string repo = Path.GetFullPath(policy.RepoDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        PublicRepositorySnapshot.EnsureNoLinks(repo);
        EnsureSeparated(repo, client);
        EnsureSeparated(repo, SetupHome.DataDir);
        if (Path.GetFullPath(policy.SigningKeyPath).StartsWith(repo + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("簽署私鑰不可放在公開更新站目錄內。");
        string? candidate = null;
        string? gatePem = null;
        IDisposable? repositoryLock = null;
        var steps = new ClientSyncOperations
        {
            ReadIdentity = () => { try { return VerifiedBaseline.ReadIdentity(client); } catch (Exception e) when (e is IOException or InvalidDataException) { return null; } },
            UpdateOfficial = (required, stop) => InstallFlow.InstallAsync(client, options, stop: stop, required: required),
            ValidateSources = () => VerifiedBaseline.ValidateSources(client),
            Repatch = () => { gatePem = EnsureGateKey(SetupHome.DataDir); patchClient(client, serverUrl, gatePem); },
            ValidateData = () =>
            {
                if (File.Exists(Path.Combine(SetupHome.DataDir, PreparedGameData.MarkerName)))
                    Flows.EnsureGameData(client, options.Reexport, !options.NoIcons);
                else NativeClientData.Ensure(SetupHome.DataDir, VerifiedBaseline.ReadIdentity(client), options.Reexport,
                    force => Flows.EnsureGameData(client, force, !options.NoIcons),
                    () => Ro3Emu.DataExport.GameDataExport.ReadStamp(SetupHome.DataDir),
                    () => Ro3Emu.DataExport.GameDataExport.Fingerprint(client));
            },
            PatchFingerprint = () => ReleaseBuilder.GetPatchFingerprint(client, serverUrl, gatePem!, policy.LocalizationMap, policy.TraditionalChinesePack),
            ReadPublished = () =>
            {
                // 原 repo 外的穩定鎖也由所有 Publisher 入口共用，長時間建立候選不會覆蓋另一個發布。
                repositoryLock = Publisher.AcquireRepositoryLock(repo);
                return File.Exists(Path.Combine(repo, ClientReleaseRequirement.FileName))
                    ? PublicRepositorySnapshot.Verify(repo, policy.Channel, trust, ct) : null;
            },
            PrepareCandidate = async (identity, patch, stop) =>
            {
                stop.ThrowIfCancellationRequested();
                string parent = Path.GetDirectoryName(repo)!;
                PublicRepositorySnapshot.EnsureNoLinks(parent);
                Directory.CreateDirectory(parent);
                candidate = Path.Combine(parent, "." + Path.GetFileName(repo) + ".candidate-" + Guid.NewGuid().ToString("N"));
                PublicRepositorySnapshot.ValidateActivation(repo, candidate);
                Directory.CreateDirectory(candidate);
                PublicRepositorySnapshot.Clone(repo, candidate);
                string serverTag = Hashing.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(serverVersion))[..8];
                string version = $"{serverVersion.Split('+')[0]}-g{identity.Sequence}-{identity.BaselineSha256[..8]}-{patch[..12]}-s{serverTag}";
                int code = await ReleaseBuilder.PublishAsync(client, serverUrl, gatePem!, candidate, version,
                    policy.Channel, 19, policy.LocalizationMap, signing: signing, traditionalChinesePack: policy.TraditionalChinesePack,
                    serverVersion: serverVersion);
                if (code != 0) throw new InvalidDataException("玩家更新候選建立失敗；保留舊通道並停止開服。");
                return PublicRepositorySnapshot.Verify(candidate, policy.Channel, trust, stop);
            },
            ActivateCandidate = () => Activate(repo, candidate ?? throw new InvalidOperationException("沒有完整候選可啟用。")),
        };
        try { return await ClientSyncCoordinator.PrepareAsync(spec, serverVersion, steps, ct); }
        finally { repositoryLock?.Dispose(); }
    }

    internal static void Activate(string repo, string candidate)
    {
        PublicRepositorySnapshot.ValidateActivation(repo, candidate);
        // 同一個檔案系統切換完整 snapshot；失敗會把舊 snapshot 放回，候選及 rollback 都保留。
        string rollback = repo + ".rollback-" + Guid.NewGuid().ToString("N");
        PublicRepositorySnapshot.EnsureNoLinks(rollback);
        bool hadOld = Directory.Exists(repo);
        if (hadOld) Directory.Move(repo, rollback);
        try { Directory.Move(candidate, repo); }
        catch { if (hadOld && !Directory.Exists(repo)) Directory.Move(rollback, repo); throw; }
    }

    private static string EnsureGateKey(string data)
    {
        Directory.CreateDirectory(data);
        string file = Path.Combine(data, "gate_rsa_private.pem");
        using var gate = RSA.Create();
        if (File.Exists(file)) gate.ImportFromPem(File.ReadAllText(file));
        else
        {
            gate.KeySize = 2048;
            using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            writer.Write(gate.ExportRSAPrivateKeyPem());
        }
        return gate.ExportRSAPublicKeyPem();
    }
    private static string AtHome(string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(SetupHome.Root, path));
    private static void EnsureSeparated(string repo, string privateDirectory)
    {
        string other = Path.GetFullPath(privateDirectory).TrimEnd(Path.DirectorySeparatorChar);
        string target = repo.TrimEnd(Path.DirectorySeparatorChar);
        if (target.Equals(other, StringComparison.OrdinalIgnoreCase) || target.StartsWith(other + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || other.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("公開更新站必須與客戶端及伺服器資料目錄分開。");
    }
    internal static void WriteAtomic(string path, object value)
    {
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(value, Json));
        File.Move(temporary, path, true);
    }
}
