using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Ro3Emu.ClientTools;

/// <summary>
/// Keeps the client's purchase links away from the publisher's store (issue #147: a player could pay the publisher and
/// get nothing on an RO3Emu server). They open the address the server operator chose (RO3Emu.exe --purchase-url), or
/// nothing when there is none. It gives
/// UnityEngine.Application.OpenURL(string) (in UnityEngine.CoreModule) a managed body that calls
/// Ro3Emu.ClientHooks.UrlRewrite.Open. OpenURL ships as a pure InternalCall extern (impl flag InternalCall, no IL
/// body, RVA 0): clicking the in-game recharge / monthly-pass button opens https://console.playgenesis3.is/ through
/// it (Frida on the live client: OpenURL -&gt; ShellExecuteW). That URL is built client-side (never fetched from the
/// server, never a plaintext literal), so a string rewrite cannot reach it, and the button does not read the Lua
/// publicparamconfig.RechargeExternalUrl table. Rewriting at the OpenURL boundary catches every caller: the game, the
/// SDK, and the reflection call Assembly-CSharp makes (its "OpenURL" UTF-16 literal), which an IL call-site rewrite
/// would miss.
/// The pristine extern is recognised before it is replaced, so another client build fails the check instead of being
/// patched blindly; repeating the patch on its own output changes nothing. The address itself is written into the
/// installed copy of the hooks DLL (<see cref="SetPurchaseUrl"/>), so the prebuilt hooks need no rebuild per server.
/// (In this file because Ro3Emu.Setup compiles the patcher sources by name.)
/// </summary>
public static class OpenUrlPatch
{
    public const string ApplicationType = "UnityEngine.Application";
    public const string OpenUrlMethod = "OpenURL";

    /// <summary>What UrlRewrite.Target() returns in the hooks build; <see cref="SetPurchaseUrl"/> replaces it.</summary>
    public const string PurchaseUrlPlaceholder = "ro3emu:purchase-url-unset";

    private const string HooksTypeFullName = "Ro3Emu.ClientHooks.UrlRewrite";
    private const string HooksNamespace = "Ro3Emu.ClientHooks";
    private const string HooksTypeName = "UrlRewrite";
    private const string HooksOpenMethod = "Open";
    private const string HooksAssembly = "Ro3Emu.ClientHooks";

    /// <summary>
    /// An absolute http or https address. Anything else (a file, a program, a script URL) is refused: the hooks hand
    /// the address to the operating system's shell, which would run it.
    /// </summary>
    public static bool IsPurchaseUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && !string.IsNullOrEmpty(uri.Host);

