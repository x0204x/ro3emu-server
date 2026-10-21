using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Ro3Emu.ClientTools;

/// <summary>
/// Makes a private method or field internal when code outside its type (and outside the types nested in it) uses it.
/// Mono refuses such a use with MethodAccessException / FieldAccessException when it compiles the using method.
/// The client of 20 Oct 2026 has one: HttpNetworkManager.RecoveryOfficialSdkLoginStart (Happyengine.Network) builds a
/// delegate to SealedFallback_SelectionFailed, a private method of its login coroutine, so every account login throws
/// before its request is sent (issue #19). Internal keeps the member out of reach of other assemblies.
/// </summary>
public static class AccessFixPatch
{
    /// <summary>Widens what <paramref name="module"/> itself cannot reach; returns the members it changed.</summary>
    public static List<string> Patch(ModuleDefinition module)
    {
        var widened = new List<string>();
        foreach (var type in module.GetTypes())
            foreach (var method in type.Methods.Where(m => m.HasBody))
                foreach (var instruction in method.Body.Instructions)
                    if (Target(instruction, module) is { } target && !Reaches(type, target.DeclaringType)
                        && Widen(target))
                        widened.Add(target.FullName);
        return widened;
    }

    /// <summary>The private method or field of this module an instruction uses, if any.</summary>
    private static IMemberDefinition? Target(Instruction instruction, ModuleDefinition module)
    {
        IMemberDefinition? member = instruction.Operand switch
        {
            MethodReference method when method.Module == module => method.Resolve(),
            FieldReference field when field.Module == module => field.Resolve(),
            _ => null,
        };
        return member?.DeclaringType.Module == module ? member : null;
    }

    /// <summary>Private members are visible to their type and to the types nested in it (ECMA-335 I.8.5.3.2).</summary>
    private static bool Reaches(TypeDefinition user, TypeDefinition owner)
    {
        for (var type = user; type != null; type = type.DeclaringType)
            if (type == owner) return true;
        return false;
    }

    private static bool Widen(IMemberDefinition member)
    {
        switch (member)
        {
            case MethodDefinition { IsPrivate: true } method:
                method.IsAssembly = true;
                return true;
            case FieldDefinition { IsPrivate: true } field:
                field.IsAssembly = true;
                return true;
            default:
                return false;
        }
    }
}
