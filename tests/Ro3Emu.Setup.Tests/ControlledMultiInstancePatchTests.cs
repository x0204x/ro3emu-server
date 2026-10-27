using Mono.Cecil;
using Mono.Cecil.Cil;
using Ro3Emu.ClientTools;

namespace Ro3Emu.Setup.Tests;

public sealed class ControlledMultiInstancePatchTests : IDisposable
{
    readonly string _hooks = Path.Combine(Path.GetTempPath(), "ro3emu-hooks-" + Guid.NewGuid().ToString("N") + ".dll");

    public ControlledMultiInstancePatchTests() => WriteHooks(_hooks);

    public void Dispose()
    {
        try { File.Delete(_hooks); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void ReplacesOnlyTheSingleInstanceCallAndKeepsLauncherProof()
    {
        using var module = ClientStartup(includeProof: true);

        Assert.Contains("LauncherProofGuard kept", ControlledMultiInstancePatch.Patch(module, _hooks));

        var calls = Awake(module).Body.Instructions
            .Where(instruction => instruction.OpCode == OpCodes.Call)
            .Select(instruction => Assert.IsAssignableFrom<MethodReference>(instruction.Operand).DeclaringType.FullName)
            .ToList();
        Assert.Equal([ControlledMultiInstancePatch.HooksGuardType, ControlledMultiInstancePatch.LauncherProofType], calls);
        Assert.Contains(module.AssemblyReferences, reference => reference.Name == "Ro3Emu.ClientHooks");

        using var bytes = new MemoryStream();
        module.Write(bytes);
        bytes.Position = 0;
        using var reloaded = ModuleDefinition.ReadModule(bytes);
        Assert.Equal(calls, Awake(reloaded).Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Call)
            .Select(instruction => ((MethodReference)instruction.Operand).DeclaringType.FullName));
    }

    [Fact]
    public void ASecondPatchIsIdempotent()
    {
        using var module = ClientStartup(includeProof: true);
        ControlledMultiInstancePatch.Patch(module, _hooks);
        module.AssemblyReferences.Single(reference => reference.Name == "Ro3Emu.ClientHooks").Version = new Version(0, 0);

        ControlledMultiInstancePatch.Patch(module, _hooks);

        Assert.Single(Awake(module).Body.Instructions, instruction => instruction.OpCode == OpCodes.Call
            && instruction.Operand is MethodReference target
            && target.DeclaringType.FullName == ControlledMultiInstancePatch.HooksGuardType);
        Assert.Equal(new Version(1, 0, 0, 0),
            module.AssemblyReferences.Single(reference => reference.Name == "Ro3Emu.ClientHooks").Version);
    }

    [Fact]
    public void MissingLauncherProofFailsClosed()
    {
        using var module = ClientStartup(includeProof: false);

        var error = Assert.Throws<InvalidOperationException>(() => ControlledMultiInstancePatch.Patch(module, _hooks));

        Assert.Contains("LauncherProofGuard.Enforce", error.Message, StringComparison.Ordinal);
        Assert.Single(Awake(module).Body.Instructions, instruction => instruction.Operand is MethodReference target
            && target.DeclaringType.FullName == ControlledMultiInstancePatch.OriginalGuardType);
    }

    [Fact]
    public void UnknownGuardShapeFailsClosed()
    {
        using var module = ClientStartup(includeProof: true);
        var awake = Awake(module);
        awake.Body.GetILProcessor().InsertBefore(awake.Body.Instructions[^1],
            Instruction.Create(OpCodes.Call, Guard(module, ControlledMultiInstancePatch.OriginalGuardType)));

        Assert.Throws<InvalidOperationException>(() => ControlledMultiInstancePatch.Patch(module, _hooks));
    }

    private static ModuleDefinition ClientStartup(bool includeProof)
    {
        var module = ModuleDefinition.CreateModule("com.happyengine-hybridclr", ModuleKind.Dll);
        var startup = new TypeDefinition("HappyEngine.HybridCLR", "StartupWithHybridCLR",
            TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
        module.Types.Add(startup);
        var awake = new MethodDefinition("Awake", MethodAttributes.Private, module.TypeSystem.Void);
        startup.Methods.Add(awake);
        var il = awake.Body.GetILProcessor();
        il.Append(Instruction.Create(OpCodes.Call, Guard(module, ControlledMultiInstancePatch.OriginalGuardType)));
        if (includeProof) il.Append(Instruction.Create(OpCodes.Call, Guard(module, ControlledMultiInstancePatch.LauncherProofType)));
        il.Append(Instruction.Create(OpCodes.Ret));
        return module;
    }

    private static MethodReference Guard(ModuleDefinition module, string fullName)
    {
        int separator = fullName.LastIndexOf('.');
        var type = new TypeReference(fullName[..separator], fullName[(separator + 1)..], module, module.TypeSystem.CoreLibrary);
        return new MethodReference("Enforce", module.TypeSystem.Void, type) { HasThis = false };
    }

    private static MethodDefinition Awake(ModuleDefinition module) =>
        module.GetType(ControlledMultiInstancePatch.StartupType).Methods.Single(method => method.Name == "Awake");

    private static void WriteHooks(string path)
    {
        using var hooks = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("Ro3Emu.ClientHooks", new Version(1, 0)), "Ro3Emu.ClientHooks", ModuleKind.Dll);
        var type = new TypeDefinition("Ro3Emu.ClientHooks", "ControlledMultiInstanceGuard",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, hooks.MainModule.TypeSystem.Object);
        var enforce = new MethodDefinition("Enforce", MethodAttributes.Public | MethodAttributes.Static, hooks.MainModule.TypeSystem.Void);
        enforce.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
        type.Methods.Add(enforce);
        hooks.MainModule.Types.Add(type);
        hooks.Write(path);
    }
}
