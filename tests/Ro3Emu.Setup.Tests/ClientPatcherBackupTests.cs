using Mono.Cecil;
using Mono.Cecil.Cil;
using Ro3Emu.ClientTools;

namespace Ro3Emu.Setup.Tests;

/// <summary>
/// The patcher's backup / stage / journal / replace steps (<see cref="ClientPatcher.ReplaceManaged"/>) on a synthetic
/// client in a temp folder: tiny assemblies under the patched DLL names, each returning one URL literal.
/// </summary>
public sealed class ClientPatcherBackupTests : IDisposable
{
    readonly string _client = Path.Combine(Path.GetTempPath(), "ro3emu-patchbackup-" + Guid.NewGuid().ToString("N"));
    string Managed => Path.Combine(_client, "ro3_Data", "Managed");
    string Backup => Path.Combine(_client, "ro3emu-backup");
    readonly Dictionary<string, byte[]> _originals = new();

    public ClientPatcherBackupTests()
    {
        Directory.CreateDirectory(Managed);
        File.WriteAllBytes(Path.Combine(_client, "ro3.exe"), []);
        foreach (var file in ClientPatcher.PatchedFiles)
        {
            _originals[file] = Original(file, "http://publisher.example/sdk/login");
            File.WriteAllBytes(Path.Combine(Managed, file), _originals[file]);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_client, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>An assembly named after <paramref name="file"/> with Game.Config.Url() returning <paramref name="url"/>.</summary>
    static byte[] Original(string file, string url)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name, new Version(1, 0)), name, ModuleKind.Dll);
        var module = assembly.MainModule;
        var type = new TypeDefinition("Game", "Config", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, module.TypeSystem.Object);
        var method = new MethodDefinition("Url", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.String);
        var il = method.Body.GetILProcessor();
        il.Emit(OpCodes.Ldstr, url);
        il.Emit(OpCodes.Ret);
        type.Methods.Add(method);
        module.Types.Add(type);
        var stream = new MemoryStream();
        assembly.Write(stream);
        return stream.ToArray();
    }

    static string Url(string path)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { InMemory = true });
        return (string)assembly.MainModule.GetType("Game.Config").Methods.Single().Body.Instructions[0].Operand;
    }

    /// <summary>What the real patch does to a DLL, in miniature: rewrite the literal, mark, write the staged file.</summary>
    static Action<string, string, string> Rewrite(string url) => (_, source, destination) =>
    {
        using var assembly = AssemblyDefinition.ReadAssembly(source, new ReaderParameters { InMemory = true });
        assembly.MainModule.GetType("Game.Config").Methods.Single().Body.Instructions[0].Operand = url;
        ClientPatcher.Mark(assembly.MainModule);
        assembly.Write(destination);
    };

    void AssertBackupsAreTheOriginals()
    {
        foreach (var file in ClientPatcher.PatchedFiles)
            Assert.Equal(_originals[file], File.ReadAllBytes(Path.Combine(Backup, file)));
    }

    void AssertNoStagedFiles() =>
        Assert.Empty(Directory.GetFiles(Managed, "*" + ClientPatcher.StagedSuffix));

    [Theory]
    [InlineData(true)]
    [InlineData(false)] // the journal is lost: the marker type alone must still recognise our DLL
    public void ARepatchInterruptedAfterTheFirstReplacementStillRestoresTheOriginals(bool keepJournal)
    {
        ClientPatcher.ReplaceManaged(_client, Rewrite("http://one.example/sdk/login"));
        Assert.All(ClientPatcher.PatchedFiles, f => Assert.Equal("http://one.example/sdk/login", Url(Path.Combine(Managed, f))));

        // A second patch to another server stops right after the first DLL was replaced.
        string first = ClientPatcher.PatchedFiles[0];
        Assert.Throws<IOException>(() => ClientPatcher.ReplaceManaged(_client, Rewrite("http://two.example/sdk/login"),
            step => { if (step == "replaced:" + first) throw new IOException("power cut"); }));
        Assert.Equal("http://two.example/sdk/login", Url(Path.Combine(Managed, first)));
        Assert.Equal("http://one.example/sdk/login", Url(Path.Combine(Managed, ClientPatcher.PatchedFiles[1])));
        string journal = Path.Combine(Backup, "patching.journal");
        Assert.True(File.Exists(journal));
        if (!keepJournal) File.Delete(journal);

        // The next run sees a DLL matching neither the record nor the backup: ours, not a game update.
        ClientPatcher.ReplaceManaged(_client, Rewrite("http://three.example/sdk/login"));
        Assert.All(ClientPatcher.PatchedFiles, f => Assert.Equal("http://three.example/sdk/login", Url(Path.Combine(Managed, f))));
        Assert.False(File.Exists(journal));
        AssertBackupsAreTheOriginals();
        AssertNoStagedFiles();

        ClientPatcher.Restore(_client);
        foreach (var file in ClientPatcher.PatchedFiles)
            Assert.Equal(_originals[file], File.ReadAllBytes(Path.Combine(Managed, file)));
    }

    [Fact]
    public void AGameUpdateStillBecomesTheNewOriginal()
    {
        ClientPatcher.ReplaceManaged(_client, Rewrite("http://one.example/sdk/login"));
        string updated = ClientPatcher.PatchedFiles[1];
        var update = Original(updated, "http://publisher.example/v2/sdk/login");
        File.WriteAllBytes(Path.Combine(Managed, updated), update); // the publisher's launcher replaced it

        ClientPatcher.ReplaceManaged(_client, Rewrite("http://two.example/sdk/login"));
        Assert.Equal(update, File.ReadAllBytes(Path.Combine(Backup, updated)));
        ClientPatcher.Restore(_client);
        Assert.Equal(update, File.ReadAllBytes(Path.Combine(Managed, updated)));
        Assert.Equal(_originals[ClientPatcher.PatchedFiles[0]], File.ReadAllBytes(Path.Combine(Managed, ClientPatcher.PatchedFiles[0])));
    }

    [Fact]
    public void AFailureWhileStagingLeavesTheClientUntouched()
    {
        ClientPatcher.ReplaceManaged(_client, Rewrite("http://one.example/sdk/login"));
        var before = ClientPatcher.PatchedFiles.ToDictionary(f => f, f => File.ReadAllBytes(Path.Combine(Managed, f)));
        string broken = ClientPatcher.PatchedFiles[3];
        var rewrite = Rewrite("http://two.example/sdk/login");

        // One staged DLL does not load: nothing in Managed may change.
        Assert.Throws<InvalidOperationException>(() => ClientPatcher.ReplaceManaged(_client, (file, source, destination) =>
        {
            if (file == broken) File.WriteAllBytes(destination, [0x4D, 0x5A, 1, 2, 3]);
            else rewrite(file, source, destination);
        }));
        foreach (var file in ClientPatcher.PatchedFiles) Assert.Equal(before[file], File.ReadAllBytes(Path.Combine(Managed, file)));
        Assert.False(File.Exists(Path.Combine(Backup, "patching.journal")));
        AssertNoStagedFiles();
        AssertBackupsAreTheOriginals();
    }

    [Fact]
    public void OurDllIsNeverTakenAsTheOriginalWhenTheBackupIsGone()
    {
        ClientPatcher.ReplaceManaged(_client, Rewrite("http://one.example/sdk/login"));
        Directory.Delete(Backup, true);
        var refused = Assert.Throws<InvalidOperationException>(() => ClientPatcher.ReplaceManaged(_client, Rewrite("http://two.example/sdk/login")));
        Assert.Contains("no original", refused.Message);
        Assert.Empty(Directory.GetFiles(Backup, "*.dll"));
    }
}
