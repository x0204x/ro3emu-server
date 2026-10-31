using System.Text.Json.Serialization;

namespace Ro3Emu.ClientRelease;

/// <summary>同一份已驗 Genesis 來源；官方序號與玩家更新通道序號分開保存。</summary>
public sealed record GenesisIdentity(
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("releaseId")] string ReleaseId,
    [property: JsonPropertyName("buildId")] string BuildId,
    [property: JsonPropertyName("contentSha256")] string ContentSha256,
    [property: JsonPropertyName("baselineSha256")] string BaselineSha256)
{
    public void Validate()
    {
        if (Sequence <= 0 || !ClientReleaseRequirement.IsText(ReleaseId)
            || !ClientReleaseRequirement.IsText(BuildId)
            || !ClientReleaseRequirement.IsHash(ContentSha256)
            || !ClientReleaseRequirement.IsHash(BaselineSha256))
            throw new InvalidDataException("Genesis 客戶端來源身分不完整或格式錯誤。");
    }
}

/// <summary>正式機宣告的必要玩家套件；內容必須與已簽署通道及 manifest 相同。</summary>
public sealed record ClientReleaseRequirement
{
    public const int CurrentFormat = 1;
    public const string FileName = "client-release.json";
    public const string Endpoint = "/ro3emu/client-release";

    [JsonPropertyName("format")] public int Format { get; init; } = CurrentFormat;
    [JsonPropertyName("serverVersion")] public string ServerVersion { get; init; } = "";
    [JsonPropertyName("baseClient")] public GenesisIdentity? BaseClient { get; init; }
    [JsonPropertyName("channel")] public string Channel { get; init; } = "";
    /// <summary>最低可接受的簽署通道序號；同一套件的公告或登入器更新可以繼續遞增。</summary>
    [JsonPropertyName("sequence")] public long Sequence { get; init; }
    [JsonPropertyName("version")] public string Version { get; init; } = "";
    [JsonPropertyName("manifestSha256")] public string ManifestSha256 { get; init; } = "";
    [JsonPropertyName("patchSha256")] public string PatchSha256 { get; init; } = "";

    public void Validate()
    {
        if (Format != CurrentFormat || !IsText(ServerVersion) || BaseClient is null
            || !IsText(Version) || Sequence <= 0 || !IsHash(ManifestSha256) || !IsHash(PatchSha256)
            || string.IsNullOrWhiteSpace(Channel)
            || Channel.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
            throw new InvalidDataException("必要客戶端套件宣告不完整或格式錯誤。");
        BaseClient.Validate();
    }

    public bool MatchesServerVersion(string actual)
    {
        if (ServerVersion.Contains('+')) return ServerVersion == actual;
        return string.Equals(ServerVersion, actual.Split('+')[0], StringComparison.Ordinal);
    }

    public static bool IsHash(string? value) => value is { Length: 64 }
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static bool IsText(string? value) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= 256 && !value.Any(char.IsControl);
}
