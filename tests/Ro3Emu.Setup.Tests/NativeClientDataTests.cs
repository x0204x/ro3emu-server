using System.Text.Json;
using Ro3Emu.ClientRelease;
using Ro3Emu.DataExport;
using Ro3Emu.Setup;

namespace Ro3Emu.Setup.Tests;

public sealed class NativeClientDataTests : IDisposable
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ro3-native-source-" + Guid.NewGuid().ToString("N"));
    private readonly GenesisIdentity _identity = new(140083, "release", "build", Hash, Hash);
    private GameDataExport.Stamp _stamp = new(GameDataExport.FormatVersion, "full-fingerprint", "synthetic", DateTime.UtcNow, 0, 0, 1, FailedTables: []);
    private readonly List<bool> _forced = [];
    public NativeClientDataTests()
    {
        foreach (string folder in new[] { "config", "sceneeditor", "skills", "nav" })
        {
            Directory.CreateDirectory(Path.Combine(_root, folder));
            File.WriteAllText(Path.Combine(_root, folder, "synthetic.json"), "{}");
        }
        File.WriteAllText(Path.Combine(_root, "config", "MapResource.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "msgtable.tsv"), "synthetic");
        File.WriteAllText(Path.Combine(_root, "loc_en.tsv"), "synthetic");
        File.WriteAllText(Path.Combine(_root, "config", "test.json"), "{}");
        WriteExport();
    }
    private void WriteExport() => File.WriteAllText(Path.Combine(_root, "export.json"), JsonSerializer.Serialize(_stamp));
    private void Ensure() => NativeClientData.Ensure(_root, _identity, false, f => { _forced.Add(f); WriteExport(); }, () => _stamp, () => "full-fingerprint");
    [Fact]
    public void NoPriorSourceContractForcesFullFirstExportThenWritesFileHashes()
    { Ensure(); Assert.Equal([true], _forced); var receipt = JsonSerializer.Deserialize<NativeClientData.Receipt>(File.ReadAllBytes(Path.Combine(_root, "client-source.json"))); Assert.NotEmpty(receipt!.Files!); }
    [Fact]
    public void VerifiedSourceContractAllowsUnforcedReuse()
    { Ensure(); _forced.Clear(); Ensure(); Assert.Equal([false], _forced); }
    [Fact]
    public void SameLengthDataTamperingForcesReexport()
    { Ensure(); _forced.Clear(); File.WriteAllText(Path.Combine(_root, "config", "test.json"), "[]"); Ensure(); Assert.Equal([true], _forced); }
    [Fact]
    public void PartialExportNeverReceivesAnAcceptedSourceContract()
    { _stamp = _stamp with { FailedTables = ["missing"] }; Assert.Throws<InvalidDataException>(Ensure); Assert.False(File.Exists(Path.Combine(_root, "client-source.json"))); }
    [Fact]
    public void WrongFullFingerprintRefusesEvenIfQuickMetadataCouldMatch()
    { _stamp = _stamp with { Fingerprint = "other" }; Assert.Throws<InvalidDataException>(Ensure); Assert.False(File.Exists(Path.Combine(_root, "client-source.json"))); }
    public void Dispose() => Directory.Delete(_root, true);
}
