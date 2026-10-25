using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Ro3Emu.Patcher;

namespace Ro3Emu.Launcher;

/// <summary>
/// The launcher's logic behind the page: checks the channel, installs/updates/repairs the client with
/// <see cref="Updater"/>, updates the launcher itself, pings the game server and starts ro3.exe.
/// Messages to the page: state, progress, server, settings, news (see ui/app.js).
/// </summary>
public sealed class LauncherController : IDisposable
{
    readonly LauncherConfig _config;
    readonly string _exePath;
    readonly Action<object> _post;
    readonly Func<string, string?> _pickFolder;
    readonly Action _minimize;
    readonly UserSettings _settings = UserSettings.Load();
    readonly RepositoryClient _repo;
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    readonly string _version = typeof(LauncherController).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    readonly CancellationTokenSource _life = new();
    InstallLayout _layout;
    ChannelInfo? _channel;
    LocalState? _installed;
    string _phase = "checking";
    CancellationTokenSource? _work;
    bool _started;

    public LauncherController(LauncherConfig config, string exePath, Action<object> post, Func<string, string?> pickFolder, Action minimize)
    {
        _config = config;
        _exePath = exePath;
        _post = post;
        _pickFolder = pickFolder;
        _minimize = minimize;
        _repo = new RepositoryClient(new Uri(config.Repository), config.RepositoryTrust);
        _layout = new InstallLayout(_settings.ClientDirectory ?? Path.Combine(Path.GetDirectoryName(exePath)!, "Client"));
    }

