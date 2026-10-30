using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Ro3Emu.ClientTools;

/// <summary>
/// Layer 1 of the localization: every text the client draws passes Ro3Emu.Loc.Ro3Loc.Map* first.
/// <list type="bullet">
/// <item>TMP_Text.set_text(string) and every SetText(string, ...) overload: the string argument is mapped at entry;</item>
/// <item>TextMeshProUGUI.Awake / TextMeshPro.Awake: m_text (text baked into the prefab) is mapped before the first layout;</item>
/// <item>HUDTextHelper.SetWordCell: kName (overhead names, titles, bubbles) is mapped at entry.</item>
/// </list>
/// </summary>
internal static class TextSink
{
    public static string PatchTmp(ModuleDefinition module, string locDll, string hooksDll)
    {
        using var loc = AssemblyDefinition.ReadAssembly(locDll, new ReaderParameters { InMemory = true });
        var map = module.ImportReference(Method(loc, "Map"));
        var mapAwake = module.ImportReference(Method(loc, "MapAwake"));
        // Ro3Emu.ClientHooks.TextFit.Touch(object): labels whose English no longer fits get auto-size one frame later.
        using var hooks = AssemblyDefinition.ReadAssembly(hooksDll, new ReaderParameters { InMemory = true });
        var touch = module.ImportReference((hooks.MainModule.GetType("Ro3Emu.ClientHooks.TextFit") ?? throw new InvalidOperationException("TextFit not found in the hooks"))
            .Methods.Single(m => m.Name == "Touch" && m.Parameters.Count == 1));

        var tmpText = module.GetType("TMPro.TMP_Text") ?? throw new InvalidOperationException("TMPro.TMP_Text not found");
        int setters = 0;
        foreach (var method in tmpText.Methods)
        {
            bool setter = method.Name == "set_text" && method.Parameters.Count == 1;
            bool setText = method.Name == "SetText" && method.Parameters.Count >= 1 && method.Parameters[0].ParameterType.FullName == "System.String";
            if (!setter && !setText) continue;
            MapArgument(method, method.Parameters[0], map);
            Report(method, touch);
            setters++;
        }
        if (setters < 2) throw new InvalidOperationException($"TMP_Text setters not found ({setters})");

        var field = tmpText.Fields.SingleOrDefault(f => f.Name == "m_text") ?? throw new InvalidOperationException("TMP_Text.m_text not found");
        foreach (var typeName in new[] { "TMPro.TextMeshProUGUI", "TMPro.TextMeshPro" })
        {
            var awake = module.GetType(typeName)?.Methods.SingleOrDefault(m => m.Name == "Awake" && m.Parameters.Count == 0)
                ?? throw new InvalidOperationException($"{typeName}.Awake not found");
            // this.m_text = Ro3Loc.MapAwake(this.m_text);
            var il = awake.Body.GetILProcessor();
            var first = awake.Body.Instructions[0];
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, il.Create(OpCodes.Ldfld, field));
            il.InsertBefore(first, il.Create(OpCodes.Call, mapAwake));
            il.InsertBefore(first, il.Create(OpCodes.Stfld, field));
            Report(awake, touch);
        }
        return $"text sink: TMP_Text {setters} setters + 2 Awake -> Ro3Loc, TextFit";
    }

    public static string PatchHud(ModuleDefinition module, string locDll)
    {
        using var loc = AssemblyDefinition.ReadAssembly(locDll, new ReaderParameters { InMemory = true });
        var map = module.ImportReference(Method(loc, "MapHud"));
        var helper = module.GetType("HUDTextHelper") ?? throw new InvalidOperationException("HUDTextHelper not found");
        int patched = 0;
        foreach (var method in helper.Methods.Where(m => m.Name == "SetWordCell"))
        {
            var name = method.Parameters.FirstOrDefault(p => p.Name == "kName" && p.ParameterType.FullName == "System.String");
            if (name == null) continue;
            MapArgument(method, name, map);
            patched++;
        }
        if (patched == 0) throw new InvalidOperationException("HUDTextHelper.SetWordCell(kName) not found");
        return $"text sink: HUDTextHelper.SetWordCell x{patched} -> Ro3Loc";
    }

    /// <summary>
    /// Boot / patch screen: LauncherLanguage.Get(key) falls back to its hard-coded Chinese because the shipped
    /// GameUpdateContent_en.json has "None" keys; every value it returns goes through Map.
    /// </summary>
    public static string PatchLauncher(ModuleDefinition module, string locDll)
    {
        using var loc = AssemblyDefinition.ReadAssembly(locDll, new ReaderParameters { InMemory = true });
        var map = module.ImportReference(Method(loc, "Map"));
        var get = module.GetType("HappyEngine.Runtime.NewRuntimeSystem.LauncherLanguage")?.Methods
            .SingleOrDefault(m => m.Name == "Get" && m.Parameters.Count == 1) ?? throw new InvalidOperationException("LauncherLanguage.Get not found");
        var il = get.Body.GetILProcessor();
        var returns = get.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList();
        foreach (var ret in returns)
        {
            // value on the stack -> Map(value); branches that target the ret land on the call instead.
            var call = il.Create(OpCodes.Call, map);
            il.InsertBefore(ret, call);
            foreach (var i in get.Body.Instructions)
                if (i.Operand == ret && i != call) i.Operand = call;
        }
        return $"text sink: LauncherLanguage.Get x{returns.Count} -> Ro3Loc";
    }

    /// <summary>
    /// LanguageMain.Get(long sid) (Obf_gO, used by LangTransUtils_TMP and as Language.Trans' last fallback): each return
    /// value goes through Ro3Loc.Sid(value, sid), which fills the sids that exist in no table.
    /// </summary>
    public static string PatchLanguageGet(ModuleDefinition module, string locDll)
    {
        NativeLanguageReader(module);
        using var loc = AssemblyDefinition.ReadAssembly(locDll, new ReaderParameters { InMemory = true });
        var sid = module.ImportReference(loc.MainModule.GetType("Ro3Emu.Loc.Ro3Loc").Methods.Single(m => m.Name == "Sid"));
        var get = module.GetType("LanguageMain")?.Methods
            .SingleOrDefault(m => m.Name == "Obf_gO" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.Int64" && m.ReturnType.FullName == "System.String")
            ?? throw new InvalidOperationException("LanguageMain.Obf_gO(long) not found (different client build?)");
        var il = get.Body.GetILProcessor();
        var returns = get.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList();
        foreach (var ret in returns)
        {
            var load = il.Create(OpCodes.Ldarg_0);
            il.InsertBefore(ret, load);
            il.InsertBefore(ret, il.Create(OpCodes.Call, sid));
            foreach (var i in get.Body.Instructions)
                if (i.Operand == ret) i.Operand = load;
        }
        return $"text sink: LanguageMain.Get(sid) x{returns.Count} -> Ro3Loc.Sid";
    }

    /// <summary>G 140083 的 GetCurrentLanguage（Lua_UT_Helper 3798）；只讀取，絕不呼叫 SetCurrentLanguage。</summary>
    internal static void NativeLanguageReader(ModuleDefinition module)
    {
        var language = module.GetType("LanguageMain") ?? throw new InvalidOperationException("LanguageMain not found");
        var getter = language.Methods.SingleOrDefault(m => m.Name == "Obf_cO" && m.IsStatic &&
            m.Parameters.Count == 0 && m.ReturnType.FullName == "System.String");
        if (getter is not { HasBody: true } ||
            !getter.Body.Instructions.Any(i => i.Operand is MethodReference m &&
                m.DeclaringType.FullName == "HappyEngine.AppConst" && m.Name == "get_DefaultLanguage"))
            throw new InvalidOperationException("native GetCurrentLanguage contract differs from verified Genesis client");
        if (language.Methods.Any(m => m.Name == "Ro3Emu_GetNativeLanguage"))
            throw new InvalidOperationException("native language shim already exists; restore the original first");
        var shim = new MethodDefinition("Ro3Emu_GetNativeLanguage", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.String);
        shim.Body.Instructions.Add(Instruction.Create(OpCodes.Call, getter));
        shim.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        language.Methods.Add(shim);
    }

    private static MethodDefinition Method(AssemblyDefinition loc, string name) =>
        loc.MainModule.GetType("Ro3Emu.Loc.Ro3Loc").Methods.Single(m => m.Name == name && m.Parameters.Count == 1);

    /// <summary>arg = map(arg) at method entry.</summary>
    /// <summary>hook(this) at method entry.</summary>
    private static void Report(MethodDefinition method, MethodReference hook)
    {
        var il = method.Body.GetILProcessor();
        var first = method.Body.Instructions[0];
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, il.Create(OpCodes.Call, hook));
    }

    private static void MapArgument(MethodDefinition method, ParameterDefinition parameter, MethodReference map)
    {
        var il = method.Body.GetILProcessor();
        var first = method.Body.Instructions[0];
        il.InsertBefore(first, il.Create(OpCodes.Ldarg, parameter));
        il.InsertBefore(first, il.Create(OpCodes.Call, map));
        il.InsertBefore(first, il.Create(OpCodes.Starg, parameter));
    }
}
