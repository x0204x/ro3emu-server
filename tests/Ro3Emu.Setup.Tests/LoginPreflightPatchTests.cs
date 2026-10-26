using Mono.Cecil;
using Mono.Cecil.Cil;
using Ro3Emu.ClientTools;

namespace Ro3Emu.Setup.Tests;

public sealed class LoginPreflightPatchTests
{
    [Fact]
    public void SelectCommitsFailOpenWithoutProbingAndThenCompletes()
    {
        using var module = ModuleDefinition.CreateModule("Happyengine.Network", ModuleKind.Dll);
        var preflight = new TypeDefinition("RO3.SealedLoginFallback", "UnityOriginPreflight",
            TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
        module.Types.Add(preflight);
        var commit = new MethodDefinition("Commit", MethodAttributes.Private, module.TypeSystem.Void);
        commit.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
        commit.Parameters.Add(new ParameterDefinition(module.TypeSystem.Boolean));
        commit.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
        preflight.Methods.Add(commit);
        var state = new TypeDefinition("", "<Select>d__13", TypeAttributes.NestedPrivate | TypeAttributes.Class,
            module.TypeSystem.Object);
        preflight.NestedTypes.Add(state);
        var owner = new FieldDefinition("<>4__this", FieldAttributes.Public, preflight);
        state.Fields.Add(owner);
        var moveNext = new MethodDefinition("MoveNext", MethodAttributes.Public, module.TypeSystem.Boolean);
        AddExpectedPreflightBody(module, preflight, commit, owner, moveNext);
        state.Methods.Add(moveNext);

        Assert.Equal("SDK origin preflight -> immediate fail-open", LoginPreflightPatch.Patch(module));

        Assert.Equal(
            [OpCodes.Ldarg_0, OpCodes.Ldfld, OpCodes.Ldc_I4_0, OpCodes.Ldc_I4_1, OpCodes.Call, OpCodes.Ldc_I4_0, OpCodes.Ret],
            moveNext.Body.Instructions.Select(instruction => instruction.OpCode));
        Assert.Same(owner, moveNext.Body.Instructions[1].Operand);
        Assert.Same(commit, moveNext.Body.Instructions[4].Operand);
    }

    [Fact]
    public void UnknownClientShapeFailsClosed()
    {
        using var module = ModuleDefinition.CreateModule("Happyengine.Network", ModuleKind.Dll);
        Assert.Throws<InvalidOperationException>(() => LoginPreflightPatch.Patch(module));
    }

    [Fact]
    public void MatchingShapeWithoutExpectedPreflightSemanticsFailsClosed()
    {
        using var module = ModuleDefinition.CreateModule("Happyengine.Network", ModuleKind.Dll);
        var preflight = new TypeDefinition("RO3.SealedLoginFallback", "UnityOriginPreflight",
            TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
        module.Types.Add(preflight);
        var commit = new MethodDefinition("Commit", MethodAttributes.Private, module.TypeSystem.Void);
        commit.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
        commit.Parameters.Add(new ParameterDefinition(module.TypeSystem.Boolean));
        commit.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
        preflight.Methods.Add(commit);
        var state = new TypeDefinition("", "<Select>d__13", TypeAttributes.NestedPrivate | TypeAttributes.Class,
            module.TypeSystem.Object);
        preflight.NestedTypes.Add(state);
        state.Fields.Add(new FieldDefinition("<>4__this", FieldAttributes.Public, preflight));
        var moveNext = new MethodDefinition("MoveNext", MethodAttributes.Public, module.TypeSystem.Boolean);
        moveNext.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ldc_I4_0));
        moveNext.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
        state.Methods.Add(moveNext);

        var error = Assert.Throws<InvalidOperationException>(() => LoginPreflightPatch.Patch(module));

        Assert.Contains("semantic fingerprint", error.Message, StringComparison.Ordinal);
    }

    private static void AddExpectedPreflightBody(ModuleDefinition module, TypeDefinition preflight,
        MethodDefinition commit, FieldDefinition owner, MethodDefinition moveNext)
    {
        var request = new TypeDefinition("UnityEngine.Networking", "UnityWebRequest",
            TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
        module.Types.Add(request);
        var get = new MethodDefinition("Get", MethodAttributes.Public | MethodAttributes.Static, request);
        get.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        request.Methods.Add(get);
        var send = new MethodDefinition("SendWebRequest", MethodAttributes.Public, module.TypeSystem.Object);
        request.Methods.Add(send);
        var il = moveNext.Body.GetILProcessor();
        il.Append(Instruction.Create(OpCodes.Ldstr, "/sdk/health"));
        il.Append(Instruction.Create(OpCodes.Call, get));
        il.Append(Instruction.Create(OpCodes.Callvirt, send));
        il.Append(Instruction.Create(OpCodes.Pop));
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Ldfld, owner));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
        il.Append(Instruction.Create(OpCodes.Call, commit));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
        il.Append(Instruction.Create(OpCodes.Ret));
    }
}