    public async Task HandleAsync(string cmd)
    {
        switch (cmd)
        {
            case "ready":
                PostSettings();
                if (!_started)
                {
                    _started = true;
                    _ = PingLoopAsync();
                    _ = LoadNewsAsync();
                }
                await CheckAsync();
                break;
            case "primary":
                if (_work != null) return;
                if (_phase is "ready" or "running") await PlayAsync();
                else if (_phase is "install" or "update" or "repair") await UpdateAsync(verifyAll: _phase == "repair");
                else await CheckAsync();
                break;
            case "repair":
                if (_work == null) await UpdateAsync(verifyAll: true);
                break;
            case "pause":
                _work?.Cancel();
                break;
            case "changeDir":
                await ChangeDirectoryAsync();
                break;
            case "openDir":
                if (Directory.Exists(_layout.GameDirectory))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_layout.GameDirectory}\"") { UseShellExecute = true });
                break;
        }
    }

    void PostSettings() => _post(new
    {
        type = "settings",
        dir = _layout.GameDirectory,
        launcher = _version,
        serverName = _config.ServerName,
    });

    void PostState(string phase, string? message = null)
    {
        _phase = phase;
        _post(new { type = "state", phase, installed = _installed?.Version, remote = _channel?.Version, message });
    }

    async Task CheckAsync()
    {
        PostState("checking");
        try
        {
            var updater = new Updater(_layout, _repo);
            var check = await updater.CheckAsync(_config.Channel, _life.Token);
            _channel = check.Channel;
            if (_config.RequireSignedChannel) _settings.AcceptChannelSequence(_channel.Channel, _channel.Sequence);
            _installed = check.Installed;
            if (_channel.Launcher is { } release && SelfUpdate.IsNewer(release.Version, _version))
            {
                PostState("checking", $"正在將啟動器更新至 {release.Version}…");
                if (await SelfUpdate.ApplyAsync(_repo, release, _version, _exePath, _life.Token))
                {
                    SelfUpdate.Restart(_exePath);
                    System.Windows.Application.Current.Dispatcher.Invoke(() => System.Windows.Application.Current.Shutdown());
                    return;
                }
            }
            if (check.Installed is null)
            {
                bool existing = File.Exists(Path.Combine(_layout.GameDirectory, "ro3.exe"));
                PostState("install", existing ? "已在此資料夾找到 RO3 客戶端，只會下載缺少或已變更的檔案。" : null);
            }
            else if (check.UpdateRequired) PostState("update", $"已有遊戲版本 {_channel.Version} 可供更新。");
            else if (updater.QuickVerify(check.Installed) is { Count: > 0 } changed) PostState("repair", $"有 {changed.Count} 個遊戲檔案與目前版本不一致。");
            else PostState("ready");
        }
        catch (Exception e) when (e is PatcherException or HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            _installed = new Updater(_layout, _repo).LoadState();
            // Offline but installed: the game can still start (the update server and the game server are separate).
            PostState(_installed is not null ? "ready" : "offline", _installed is not null ? "目前無法連線至更新伺服器，仍可啟動已安裝的版本。" : null);
        }
    }

    async Task UpdateAsync(bool verifyAll)
    {
        if (_channel is null)
        {
            await CheckAsync();
            if (_channel is null) return;
        }
        Directory.CreateDirectory(_layout.GameDirectory);
        _work = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        PostState("updating");
        var meter = new SpeedMeter();
        var progress = new ThrottledProgress(p =>
        {
            var (rate, eta) = p.Stage == UpdateStage.Downloading ? meter.Sample(p.BytesDone, p.BytesTotal) : (0, 0);
            _post(new { type = "progress", stage = p.Stage.ToString().ToLowerInvariant(), done = p.BytesDone, total = p.BytesTotal, rate, eta });
        });
        try
        {
            var updater = new Updater(_layout, _repo);
            var channel = _channel;
            var token = _work.Token;
            await Task.Run(() => updater.UpdateAsync(channel, verifyAll, progress, token));
            _work.Dispose();
            _work = null;
            await CheckAsync();
        }
        catch (OperationCanceledException)
        {
            _work?.Dispose();
            _work = null;
            PostState(_installed is null ? "install" : "update", "下載已暫停；已下載的內容會保留，之後可繼續。");
        }
        catch (Exception e)
        {
            _work?.Dispose();
            _work = null;
            PostState("error", $"更新失敗：{e.Message}");
        }
    }

    async Task PlayAsync()
    {
        PostState("launching");
        try
        {
            string? failure = await GameStarter.StartAsync(_layout.GameDirectory);
            if (failure is null)
            {
                PostState("running");
                _minimize();
            }
            else PostState("repair", $"遊戲未能啟動：{failure}。");
        }
        catch (Exception e)
        {
            PostState("error", $"啟動失敗：{e.Message}");
        }
    }

    async Task ChangeDirectoryAsync()
    {
        if (_work != null) return;
        string? folder = _pickFolder("遊戲資料夾（既有的 ROO_PC 資料夾，或空白資料夾）");
        if (folder is null) return;
        // An existing client (ro3.exe) or an empty folder is used as is; anything else gets its own subfolder.
        bool usable = File.Exists(Path.Combine(folder, "ro3.exe")) || !Directory.EnumerateFileSystemEntries(folder).Any();
        if (!usable) folder = Path.Combine(folder, "RO3Emu");
        _settings.ClientDirectory = folder;
        _settings.Save();
        _layout = new InstallLayout(folder);
        _installed = null;
        PostSettings();
        await CheckAsync();
    }

    async Task PingLoopAsync()
    {
        if (string.IsNullOrEmpty(_config.Server)) return;
        string server = _config.Server.TrimEnd('/');
        while (!_life.IsCancellationRequested)
        {
            bool online = false;
            int? players = null;
            try
            {
                using var status = await _http.GetAsync(server + "/ro3emu/status", _life.Token);
                if (status.IsSuccessStatusCode)
                {
                    online = true;
                    using var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync(_life.Token));
                    if (json.RootElement.TryGetProperty("players", out var p)) players = p.GetInt32();
                }
                else
                {
                    // Servers without /ro3emu/status: any answer from the gate-key endpoint means up.
                    using var key = await _http.GetAsync(server + "/ro3emu/gate-key.pem", _life.Token);
                    online = key.IsSuccessStatusCode;
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { }
            if (_life.IsCancellationRequested) return;
            _post(new { type = "server", online, players });
            try { await Task.Delay(TimeSpan.FromSeconds(30), _life.Token); }
            catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>news.json in the repository root: { "items": [ { tag, color, title, text } ] }.</summary>
    async Task LoadNewsAsync()
    {
        try
        {
            byte[]? bytes = await _repo.TryGetBytesAsync("news.json", _life.Token);
            if (bytes is null) return;
            using var json = JsonDocument.Parse(bytes);
            if (json.RootElement.TryGetProperty("items", out var items))
            {
                JsonElement? banners = json.RootElement.TryGetProperty("banners", out var bannerItems) ? bannerItems.Clone() : null;
                _post(new { type = "news", items = items.Clone(), banners });
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or PatcherException) { }
    }

    public void Dispose()
    {
        _life.Cancel();
        _work?.Cancel();
    }

    /// <summary>Download speed over the last ~5 seconds and the remaining seconds at that speed.</summary>
    sealed class SpeedMeter
    {
        readonly Stopwatch _clock = Stopwatch.StartNew();
        readonly Queue<(double At, long Bytes)> _samples = new();

        public (double Rate, double Eta) Sample(long done, long total)
        {
            double now = _clock.Elapsed.TotalSeconds;
            _samples.Enqueue((now, done));
            while (_samples.Count > 2 && now - _samples.Peek().At > 5) _samples.Dequeue();
            var first = _samples.Peek();
            double seconds = now - first.At;
            if (seconds < 1) return (0, 0);
            double rate = (done - first.Bytes) / seconds;
            return rate <= 0 ? (0, 0) : (rate, (total - done) / rate);
        }
    }
}
