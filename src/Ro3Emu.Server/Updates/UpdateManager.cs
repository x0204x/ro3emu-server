using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Ro3Emu.Panel;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Http;

namespace Ro3Emu.Server.Updates;

/// <summary>
/// The update manager of the panel's Server control (docs/guide/panel.md, Updates). It compares the running version
/// with the releases on GitHub (asked when the page asks, at most once a minute, and every Ro3Emu:Updates:CheckHours
/// in the background while the panel is on; the answer is kept in updates/releases.json), downloads and stages a newer
/// release (UpdatePackage), and installs or rolls back only when an Admin plans it: a restart with the countdown in
/// the game whose last step backs up the database and swaps the files (UpdateInstaller), after which the process
/// manager starts the new version. What the page may do depends on how the server runs (<see cref="UpdateHosting"/>).
/// Every action is in the audit log (action "update", target server) and installs and rollbacks in updates/history.jsonl,
/// which a database restore does not rewind.
/// </summary>
public sealed partial class UpdateManager : BackgroundService, IPanelUpdates
{
    /// <summary>The shortest gap between two checks the page asks for; GitHub allows 60 an hour per address.</summary>
    private static readonly TimeSpan CheckSpacing = TimeSpan.FromMinutes(1);
    /// <summary>The first background check waits this long after a start, so a server that restarts often asks rarely.</summary>
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(2);
    private const int HistoryShown = 20, HistoryKept = 200;

    private readonly ServerOptions _options;
    private readonly UpdatePaths _paths;
    private readonly HttpClient _http;
    private readonly ReleaseFeed _feed;
    private readonly DatabaseBackup _backups;
    private readonly AccountStore _accounts;
    private readonly PanelOperations _ops;
    private readonly TimeProvider _clock;
    private readonly ILogger<UpdateManager> _log;
    private readonly UpdatePlatform? _platform;
    private readonly string _platformName, _running;
    private readonly ReleaseVersion? _installed;

    private readonly object _gate = new();
    private FeedCache _cache = new([], null, null, null, null, null);
    private UpdateJob _job = UpdateJob.Idle;
    private CancellationTokenSource? _download;
    private StagedRecord? _staged;
    private bool _installing;

    public UpdateHosting Hosting { get; }

    /// <param name="handler">The HTTP stack for GitHub (tests give a fake); null: the default one.</param>
    /// <param name="probe">What the hosting detection looks at (tests give another machine's); null: this one's.</param>
    public UpdateManager(IOptions<ServerOptions> options, DatabaseBackup backups, AccountStore accounts, PanelOperations ops, TimeProvider clock,
        ILogger<UpdateManager> log, HttpMessageHandler? handler = null, UpdateHosting.Probe? probe = null)
    {
        _options = options.Value;
        _paths = new UpdatePaths(_options.DataDir);
        _http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan; // each request has its own (ReleaseFeed) or is cancelled by the admin
        _feed = new ReleaseFeed(_http, _options.Updates, clock);
        (_backups, _accounts, _ops, _clock, _log) = (backups, accounts, ops, clock, log);
        Hosting = UpdateHosting.Detect(_options.Updates, probe ?? UpdateHosting.Probe.Current());
        _platformName = UpdatePlatform.Current();
        _platform = UpdatePlatform.Of(_platformName);
        _running = ReleaseVersion.Running;
        _installed = ReleaseVersion.TryParse(_running, out var installed) ? installed : null;
    }

    /// <summary>The running version without its commit: "1.0.24".</summary>
    private string Installed => _installed?.ToString() ?? _running;

    private string UserAgent => "RO3Emu-Server/" + Installed;

    private DateTimeOffset Now => _clock.GetUtcNow();

    /// <summary>The releases list as last asked, and when to ask again.</summary>
    private sealed record FeedCache(List<ReleaseInfo> Releases, string? ETag, DateTimeOffset? Checked, DateTimeOffset? Attempted, string? Error,
        DateTimeOffset? RetryAt);

    /// <param name="Phase">idle, downloading, extracting, checking or failed.</param>
    private sealed record UpdateJob(string Phase, string? Tag, long Done, long Total, string? Message)
    {
        public static readonly UpdateJob Idle = new("idle", null, 0, 0, null);
        public bool Running => Phase is "downloading" or "extracting" or "checking";
    }

