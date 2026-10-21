using Mono.Cecil;
using Mono.Cecil.Cil;
using Ro3Emu.ClientTools;

namespace Ro3Emu.Setup.Tests;

/// <summary>
/// <see cref="AccessFixPatch"/> on a synthetic module shaped like the 20 Oct 2026 Happyengine.Network: an outer type
/// that uses a private method and a private field of a type nested in it (Mono refuses both), and a nested type that
/// uses a private member of its outer type (allowed, so left as it is).
/// </summary>
public class AccessFixPatchTests
{
    static ModuleDefinition Module()
    {
        var module = ModuleDefinition.CreateModule("Happyengine.Network", ModuleKind.Dll);
        var outer = new TypeDefinition("", "HttpNetworkManager", TypeAttributes.Public | TypeAttributes.Class,
            module.TypeSystem.Object);
        var coroutine = new TypeDefinition("", "<LoginCoroutine>d__31",
            TypeAttributes.NestedPrivate | TypeAttributes.Class, module.TypeSystem.Object);
        outer.NestedTypes.Add(coroutine);
        module.Types.Add(outer);

        var selectionFailed = Method(coroutine, "SealedFallback_SelectionFailed",
            MethodAttributes.Private | MethodAttributes.Static);
        selectionFailed.Body.GetILProcessor().Emit(OpCodes.Ret);
        var pending = new FieldDefinition("pending", FieldAttributes.Private | FieldAttributes.Static,
            module.TypeSystem.Boolean);
        coroutine.Fields.Add(pending);
        var outerHelper = Method(outer, "Helper", MethodAttributes.Private | MethodAttributes.Static);
        outerHelper.Body.GetILProcessor().Emit(OpCodes.Ret);

        // HttpNetworkManager.LoginStart: a delegate to the coroutine's private method and a read of its private field.
        var start = Method(outer, "LoginStart", MethodAttributes.Public | MethodAttributes.Static);
        var il = start.Body.GetILProcessor();
        il.Emit(OpCodes.Ldftn, selectionFailed);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldsfld, pending);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ret);

        // The coroutine calls its outer type's private helper: legal, stays private.
        var moveNext = Method(coroutine, "MoveNext", MethodAttributes.Private | MethodAttributes.Static);
        moveNext.Body.GetILProcessor().Emit(OpCodes.Call, outerHelper);
        moveNext.Body.GetILProcessor().Emit(OpCodes.Ret);
        return module;
    }

    static MethodDefinition Method(TypeDefinition type, string name, MethodAttributes attributes)
    {
        var method = new MethodDefinition(name, attributes | MethodAttributes.HideBySig, type.Module.TypeSystem.Void);
        type.Methods.Add(method);
        return method;
    }

    static TypeDefinition Coroutine(ModuleDefinition module) =>
        module.GetType("HttpNetworkManager").NestedTypes.Single();

    [Fact]
    public void PrivateMembersTheOuterTypeUsesBecomeInternal()
    {
        using var module = Module();

        var widened = AccessFixPatch.Patch(module);

        Assert.Equal(2, widened.Count);
        Assert.True(Coroutine(module).Methods.Single(m => m.Name == "SealedFallback_SelectionFailed").IsAssembly);
        Assert.True(Coroutine(module).Fields.Single(f => f.Name == "pending").IsAssembly);
    }

    [Fact]
    public void MembersUsedOnlyWhereTheyAreVisibleStayPrivate()
    {
        using var module = Module();

        AccessFixPatch.Patch(module);

        Assert.True(module.GetType("HttpNetworkManager").Methods.Single(m => m.Name == "Helper").IsPrivate);
        Assert.True(Coroutine(module).Methods.Single(m => m.Name == "MoveNext").IsPrivate);
    }

    [Fact]
    public void ASecondPatchChangesNothing()
    {
        using var module = Module();
        AccessFixPatch.Patch(module);

        Assert.Empty(AccessFixPatch.Patch(module));
    }
}
