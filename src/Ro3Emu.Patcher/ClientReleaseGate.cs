using Ro3Emu.ClientRelease;

namespace Ro3Emu.Patcher;

/// <summary>正式機、簽署通道與玩家清單必須指向同一官方基底及修補套件。</summary>
public static class ClientReleaseGate
{
    public static void Validate(ClientReleaseRequirement required, ChannelInfo channel, Manifest manifest)
    {
        try { required.Validate(); }
        catch (InvalidDataException e) { throw new PatcherException(e.Message, e); }
        // 必要序號是最低已核對 epoch；公告／登入器更新可提高通道序號，但不能變更必要遊戲套件。
        if (required.Channel != channel.Channel || channel.Sequence < required.Sequence
            || required.Version != channel.Version || required.ManifestSha256 != channel.ManifestSha256
            || required.BaseClient != channel.BaseClient || required.ServerVersion != channel.ServerVersion
            || required.PatchSha256 != channel.PatchSha256 || channel.Version != manifest.Version
            || required.BaseClient != manifest.BaseClient || required.ServerVersion != manifest.ServerVersion
            || required.PatchSha256 != manifest.PatchSha256)
            throw new PatcherException("相容更新站尚未與正式機同步；已停止更新與啟動，請稍後重新檢查。");
        if (manifest.Files.Count == 0 || manifest.Files.Any(file => file.Size < 0 || !Manifest.IsSha256Hex(file.Sha256))
            || manifest.Files.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Count
            || !manifest.Files.Any(file => file.Path == manifest.Launch.Exe))
            throw new PatcherException("客戶端清單不完整，無法驗證必要套件。");
    }

    public static bool MatchesInstalled(LocalState? state, ChannelInfo channel, Manifest manifest) => state is not null
        && state.Channel == channel.Channel && state.ManifestSha256 == channel.ManifestSha256
        && state.Version == manifest.Version && state.BaseClient == manifest.BaseClient
        && state.ServerVersion == manifest.ServerVersion && state.PatchSha256 == manifest.PatchSha256
        && state.Launch is not null && state.Launch.Exe == manifest.Launch.Exe && state.Launch.Args == manifest.Launch.Args
        && state.Files.Count == manifest.Files.Count && manifest.Files.All(file =>
            state.Files.TryGetValue(file.Path, out var entry) && entry.Size == file.Size && entry.Sha256 == file.Sha256);
}

public sealed record ClientReleaseReady(ClientReleaseRequirement Required, ChannelInfo Channel, Manifest Manifest,
    LocalState Installed, bool Updated);

/// <summary>單次同步不遞迴重試；只有已核對正式契約的清單可以寫入安裝目錄。</summary>
public sealed class ClientReleaseSynchronizer(Updater updater, RepositoryClient repository,
    Func<CancellationToken, Task<ClientReleaseRequirement>> requiredClient, Action<ChannelInfo>? acceptSequence = null)
{
    public async Task<ClientReleaseReady> EnsureReadyAsync(string channelName, bool gamesRunning,
        Func<bool> canChangeInstallation, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        if (!repository.RequiresSignedChannel) throw new PatcherException("正式模式必須使用已簽署的客戶端更新通道。");
        var (required, channel, manifest) = await ReadTargetAsync(channelName, ct);
        acceptSequence?.Invoke(channel);
        LocalState? state = updater.ReadStateOnly();
        bool stateMatches = !updater.HasPendingCommit && ClientReleaseGate.MatchesInstalled(state, channel, manifest);
        if (gamesRunning && !stateMatches) throw CloseGames();
        // 缺安裝紀錄時直接由 verifyAll 更新核對每一檔，避免同一輪先掃、更新再掃三遍。
        bool filesVerified = stateMatches && (await updater.VerifyInstalledAsync(manifest, progress, ct)).Matches;
        bool update = !stateMatches || !filesVerified;
        if (update)
        {
            ct.ThrowIfCancellationRequested();
            if (gamesRunning || !canChangeInstallation()) throw CloseGames();
            Func<bool>? previousGuard = updater.CanChangeInstallation;
            updater.CanChangeInstallation = canChangeInstallation;
            try { await updater.UpdateAsync(channel, verifyAll: true, progress, ct); }
            finally { updater.CanChangeInstallation = previousGuard; }
            state = updater.ReadStateOnly();
            // UpdateAsync(verifyAll:true) 已核現有檔的實體 SHA、每個重用／下載 chunk 及重組整檔。
            // 同一次呼叫重用此證據；下一次 Check／Play 仍必須重新讀檔。
            filesVerified = true;
        }
        if (!filesVerified || !ClientReleaseGate.MatchesInstalled(state, channel, manifest))
            throw new PatcherException("客戶端仍有檔案未通過 SHA-256 驗證，已停止啟動。");
        // 大型下載或掃描期間正式機可能換版；不使用掃描開始時的舊契約直接開啟遊戲。
        var current = await ReadTargetAsync(channelName, ct);
        acceptSequence?.Invoke(current.Channel);
        if (current.Required with { Sequence = required.Sequence } != required)
            throw new PatcherException("正式機必要客戶端已切換；請重新檢查後再啟動。");
        ct.ThrowIfCancellationRequested();
        return new(current.Required, current.Channel, current.Manifest,
            state ?? throw new PatcherException("安裝紀錄未完成。"), update);
    }

    async Task<(ClientReleaseRequirement Required, ChannelInfo Channel, Manifest Manifest)> ReadTargetAsync(
        string channelName, CancellationToken ct)
    {
        ClientReleaseRequirement required = await requiredClient(ct);
        ChannelInfo channel = await repository.GetChannelAsync(channelName, ct);
        var (manifest, _) = await repository.GetManifestAsync(channel, ct);
        ClientReleaseGate.Validate(required, channel, manifest);
        return (required, channel, manifest);
    }

    static PatcherException CloseGames() => new("正式機所需客戶端與目前安裝不同；請先關閉所有遊戲，再重新檢查與同步。");
}
