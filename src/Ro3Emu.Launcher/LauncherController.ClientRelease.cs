using System.IO;
using System.Net.Http;
using System.Text.Json;
using Ro3Emu.Patcher;

namespace Ro3Emu.Launcher;

public sealed partial class LauncherController
{
    long? _verifiedGenesisSequence;

    async Task<bool> EnsureStrictReadyAsync(bool launching = false)
    {
        if (_work is not null || _launching || !RefreshGameProcesses()) return false;
        _verifiedGenesisSequence = null;
        _work = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        CancellationToken ct = _work.Token;
        PostState("updating", "正在核對正式機必要客戶端與完整檔案 SHA-256…");
        try
        {
            if (!Uri.TryCreate(_config.Server, UriKind.Absolute, out var server))
                throw new PatcherException("尚未設定正式機 API，無法確認必要客戶端。");
            using var required = new ServerClientReleaseClient(server);
            var sync = new ClientReleaseSynchronizer(new Updater(_layout, _repo), _repo, required.GetAsync,
                channel => _settings.AcceptChannelSequence(channel.Channel, channel.Sequence));
            bool running = _games.State.AnyRunning;
            var progress = StrictProgress();
            ClientReleaseReady ready = await sync.EnsureReadyAsync(_config.Channel, running,
                () => CanChangeStrictInstallation(ct), progress, ct);
            _channel = ready.Channel;
            _installed = ready.Installed;
            if (!_games.State.AnyRunning && await RestartStrictLauncherAsync(ct)) return false;
            _verifiedGenesisSequence = ready.Required.BaseClient?.Sequence;
            if (!launching) await LoadNewsAsync();
            if (_games.State.AnyRunning) PostRunningState("必要客戶端已通過驗證；遊戲執行期間不會替換檔案。");
            else PostState("ready", "客戶端已與正式機必要套件同步，完整 SHA-256 驗證通過。");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            PostState("update", "檢查或同步已暫停；完成正式機契約與檔案驗證前不會啟動遊戲。");
            return false;
        }
        catch (Exception e) when (e is PatcherException or HttpRequestException or IOException or JsonException
            or UnauthorizedAccessException or ArgumentException or OperationCanceledException)
        {
            _installed = null;
            Log("Strict client release validation failed", e);
            PostState("error", e is PatcherException ? e.Message : "無法核對正式機必要客戶端，已停止啟動。" + e.Message);
            return false;
        }
        finally { _work?.Dispose(); _work = null; }
    }

    IProgress<UpdateProgress> StrictProgress()
    {
        var meter = new SpeedMeter();
        return new ThrottledProgress(progress =>
        {
            if (_life.IsCancellationRequested) return;
            var (rate, eta) = progress.Stage == UpdateStage.Downloading
                ? meter.Sample(progress.BytesDone, progress.BytesTotal) : (0, 0);
            _post(new
            {
                type = "progress", stage = progress.Stage.ToString().ToLowerInvariant(),
                done = progress.BytesDone, total = progress.BytesTotal, file = progress.File, rate, eta,
                filesDone = progress.FilesDone, filesTotal = progress.FilesTotal,
            });
        });
    }

    bool CanChangeStrictInstallation(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        if (dispatcher.HasShutdownStarted) return false;
        return dispatcher.Invoke(() => !ct.IsCancellationRequested
            && RefreshGameProcesses() && _games.State.CanChangeInstallation);
    }

    async Task<bool> RestartStrictLauncherAsync(CancellationToken ct)
    {
        if (_channel?.Launcher is not { } release || !SelfUpdate.IsNewer(release.Version, _version)) return false;
        PostState("checking", $"正在將啟動器更新至 {release.Version}…");
        if (!await SelfUpdate.ApplyAsync(_repo, release, _version, _exePath, ct)) return false;
        using (SelfUpdate.Restart(_exePath, Environment.ProcessId)) { }
        System.Windows.Application.Current.Dispatcher.Invoke(() => System.Windows.Application.Current.Shutdown());
        return true;
    }
}
