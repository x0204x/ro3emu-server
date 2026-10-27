using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Ro3Emu.ClientTools;

/// <summary>
/// Replaces the Recovery client's scene-zero single-instance call with RO3Emu's two-slot guard. The launcher proof
/// call immediately after it is a required part of the recognised startup shape and is never removed or replaced.
/// </summary>
public static class ControlledMultiInstancePatch
{
    public const string StartupType = "HappyEngine.HybridCLR.StartupWithHybridCLR";
    public const string OriginalGuardType = "RO3.ClientSecurity.SingleInstanceGuard";
    public const string HooksGuardType = "Ro3Emu.ClientHooks.ControlledMultiInstanceGuard";
    public const string LauncherProofType = "RO3.ClientSecurity.LauncherProofGuard";

    private const string HooksAssembly = "Ro3Emu.ClientHooks";

    public static string Patch(ModuleDefinition module, string? hooksDll = null)
    {
        var startup = module.GetType(StartupType)
            ?? throw new InvalidOperationException($"{StartupType} not found");
        var awake = startup.Methods.SingleOrDefault(m => m.Name == "Awake" && !m.IsStatic && m.Parameters.Count == 0
            && m.ReturnType.MetadataType == MetadataType.Void && m.HasBody)
            ?? throw new InvalidOperationException($"{StartupType}.Awake() not found");

        var original = Calls(awake, OriginalGuardType).ToList();
        var patched = Calls(awake, HooksGuardType).ToList();
        var proof = Calls(awake, LauncherProofType).ToList();
        if (proof.Count != 1)
            throw new InvalidOperationException($"{StartupType}.Awake has {proof.Count} LauncherProofGuard.Enforce calls, expected one");
        if (original.Count + patched.Count != 1)
            throw new InvalidOperationException($"{StartupType}.Awake has an unknown single-instance guard shape");

        var guardCall = original.SingleOrDefault() ?? patched.Single();
        if (awake.Body.Instructions.IndexOf(guardCall) >= awake.Body.Instructions.IndexOf(proof[0]))
            throw new InvalidOperationException($"{StartupType}.Awake does not run the instance guard before LauncherProofGuard");

        MethodReference hook = Hook(module, hooksDll);
        if (original.Count == 1) original[0].Operand = hook;
        else patched[0].Operand = hook; // normalise the assembly reference and validate the shipped hooks method

        return "StartupWithHybridCLR.Awake single-instance guard -> controlled two-client slots; LauncherProofGuard kept";
    }

    private static IEnumerable<Instruction> Calls(MethodDefinition method, string declaringType) =>
        method.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Call
            && instruction.Operand is MethodReference { Name: "Enforce", HasThis: false } target
            && target.Parameters.Count == 0 && target.ReturnType.MetadataType == MetadataType.Void
            && target.DeclaringType.FullName == declaringType);

    private static MethodReference Hook(ModuleDefinition module, string? hooksDll)
    {
        using var hooks = hooksDll == null
            ? null
            : AssemblyDefinition.ReadAssembly(hooksDll, new ReaderParameters { InMemory = true });
        var definition = hooks?.MainModule.GetType(HooksGuardType);
        if (hooks != null && (definition == null || !definition.Methods.Any(IsGuardMethod)))
            throw new InvalidOperationException($"{HooksGuardType}.Enforce() not found in {Path.GetFileName(hooksDll)}");

        var scope = module.AssemblyReferences.FirstOrDefault(reference => reference.Name == HooksAssembly);
        if (scope == null)
        {
            scope = new AssemblyNameReference(HooksAssembly, hooks?.Name.Version ?? new Version(0, 0, 0, 0));
            module.AssemblyReferences.Add(scope);
        }
        else if (hooks != null)
        {
            // An assembly may already have been patched by an older RO3Emu build. Keep its reference aligned with
            // the hooks DLL that will actually be installed beside it instead of preserving a stale build version.
            scope.Version = hooks.Name.Version;
        }
        var type = new TypeReference("Ro3Emu.ClientHooks", "ControlledMultiInstanceGuard", module, scope);
        return new MethodReference("Enforce", module.TypeSystem.Void, type) { HasThis = false };
    }

    private static bool IsGuardMethod(MethodDefinition method) =>
        method.Name == "Enforce" && method.IsPublic && method.IsStatic && method.Parameters.Count == 0
        && method.ReturnType.MetadataType == MetadataType.Void;
}
