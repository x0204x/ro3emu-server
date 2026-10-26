using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Ro3Emu.ClientTools;

/// <summary>
/// A self-hosted client already has an explicit SDK URL. The publisher's sealed-login wrapper nevertheless probes five
/// unrelated public origins before it starts that request; a blocked probe can keep the login button in its pending
/// state for 20-40 seconds, and disposing the coroutine during that wait reports a silent failure. Keep the wrapper's
/// cleanup/failure callback, but make its origin-selection iterator fail open immediately. <c>RewriteUrl</c> leaves
/// RO3Emu's non-publisher origin unchanged, so the legacy request still goes directly to the selected server.
/// </summary>
public static class LoginPreflightPatch
{
    private const string PreflightType = "RO3.SealedLoginFallback.UnityOriginPreflight";

    public static string Patch(ModuleDefinition module)
    {
        var preflight = module.GetType(PreflightType)
            ?? throw new InvalidOperationException($"{PreflightType} not found");
        var commit = preflight.Methods.SingleOrDefault(method => method.Name == "Commit" && !method.IsStatic
            && method.ReturnType.MetadataType == MetadataType.Void && method.Parameters.Count == 2
            && method.Parameters[0].ParameterType.MetadataType == MetadataType.Int32
            && method.Parameters[1].ParameterType.MetadataType == MetadataType.Boolean)
            ?? throw new InvalidOperationException("UnityOriginPreflight.Commit(int, bool) not found");
        var state = preflight.NestedTypes.SingleOrDefault(type => type.Name.StartsWith("<Select>d__", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("UnityOriginPreflight Select state machine not found");
        var owner = state.Fields.SingleOrDefault(field => field.FieldType.FullName == preflight.FullName)
            ?? throw new InvalidOperationException("UnityOriginPreflight Select owner field not found");
        var moveNext = state.Methods.SingleOrDefault(method => method.Name == "MoveNext" && !method.IsStatic
            && method.Parameters.Count == 0 && method.ReturnType.MetadataType == MetadataType.Boolean && method.HasBody)
            ?? throw new InvalidOperationException("UnityOriginPreflight Select MoveNext not found");

        var instructions = moveNext.Body.Instructions;
        var hasHealthPath = instructions.Any(instruction => instruction.OpCode == OpCodes.Ldstr
            && string.Equals(instruction.Operand as string, "/sdk/health", StringComparison.Ordinal));
        var hasGet = instructions.Any(instruction => IsCall(instruction, "UnityEngine.Networking.UnityWebRequest", "Get"));
        var hasSend = instructions.Any(instruction => IsCall(instruction, "UnityEngine.Networking.UnityWebRequest", "SendWebRequest"));
        var hasCommit = instructions.Any(instruction => instruction.Operand is MethodReference method
            && method.Name == commit.Name && method.DeclaringType.FullName == commit.DeclaringType.FullName);
        if (!hasHealthPath || !hasGet || !hasSend || !hasCommit)
            throw new InvalidOperationException("UnityOriginPreflight Select semantic fingerprint not found");

        moveNext.Body.Instructions.Clear();
        moveNext.Body.ExceptionHandlers.Clear();
        moveNext.Body.Variables.Clear();
        var il = moveNext.Body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldfld, owner));
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Call, commit));
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Ret));
        return "SDK origin preflight -> immediate fail-open";
    }

    private static bool IsCall(Instruction instruction, string declaringType, string methodName) =>
        (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt)
        && instruction.Operand is MethodReference method
        && method.Name == methodName
        && method.DeclaringType.FullName == declaringType;
}