    // ---------------------------------------------------------------- start: finish what the last run began

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        try { Recover(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { LogRecoverFailed(_log, e); }
        return base.StartAsync(cancellationToken);
    }

    /// <summary>
    /// The staged release (when its files are still there), the cached releases list, the outcome of an install or
    /// rollback that restarted into this run, and the *.old files its swap left.
    /// </summary>
    private void Recover()
    {
        if (!Directory.Exists(_paths.Root)) return;
        if (Directory.Exists(_paths.Downloads))
            foreach (string part in Directory.EnumerateFiles(_paths.Downloads, "*.part")) UpdatePackage.TryDelete(part);
        var staged = UpdateInstaller.ReadJson<StagedRecord>(_paths.Staged);
        if (staged != null && staged.Platform == _platformName && File.Exists(Path.Combine(staged.Folder, _platform?.ServerFile ?? "-"))) _staged = staged;
        if (UpdateInstaller.ReadJson<FeedCache>(_paths.Feed) is { } cache) _cache = cache with { Error = null };
        if (Hosting.InstallDir is { } install) UpdateInstaller.DeleteLeftovers(install);
        if (UpdateInstaller.ReadJson<PendingRecord>(_paths.Pending) is { } pending) _ = ConfirmAsync(pending);
    }

    /// <summary>The first start after an install or rollback says whether it came back as the planned version.</summary>
    private async Task ConfirmAsync(PendingRecord pending)
    {
        UpdatePackage.TryDelete(_paths.Pending);
        bool planned = ReleaseVersion.TryParse(pending.To, out var to) && to == _installed;
        string detail = planned ? "The server started as " + Installed + "." : "The server started as " + Installed + ", not the planned " + pending.To + ".";
        AppendHistory(new HistoryRecord(Now, planned ? "started" : "failed", pending.From, Installed, pending.By, detail));
        try { await AuditAsync(pending.By, (pending.Kind == "rollback" ? "rollback: " : "install: ") + detail); }
        catch (Exception e) when (e is not OperationCanceledException) { LogRecoverFailed(_log, e); }
    }

    // ---------------------------------------------------------------- checks

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Updates.Check || !_options.Panel.Enabled) return;
        var interval = TimeSpan.FromHours(Math.Max(1, _options.Updates.CheckHours));
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                FeedCache cache;
                lock (_gate) cache = _cache;
                var due = (cache.Attempted ?? DateTimeOffset.MinValue) + interval;
                if (cache.RetryAt > due) due = cache.RetryAt.Value;
                var wait = due - Now;
                await Task.Delay(wait < FirstCheck ? FirstCheck : wait, _clock, stoppingToken);
                await CheckCoreAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) { } // stopping
    }

    public async Task<PanelUpdateState> CheckAsync(string admin)
    {
        bool ask;
        lock (_gate) ask = _options.Updates.Check && !(Now - _cache.Attempted < CheckSpacing) && !(_cache.RetryAt > Now);
        if (ask) await CheckCoreAsync(CancellationToken.None);
        return State();
    }

    private async Task CheckCoreAsync(CancellationToken ct)
    {
        string? etag;
        lock (_gate)
        {
            _cache = _cache with { Attempted = Now };
            // Without a list there is nothing a 304 could confirm.
            etag = _cache.Releases.Count > 0 ? _cache.ETag : null;
        }
        var response = await _feed.FetchAsync(etag, UserAgent, ct);
        FeedCache cache;
        lock (_gate)
        {
            _cache = cache = response switch
            {
                { Releases: { } list } => _cache with { Releases = [.. list], ETag = response.ETag, Checked = Now, Error = null, RetryAt = null },
                { NotModified: true } => _cache with { Checked = Now, Error = null, RetryAt = null },
                _ => _cache with { Error = response.Error, RetryAt = response.RetryAt },
            };
        }
        if (response.Error != null) LogCheckFailed(_log, response.Error);
        try { UpdateInstaller.WriteJson(_paths.Feed, cache); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { LogRecoverFailed(_log, e); }
    }

    // ---------------------------------------------------------------- the page's state

    public Task<PanelUpdateState> StateAsync() => Task.FromResult(State());

    private PanelUpdateState State()
    {
        FeedCache cache;
        UpdateJob job;
        StagedRecord? staged;
        bool installing;
        lock (_gate) (cache, job, staged, installing) = (_cache, _job, _staged, _installing);
        var newer = cache.Releases.Where(r => _installed is not { } v || r.Version > v).Select(Release).ToList();
        string commit = _running.IndexOf('+', StringComparison.Ordinal) is var plus and >= 0 ? _running[(plus + 1)..] : "";
        var interval = TimeSpan.FromHours(Math.Max(1, _options.Updates.CheckHours));
        DateTimeOffset? next = _options.Updates.Check && _options.Panel.Enabled ? Later((cache.Attempted ?? Now) + interval, cache.RetryAt) : null;
        return new PanelUpdateState(
            new PanelUpdateInstalled(Installed, commit.Length > 0 ? commit[..Math.Min(7, commit.Length)] : null, _platformName),
            HostingState(staged),
            new PanelUpdateCheck(_options.Updates.Check, _options.Updates.Repository, cache.Checked, next,
                _options.Updates.Check ? cache.Error : null),
            newer, cache.Releases.Count > 0 ? Release(cache.Releases[0]) : null,
            JobState(job, staged, installing),
            staged == null ? null : new PanelStagedUpdate(staged.Tag, staged.Version, staged.Folder, staged.Bytes, staged.Sha256, staged.Staged),
            PreviousState(), History());
    }

    private static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset? b) => b > a ? b.Value : a;

    private PanelRelease Release(ReleaseInfo r)
    {
        var asset = _platform?.Pick(r);
        return new PanelRelease(r.Tag, r.Version.ToString(), r.Name, r.Published, r.Notes, r.Page, asset?.Name, asset?.Size ?? 0, asset?.Sha256 != null);
    }

    /// <summary>A planned or running install or rollback comes first, then a download, then a staged release.</summary>
    private PanelUpdateJob JobState(UpdateJob job, StagedRecord? staged, bool installing)
    {
        if (installing) return new PanelUpdateJob("installing", staged?.Tag, 0, 0, null);
        if (_ops.PlannedKind is "update" or "rollback") return new PanelUpdateJob("planned", staged?.Tag, 0, 0, null);
        if (job.Phase != "idle") return new PanelUpdateJob(job.Phase, job.Tag, job.Done, job.Total, job.Message);
        return staged != null ? new PanelUpdateJob("staged", staged.Tag, 0, 0, null) : new PanelUpdateJob("idle", null, 0, 0, null);
    }

    private PanelPreviousVersion? PreviousState()
    {
        if (UpdateInstaller.ReadPrevious(_paths) is not { } previous) return null;
        var backup = previous.Backup is { } name ? new FileInfo(Path.Combine(_paths.Backups, name)) : null;
        return new PanelPreviousVersion(previous.Version, previous.Saved, backup is { Exists: true } ? backup.Name : null,
            backup is { Exists: true } ? backup.Length : 0);
    }

    // ---------------------------------------------------------------- history and audit

    private IReadOnlyList<PanelUpdateHistory> History()
    {
        if (!File.Exists(_paths.History)) return [];
        try
        {
            return [.. File.ReadLines(_paths.History).TakeLast(HistoryShown).Reverse()
                .Select(line => { try { return JsonSerializer.Deserialize<HistoryRecord>(line, UpdateInstaller.Json); } catch (JsonException) { return null; } })
                .OfType<HistoryRecord>().Select(h => new PanelUpdateHistory(h.Utc, h.Kind, h.From, h.To, h.By, h.Detail))];
        }
        catch (IOException) { return []; }
    }

    private void AppendHistory(HistoryRecord record)
    {
        try
        {
            Directory.CreateDirectory(_paths.Root);
            File.AppendAllText(_paths.History, JsonSerializer.Serialize(record, UpdateInstaller.Json) + "\n");
            var lines = File.ReadAllLines(_paths.History);
            if (lines.Length > HistoryKept * 2) File.WriteAllLines(_paths.History, lines[^HistoryKept..]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { LogRecoverFailed(_log, e); }
    }

    private Task<bool> AuditAsync(string admin, string detail) =>
        _accounts.WriteAsync<bool>(new AdminAuditWritePlan(Now.ToUnixTimeSeconds(), admin, AdminAudit.Update, "server", detail));

    private static string Stamp(DateTimeOffset at) => at.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Called twice: the container disposes the singleton and the hosted service registration that hands it out.</summary>
    public override void Dispose()
    {
        CancellationTokenSource? download;
        lock (_gate) (download, _download) = (_download, null);
        download?.Cancel();
        download?.Dispose();
        _http.Dispose();
        base.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Updates: the releases check failed: {Reason}")]
    private static partial void LogCheckFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Updates: the update folder could not be read or written")]
    private static partial void LogRecoverFailed(ILogger logger, Exception error);
}

/// <summary>A release unpacked and checked in updates/staging, ready to install.</summary>
internal sealed record StagedRecord(string Tag, string Version, string Folder, long Bytes, string Sha256, DateTimeOffset Staged, string Platform,
    string Archive);

/// <summary>An install or rollback that restarted the server: the next start confirms it (updates/pending.json).</summary>
internal sealed record PendingRecord(string Kind, string From, string To, string By, DateTimeOffset At);

/// <param name="Kind">install, rollback, started or failed.</param>
internal sealed record HistoryRecord(DateTimeOffset Utc, string Kind, string From, string To, string By, string Detail);