    /// <summary>
    /// <paramref name="hooksDll"/>: the Ro3Emu.ClientHooks.dll to call into; UrlRewrite.Open(string) is checked
    /// against it, so a hooks build without it fails here and not in the game. Without it (the tests, the check) the
    /// reference is made by name only. Throws <see cref="InvalidOperationException"/> when this client build is not
    /// the one the patch knows.
    /// </summary>
    public static string Patch(ModuleDefinition module, string? hooksDll = null)
    {
        var application = module.GetType(ApplicationType) ?? throw new InvalidOperationException($"{ApplicationType} not found");
        var openUrl = application.Methods.SingleOrDefault(m => m.Name == OpenUrlMethod && m.IsStatic
                && m.ReturnType.MetadataType == MetadataType.Void
                && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.MetadataType == MetadataType.String)
            ?? throw new InvalidOperationException($"{ApplicationType}.{OpenUrlMethod}(string) not found");

        // Either the pristine extern (InternalCall, no IL body) or our own replacement (a managed body that already
        // calls UrlRewrite.Open). Anything else is a client build this patch does not know, and must not be rewritten.
        bool alreadyRedirected = openUrl.HasBody && openUrl.Body.Instructions.Any(i =>
            i.Operand is MethodReference r && r.Name == HooksOpenMethod && r.DeclaringType?.FullName == HooksTypeFullName);
        if (!openUrl.IsInternalCall && !alreadyRedirected)
            throw new InvalidOperationException($"{ApplicationType}.{OpenUrlMethod}(string) is neither the expected extern nor an Ro3Emu redirect (unknown client build)");

        var open = OpenHook(module, hooksDll);

        // OpenURL(string url) => UrlRewrite.Open(url); (static method: arg 0 is the url)
        openUrl.ImplAttributes = MethodImplAttributes.IL | MethodImplAttributes.Managed;
        openUrl.Body = new MethodBody(openUrl) { MaxStackSize = 8 };
        var il = openUrl.Body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Call, open));
        il.Append(il.Create(OpCodes.Ret));
        return "Application.OpenURL -> UrlRewrite.Open";
    }

    /// <summary>
    /// Writes <paramref name="url"/> into the hooks DLL the patcher has just installed, in place of the
    /// <see cref="PurchaseUrlPlaceholder"/> that UrlRewrite.Target() returns. A hooks build without that slot (an older
    /// prebuilt DLL) is refused, so the client never gets a redirect that goes nowhere.
    /// </summary>
    public static void SetPurchaseUrl(string installedHooksDll, string url)
    {
        if (!IsPurchaseUrl(url)) throw new ArgumentException($"'{url}' is not an http or https address", nameof(url));
        using (var hooks = AssemblyDefinition.ReadAssembly(installedHooksDll, new ReaderParameters { InMemory = true }))
        {
            var slot = hooks.MainModule.GetType(HooksTypeFullName)?.Methods.Where(m => m.HasBody)
                .SelectMany(m => m.Body.Instructions)
                .FirstOrDefault(i => i.OpCode == OpCodes.Ldstr && i.Operand is PurchaseUrlPlaceholder)
                ?? throw new InvalidOperationException($"{Path.GetFileName(installedHooksDll)} has no purchase address slot ({HooksTypeFullName}); "
                    + "this RO3Emu build's client hooks are older than --purchase-url");
            slot.Operand = url;
            hooks.Write(installedHooksDll);
        }
    }

    /// <summary>
    /// The Ro3Emu.ClientHooks.UrlRewrite.Open(string) reference imported into <paramref name="module"/>. With the
    /// hooks DLL the method must exist there (same shape as VoiceStubPatch's check); without it the reference is built
    /// by name, so the tests need no hooks build.
    /// </summary>
    private static MethodReference OpenHook(ModuleDefinition module, string? hooksDll)
    {
        if (hooksDll != null)
        {
            using var hooks = AssemblyDefinition.ReadAssembly(hooksDll, new ReaderParameters { InMemory = true });
            var type = hooks.MainModule.GetType(HooksTypeFullName)
                ?? throw new InvalidOperationException($"{HooksTypeFullName} not found in {Path.GetFileName(hooksDll)}");
            var method = type.Methods.SingleOrDefault(m => m.Name == HooksOpenMethod && m.IsStatic && m.IsPublic
                    && m.ReturnType.MetadataType == MetadataType.Void
                    && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.MetadataType == MetadataType.String)
                ?? throw new InvalidOperationException($"{HooksTypeFullName}.{HooksOpenMethod}(string) not found in {Path.GetFileName(hooksDll)}");
            return module.ImportReference(method);
        }

        var scope = module.AssemblyReferences.FirstOrDefault(r => r.Name == HooksAssembly);
        if (scope == null)
        {
            scope = new AssemblyNameReference(HooksAssembly, new Version(0, 0, 0, 0));
            module.AssemblyReferences.Add(scope);
        }
        var typeRef = new TypeReference(HooksNamespace, HooksTypeName, module, scope);
        var reference = new MethodReference(HooksOpenMethod, module.TypeSystem.Void, typeRef) { HasThis = false };
        reference.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        return reference;
    }
}
