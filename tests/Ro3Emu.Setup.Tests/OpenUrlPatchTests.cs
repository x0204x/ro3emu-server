using Mono.Cecil;
using Mono.Cecil.Cil;
using Ro3Emu.ClientTools;

namespace Ro3Emu.Setup.Tests;

/// <summary>
/// The purchase link redirect (<see cref="OpenUrlPatch"/>) on synthetic assemblies: a UnityEngine.CoreModule whose
/// Application.OpenURL(string) is the engine's InternalCall extern, and a hooks DLL with the UrlRewrite slot.
/// </summary>
public sealed class OpenUrlPatchTests : IDisposable
{
    const string Donations = "https://shop.example/donate";
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3emu-openurl-" + Guid.NewGuid().ToString("N"));

    public OpenUrlPatchTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>UnityEngine.CoreModule with Application.OpenURL(string) as an InternalCall extern, like the client's.</summary>
    static AssemblyDefinition CoreModule(bool withBody = false)
    {
        var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("UnityEngine.CoreModule", new Version(0, 0)), "UnityEngine.CoreModule", ModuleKind.Dll);
        var module = assembly.MainModule;
        var application = new TypeDefinition("UnityEngine", "Application", TypeAttributes.Public | TypeAttributes.Sealed, module.TypeSystem.Object);
        var openUrl = new MethodDefinition(OpenUrlPatch.OpenUrlMethod, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.Void);
        openUrl.Parameters.Add(new ParameterDefinition("url", ParameterAttributes.None, module.TypeSystem.String));
        if (withBody) openUrl.Body.GetILProcessor().Emit(OpCodes.Ret);
        else openUrl.ImplAttributes = MethodImplAttributes.InternalCall;
        application.Methods.Add(openUrl);
        module.Types.Add(application);
        return assembly;
    }

    static MethodDefinition OpenUrl(AssemblyDefinition assembly) =>
        assembly.MainModule.GetType(OpenUrlPatch.ApplicationType).Methods.Single(m => m.Name == OpenUrlPatch.OpenUrlMethod);

    static AssemblyDefinition RoundTrip(AssemblyDefinition assembly)
    {
        var stream = new MemoryStream();
        assembly.Write(stream);
        stream.Position = 0;
        return AssemblyDefinition.ReadAssembly(stream);
    }

    /// <summary>A Ro3Emu.ClientHooks.dll with UrlRewrite.Open(string) and the Target() slot holding <paramref name="target"/>.</summary>
    string HooksDll(string target = OpenUrlPatch.PurchaseUrlPlaceholder)
    {
        using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Ro3Emu.ClientHooks", new Version(0, 0)), "Ro3Emu.ClientHooks", ModuleKind.Dll);
        var module = assembly.MainModule;
        var type = new TypeDefinition("Ro3Emu.ClientHooks", "UrlRewrite", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, module.TypeSystem.Object);
        var open = new MethodDefinition("Open", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
        open.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        open.Body.GetILProcessor().Emit(OpCodes.Ret);
        var slot = new MethodDefinition("Target", MethodAttributes.Assembly | MethodAttributes.Static, module.TypeSystem.String);
        var il = slot.Body.GetILProcessor();
        il.Emit(OpCodes.Ldstr, target);
        il.Emit(OpCodes.Ret);
        type.Methods.Add(open);
        type.Methods.Add(slot);
        module.Types.Add(type);
        var path = Path.Combine(_dir, "Ro3Emu.ClientHooks.dll");
        assembly.Write(path);
        return path;
    }

    static string Target(string hooksDll)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(hooksDll, new ReaderParameters { InMemory = true });
        return (string)assembly.MainModule.GetType("Ro3Emu.ClientHooks.UrlRewrite").Methods.Single(m => m.Name == "Target").Body.Instructions[0].Operand;
    }

    [Fact]
    public void OpenUrlGetsABodyThatCallsTheHook()
    {
        using var core = CoreModule();
        Assert.Equal("Application.OpenURL -> UrlRewrite.Open", OpenUrlPatch.Patch(core.MainModule, HooksDll()));

        using var written = RoundTrip(core);
        var openUrl = OpenUrl(written);
        Assert.False(openUrl.IsInternalCall);
        var calls = openUrl.Body.Instructions.Where(i => i.OpCode == OpCodes.Call).Select(i => (MethodReference)i.Operand).ToList();
        Assert.Equal("System.Void Ro3Emu.ClientHooks.UrlRewrite::Open(System.String)", Assert.Single(calls).FullName);
    }

    [Fact]
    public void PatchingTwiceChangesNothing()
    {
        using var core = CoreModule();
        OpenUrlPatch.Patch(core.MainModule);
        using var once = RoundTrip(core);
        OpenUrlPatch.Patch(once.MainModule);
        using var twice = RoundTrip(once);
        Assert.Equal(3, OpenUrl(twice).Body.Instructions.Count);
        Assert.Single(twice.MainModule.AssemblyReferences, r => r.Name == "Ro3Emu.ClientHooks");
    }

    [Fact]
    public void AnUnknownClientBuildIsRefused()
    {
        using var core = CoreModule(withBody: true);
        var refused = Assert.Throws<InvalidOperationException>(() => OpenUrlPatch.Patch(core.MainModule));
        Assert.Contains("unknown client build", refused.Message);
    }

    [Fact]
    public void AHooksBuildWithoutTheRedirectIsRefused()
    {
        using var core = CoreModule();
        using var other = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Ro3Emu.ClientHooks", new Version(0, 0)), "Ro3Emu.ClientHooks", ModuleKind.Dll);
        var old = Path.Combine(_dir, "old-hooks.dll");
        other.Write(old);
        Assert.Throws<InvalidOperationException>(() => OpenUrlPatch.Patch(core.MainModule, old));
    }

    [Fact]
    public void ThePurchaseUrlIsWrittenIntoTheInstalledHooks()
    {
        var hooks = HooksDll();
        OpenUrlPatch.SetPurchaseUrl(hooks, Donations);
        Assert.Equal(Donations, Target(hooks));

        // Joining again with another URL rewrites the copy the patcher installs, never a URL a previous run wrote.
        var again = HooksDll(Donations);
        Assert.Throws<InvalidOperationException>(() => OpenUrlPatch.SetPurchaseUrl(again, "https://other.example/"));
    }

    [Fact]
    public void OpenUrlIsRedirectedWithOrWithoutAPurchaseUrl()
    {
        // Without an address the hooks open nothing for a publisher store link (issue #147), so the redirect is always made.
        Assert.Contains("UnityEngine.CoreModule.dll", ClientPatcher.PatchedFiles);
        var extra = ClientPatcher.Extra("UnityEngine.CoreModule.dll", HooksDll(), "Ro3Emu.Loc.dll");
        Assert.NotNull(extra);
        using var core = CoreModule();
        Assert.Equal("Application.OpenURL -> UrlRewrite.Open", extra(core.MainModule));
    }

    [Theory]
    [InlineData("https://shop.example/donate", true)]
    [InlineData("http://203.0.113.10:8080/shop?x=1", true)]
    [InlineData("ftp://shop.example/", false)]
    [InlineData("shop.example/donate", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/Windows/notepad.exe", false)]
    [InlineData("", false)]
    public void OnlyAnAbsoluteWebAddressIsAPurchaseUrl(string url, bool accepted) => Assert.Equal(accepted, OpenUrlPatch.IsPurchaseUrl(url));

    [Fact]
    public void TheJoinOptionTakesTheUrlAndRefusesAnythingElse()
    {
        Assert.Equal(Donations, Options.Parse(["join", "1.2.3.4", "--purchase-url", Donations]).PurchaseUrl);
        Assert.Null(Options.Parse(["join", "1.2.3.4"]).PurchaseUrl);
        Assert.Throws<SetupException>(() => Options.Parse(["join", "1.2.3.4", "--purchase-url", "file:///C:/x.exe"]));
        Assert.Throws<SetupException>(() => Options.Parse(["join", "1.2.3.4", "--purchase-url"]));
        Assert.Equal(Donations, new OptionsWithClient(Options.Parse(["install", "--purchase-url", Donations]), @"C:\RO3\ROO_PC").Value.PurchaseUrl);
    }
}
