using System.Security.Cryptography;
using Ro3Emu.ClientTools;

namespace Ro3Emu.Setup.Tests;

/// <summary>只以自造bytes核來源身分與混版拒絕，不需要合法遊戲DLL。</summary>
public sealed class VerifiedBaselineSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ro3-baseline-source-" + Guid.NewGuid().ToString("N"));
    private const string Managed = "ro3_Data/Managed/HUD.dll";
    private string FileAt(string relative) => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    public VerifiedBaselineSourceTests()
    {
        Write("ro3.exe", "synthetic-executable");
        Write(Managed, "synthetic-managed");
        VerifiedBaseline.Write(_root, "synthetic-release", 17, "synthetic-build", new string('a', 64),
            new[] { "ro3.exe", Managed }.Select(p => new VerifiedBaseline.Entry(new FileInfo(FileAt(p)).Length, Hash(FileAt(p)), p)));
    }

    private void Write(string relative, string value)
    {
        string path = FileAt(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidDataException("fixture上層目錄不存在"));
        File.WriteAllText(path, value);
    }
    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public void IdentityIsPortableAndBindsTheBaselineBytes()
    {
        var identity = VerifiedBaseline.ReadIdentity(_root);
        Assert.Equal(17, identity.Sequence);
        Assert.Equal("synthetic-release", identity.ReleaseId);
        Assert.Equal("synthetic-build", identity.BuildId);
        Assert.Equal(Hash(FileAt(VerifiedBaseline.RelativePath)), identity.BaselineSha256);
        Assert.Equal(2, VerifiedBaseline.ValidateSources(_root).Count);
    }

    [Fact]
    public void SameLengthDifferentContentIsRefused()
    {
        byte[] bytes = File.ReadAllBytes(FileAt("ro3.exe"));
        bytes[0] ^= 1;
        File.WriteAllBytes(FileAt("ro3.exe"), bytes);
        Assert.Throws<InvalidDataException>(() => VerifiedBaseline.ValidateSources(_root));
    }

    [Fact]
    public void PatchedManagedRequiresAMatchingPristineBackup()
    {
        string original = File.ReadAllText(FileAt(Managed));
        Write("ro3emu-backup/HUD.dll", original);
        Write(Managed, "a locally patched DLL");
        Assert.Equal(2, VerifiedBaseline.ValidateSources(_root).Count);
        Write("ro3emu-backup/HUD.dll", "wrong original");
        Assert.Throws<InvalidDataException>(() => VerifiedBaseline.ValidateSources(_root));
    }

    [Fact]
    public void UnlistedManagedFileIsRefusedInsteadOfSilentlyPackaged()
    {
        Write("ro3_Data/Managed/foreign.dll", "unexpected");
        Assert.Throws<InvalidDataException>(() => VerifiedBaseline.ValidateSources(_root));
    }

    [Fact]
    public void MalformedReleaseIdentityIsRefused()
    {
        string path = FileAt(VerifiedBaseline.RelativePath);
        File.WriteAllText(path, File.ReadAllText(path).Replace("release\t17", "release\t0", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => VerifiedBaseline.ReadIdentity(_root));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
