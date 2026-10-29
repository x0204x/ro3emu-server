using Mono.Cecil;
using Mono.Cecil.Cil;
using Ro3Emu.ClientTools;

namespace Ro3Emu.Setup.Tests;

/// <summary>
/// The literals <see cref="ClientPatcher"/> rewrites, on synthetic assemblies: each type named after the client's, each
/// method returning one literal. The real client's literals are checked by <see cref="TheRealClientHasEveryLiteralInItsPlace"/>.
/// </summary>
public sealed class ClientPatcherTests : IDisposable
{
    const string Server = "http://10.1.2.3:5380";
    const string Pem = "-----BEGIN RSA PUBLIC KEY-----\nMIIB\n-----END RSA PUBLIC KEY-----";
    const string Genesis = "https://server.genesis3.is";
    const string GmtManager = "GMT.GMTManager";
    const string GiftCardPath = "/gift-card/use/v2";

    readonly string _client = Path.Combine(Path.GetTempPath(), "ro3emu-patchrules-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_client, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>An assembly with one static method per (type, method, literal); a type "A.B/C" is C nested in A.B.</summary>
    static AssemblyDefinition Assembly(string name, params (string Type, string Method, string Literal)[] literals)
    {
        var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name, new Version(1, 0)), name, ModuleKind.Dll);
        var module = assembly.MainModule;
        foreach (var (typeName, methodName, literal) in literals)
        {
            var method = new MethodDefinition(methodName, MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.String);
            var il = method.Body.GetILProcessor();
            il.Emit(OpCodes.Ldstr, literal);
            il.Emit(OpCodes.Ret);
            Type(module, typeName).Methods.Add(method);
        }
        return assembly;
    }

    static TypeDefinition Type(ModuleDefinition module, string fullName)
    {
        var parts = fullName.Split('/');
        var top = module.GetType(parts[0]);
        if (top == null)
        {
            int dot = parts[0].LastIndexOf('.');
            top = new TypeDefinition(dot < 0 ? "" : parts[0][..dot], parts[0][(dot + 1)..], TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
            module.Types.Add(top);
        }
        if (parts.Length == 1) return top;
        var nested = top.NestedTypes.FirstOrDefault(t => t.Name == parts[1]);
        if (nested == null)
        {
            nested = new TypeDefinition("", parts[1], TypeAttributes.NestedPrivate | TypeAttributes.Class, module.TypeSystem.Object);
            top.NestedTypes.Add(nested);
        }
        return nested;
    }

    static string Literal(AssemblyDefinition assembly, string type, string method) =>
        (string)assembly.MainModule.GetType(type).Methods.Single(m => m.Name == method).Body.Instructions[0].Operand;

    static List<ClientPatcher.Rule> Rewrite(AssemblyDefinition assembly)
    {
        var rules = ClientPatcher.Rules(Server, Pem);
        ClientPatcher.RewriteLiterals(assembly.MainModule, rules);
        return rules;
    }

    [Theory]
    [InlineData(Genesis)]
    [InlineData("https://server.playragnarok3.com")] // the publisher's host before 16 Oct 2026
    [InlineData("http://52.8.99.195")]
    public void TheGmtOriginInGmtManagerBecomesTheServerWhateverItsHost(string origin)
    {
        using var assembly = Assembly("Assembly-CSharp",
            (GmtManager, "Obf_wCA", origin), // GMTUrl(): RegionalBootstrap.RewriteUrl(origin)
            (GmtManager + "/<>c__DisplayClass13_0", "Get", origin)); // a closure of the notice GET
        var rules = Rewrite(assembly);

        Assert.Equal(Server, Literal(assembly, GmtManager, "Obf_wCA"));
        Assert.Equal(Server, Literal(assembly, GmtManager + "/<>c__DisplayClass13_0", "Get"));
        Assert.Equal(2, rules.Single(r => r.Description.StartsWith("GMT origin", StringComparison.Ordinal)).Hits);
    }

    [Fact]
    public void TheSameOriginOutsideGmtManagerOrWithAPathIsKept()
    {
        using var assembly = Assembly("Happyengine.Network",
            ("RO3.SealedLoginFallback.RegionalBootstrap", "Primary", Genesis), // the publisher's origin routing keeps its own
            ("GMT.GMTManagerHelper", "Origin", Genesis),
            (GmtManager, "Notice", Genesis + "/notice/list"),
            (GmtManager, "Host", "server.genesis3.is"));
        Rewrite(assembly);

        Assert.Equal(Genesis, Literal(assembly, "RO3.SealedLoginFallback.RegionalBootstrap", "Primary"));
        Assert.Equal(Genesis, Literal(assembly, "GMT.GMTManagerHelper", "Origin"));
        Assert.Equal(Genesis + "/notice/list", Literal(assembly, GmtManager, "Notice"));
        Assert.Equal("server.genesis3.is", Literal(assembly, GmtManager, "Host"));
    }

    [Theory]
    [InlineData(Genesis + GiftCardPath)]
    [InlineData("https://server.playragnarok3.com" + GiftCardPath)]
    public void TheGiftCardPostGuardAllowsTheServersOwnUrl(string url)
    {
        using var assembly = Assembly("Happyengine.Network", ("Obf_dI", "RecoveryRedemption_IsAllowedPost", url));
        var rules = Rewrite(assembly);

        Assert.Equal(Server + GiftCardPath, Literal(assembly, "Obf_dI", "RecoveryRedemption_IsAllowedPost"));
        Assert.Equal(1, rules.Single(r => r.Description.Contains(GiftCardPath, StringComparison.Ordinal)).Hits);
    }

    [Fact]
    public void TheLoginAndServerListUrlsAreStillRewritten()
    {
        using var assembly = Assembly("Happyengine.Network",
            ("HttpNetworkManager", "Login", Genesis + "/sdk/login"),
            ("Obf_dI", "ServerList", Genesis + "/serverlist/v2/168/serverlist.json?inline=true&force_refresh=true"));
        Rewrite(assembly);

        Assert.Equal(Server + "/sdk/login", Literal(assembly, "HttpNetworkManager", "Login"));
        Assert.Equal(Server + "/serverlist/v2/168/serverlist.json?inline=true&force_refresh=true", Literal(assembly, "Obf_dI", "ServerList"));
    }

    /// <summary>A client folder whose DLLs are synthetic: <paramref name="dlls"/> by file name, the others empty.</summary>
    void WriteClient(Dictionary<string, AssemblyDefinition> dlls)
    {
        var managed = Path.Combine(_client, "ro3_Data", "Managed");
        Directory.CreateDirectory(managed);
        File.WriteAllBytes(Path.Combine(_client, "ro3.exe"), []);
        foreach (var file in ClientPatcher.PatchedFiles)
        {
            using var assembly = dlls.Remove(file, out var given) ? given : Assembly(Path.GetFileNameWithoutExtension(file));
            assembly.Write(Path.Combine(managed, file));
        }
    }

    List<string> Check() => ClientPatcher.Check(_client, Path.Combine(_client, "no-hooks.dll"), Path.Combine(_client, "no-loc.dll"));

    [Fact]
    public void CheckReportsAClientWithoutTheGmtOriginOrTheGiftCardUrl()
    {
        WriteClient(new()
        {
            ["Assembly-CSharp.dll"] = Assembly("Assembly-CSharp", ("Other.Config", "Origin", Genesis)), // not in GMTManager
        });
        var problems = Check();

        Assert.Contains(problems, p => p.StartsWith("literal not found: GMT origin", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("literal not found: ", StringComparison.Ordinal) && p.Contains(GiftCardPath, StringComparison.Ordinal));
    }

    [Fact]
    public void CheckRecognisesTheGmtOriginAndTheGiftCardUrl()
    {
        WriteClient(new()
        {
            ["Assembly-CSharp.dll"] = Assembly("Assembly-CSharp", (GmtManager, "Obf_wCA", Genesis)),
            ["Happyengine.Network.dll"] = Assembly("Happyengine.Network", ("Obf_dI", "RecoveryRedemption_IsAllowedPost", Genesis + GiftCardPath)),
        });
        var problems = Check();

        Assert.DoesNotContain(problems, p => p.StartsWith("literal not found: GMT origin", StringComparison.Ordinal));
        Assert.DoesNotContain(problems, p => p.Contains(GiftCardPath, StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("literal not found: SDK login URL", StringComparison.Ordinal)); // still checked
    }

    /// <summary>
    /// The installed client (RO3_CLIENT_DIR): the GMT origin is rewritten in GMTManager's getter and notice GET only, the
    /// gift card guard once, and the origins the publisher's session routing owns (RegionalBootstrap, SealedRuntimeSession)
    /// are kept: with the server's origin outside them, its RewriteUrl and NormalizeForGuard return the URL unchanged.
    /// </summary>
    [ClientDllFact]
    public void TheRealClientHasEveryLiteralInItsPlace()
    {
        using var game = ClientDll.Read();
        using var network = AssemblyDefinition.ReadAssembly(Path.Combine(Path.GetDirectoryName(ClientDll.Path)!, "Happyengine.Network.dll"), new ReaderParameters { InMemory = true });
        var rules = ClientPatcher.Rules(Server, Pem);
        var changed = ClientPatcher.RewriteLiterals(game.MainModule, rules).Concat(ClientPatcher.RewriteLiterals(network.MainModule, rules)).ToList();

        Assert.Equal(2, rules.Single(r => r.Description.StartsWith("GMT origin", StringComparison.Ordinal)).Hits);
        Assert.Equal(1, rules.Single(r => r.Description.Contains(GiftCardPath, StringComparison.Ordinal)).Hits);
        Assert.All(rules.Where(r => r.Description.Contains("URL", StringComparison.Ordinal)), r => Assert.True(r.Hits > 0, r.Description));
        Assert.DoesNotContain(changed, w => w.Contains("RO3.SealedLoginFallback", StringComparison.Ordinal));
    }
}
