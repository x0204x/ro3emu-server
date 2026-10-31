using System.Security.Cryptography;
using System.Text.Json;
using Ro3Emu.ClientRelease;
using Ro3Emu.DataExport;

namespace Ro3Emu.Setup;

/// <summary>原生匯出的資料驗證；首次沒有完整來源／檔案契約時，不以檔案數與 mtime 背書。</summary>
internal static class NativeClientData
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    internal sealed record Receipt(int Format, GenesisIdentity BaseClient, string ExportSha256,
        SortedDictionary<string, DataFileRecord>? Files);

    internal static void Ensure(string data, GenesisIdentity identity, bool force, Action<bool> ensureData,
        Func<GameDataExport.Stamp?> readStamp, Func<string> fullFingerprint)
    {
        identity.Validate();
        string source = Path.Combine(data, "client-source.json");
        string export = Path.Combine(data, "export.json");
        string fingerprint = fullFingerprint();
        bool verified = false;
        if (!force && File.Exists(source))
        {
            try
            {
                var receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllBytes(source), Json)
                    ?? throw new InvalidDataException("原生資料來源契約不可為 null。");
                if (receipt.Format != 1 || receipt.BaseClient != identity || !ClientReleaseRequirement.IsHash(receipt.ExportSha256)
                    || Hash(export) != receipt.ExportSha256) throw new InvalidDataException("原生資料來源契約不符。");
                GameDataIntegrity.Validate(data, receipt.Files);
                ValidateStamp(readStamp(), fingerprint);
                verified = true;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or JsonException) { }
        }
        ensureData(force || !verified);
        // 匯出缺表只印 Warning 不足以開服；重新核完整 fingerprint 與 FailedTables。
        ValidateStamp(readStamp(), fullFingerprint());
        var files = GameDataIntegrity.Capture(data);
        ClientSync.WriteAtomic(source, new Receipt(1, identity, Hash(export), files));
    }

    private static void ValidateStamp(GameDataExport.Stamp? stamp, string fingerprint)
    {
        if (stamp is null || stamp.Format != GameDataExport.FormatVersion || stamp.FailedTables is not { Length: 0 }
            || stamp.Fingerprint != fingerprint)
            throw new InvalidDataException("原生資料匯出不完整或與完整客戶端 fingerprint 不符，拒絕發布及開服。");
    }
    private static string Hash(string path) { using var s = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(s)); }
}
