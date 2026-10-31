using System.Security.Cryptography;
using System.Text.Json;
using Ro3Emu.ClientRelease;
using Ro3Emu.Server;

namespace Ro3Emu.Tests;

public sealed class ClientReleaseGateTests : IDisposable
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ro3-contract-" + Guid.NewGuid().ToString("N"));
    private readonly GenesisIdentity _identity = new(12345, "synthetic-release", "synthetic-build", Hash, Hash);
    private ServerOptions Options => new() { DataDir = _root, ClientRelease = new() { Required = true, RequirementPath = Path.Combine(_root, "client-release.json") } };
    public ClientReleaseGateTests()
    {
        foreach (string folder in new[] { "config", "sceneeditor", "skills", "nav" })
        {
            Directory.CreateDirectory(Path.Combine(_root, folder));
            File.WriteAllText(Path.Combine(_root, folder, "synthetic.json"), "{}");
        }
        File.WriteAllText(Path.Combine(_root, "config", "MapResource.json"), "{}");
        foreach (string file in new[] { "msgtable.tsv", "loc_en.tsv", "export.json" })
            File.WriteAllText(Path.Combine(_root, file), "{}");
    }
    private void Write(string? server = null, GenesisIdentity? data = null)
    {
        var r = new ClientReleaseRequirement { ServerVersion = server ?? "1.0.24", BaseClient = _identity, Channel = "stable", Sequence = 20, Version = "test", ManifestSha256 = Hash, PatchSha256 = Hash };
        File.WriteAllText(Path.Combine(_root, "client-release.json"), JsonSerializer.Serialize(r));
        File.WriteAllText(Path.Combine(_root, "curated-import.json"), JsonSerializer.Serialize(new { Format = 1, Client = data ?? _identity, Files = GameDataIntegrity.Capture(_root) }));
    }
    [Fact] public void LegacyUnconfiguredHostsRemainOptional() => Assert.Null(ClientReleaseGate.Read(new ServerOptions()));
    [Fact] public void RequiredMissingContractRefusesBeforeHostBuild() => Assert.Throws<InvalidDataException>(() => ClientReleaseGate.Read(Options, "1.0.24"));
    [Fact]
    public void MatchingCuratedIdentityProvidesOnlyPublicRequirement()
    { Write(); var result = ClientReleaseGate.Read(Options, "1.0.24+test"); Assert.Equal(_identity, result!.BaseClient); Assert.DoesNotContain("private", JsonSerializer.Serialize(result)); }
    [Fact] public void WrongServerRefuses() { Write("1.0.23"); Assert.Throws<InvalidDataException>(() => ClientReleaseGate.Read(Options, "1.0.24")); }
    [Fact] public void WrongBaseRefuses() { Write(data: _identity with { Sequence = 12344 }); Assert.Throws<InvalidDataException>(() => ClientReleaseGate.Read(Options, "1.0.24")); }
    [Fact]
    public void InvalidFormatRefuses()
    { Write(); string p = Path.Combine(_root, "client-release.json"); File.WriteAllText(p, File.ReadAllText(p).Replace("\"format\":1", "\"format\":2")); Assert.Throws<InvalidDataException>(() => ClientReleaseGate.Read(Options, "1.0.24")); }
    private void WriteNative()
    {
        Write();
        File.Delete(Path.Combine(_root, "curated-import.json"));
        File.WriteAllText(Path.Combine(_root, "client-source.json"), JsonSerializer.Serialize(new
        {
            Format = 1, BaseClient = _identity,
            ExportSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(_root, "export.json")))),
            Files = GameDataIntegrity.Capture(_root),
        }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameSizeTimestampPreservingCorruptionRefuses(bool native)
    {
        if (native) WriteNative(); else Write();
        string path = Path.Combine(_root, "config", "synthetic.json");
        var modified = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, "[]");
        File.SetLastWriteTimeUtc(path, modified);
        Assert.Throws<InvalidDataException>(() => ClientReleaseGate.Read(Options, "1.0.24"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddedOrMissingCoreFileRefuses(bool delete)
    {
        Write();
        if (delete) File.Delete(Path.Combine(_root, "nav", "synthetic.json"));
        else File.WriteAllText(Path.Combine(_root, "nav", "extra.json"), "{}");
        Assert.Throws<InvalidDataException>(() => ClientReleaseGate.Read(Options, "1.0.24"));
    }

    [Fact]
    public void EmptyCuratedFileListCannotVouchForData()
    {
        Write();
        File.WriteAllText(Path.Combine(_root, "curated-import.json"), JsonSerializer.Serialize(new
        { Format = 1, Client = _identity, Files = new Dictionary<string, DataFileRecord>() }));
        Assert.Throws<InvalidDataException>(() => ClientReleaseGate.Read(Options, "1.0.24"));
    }

    [Fact]
    public void MatchingNativeDataProvidesRequirement()
    {
        WriteNative();
        Assert.Equal(_identity, ClientReleaseGate.Read(Options, "1.0.24")!.BaseClient);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
