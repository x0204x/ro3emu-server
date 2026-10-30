using Mono.Cecil;
using Mono.Cecil.Cil;
using Ro3Emu.ClientTools;

namespace Ro3Emu.Setup.Tests;

public sealed class NativeLanguageReaderPatchTests
{
    [Fact]
    public void NarrowShimOnlyCallsNativeGetterAndDuplicatePatchIsRejected()
    {
        using var module = ModuleDefinition.CreateModule("SyntheticLanguage", ModuleKind.Dll);
        var language = new TypeDefinition("", "LanguageMain", TypeAttributes.Public, module.TypeSystem.Object);
        module.Types.Add(language);
        var appConst = new TypeReference("HappyEngine", "AppConst", module, module);
        var getter = new MethodDefinition("Obf_cO", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.String);
        getter.Body.Instructions.Add(Instruction.Create(OpCodes.Call, new MethodReference("get_DefaultLanguage", module.TypeSystem.String, appConst) { HasThis = false }));
        getter.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        language.Methods.Add(getter);
        TextSink.NativeLanguageReader(module);
        var shim = Assert.Single(language.Methods, m => m.Name == "Ro3Emu_GetNativeLanguage");
        Assert.True(shim.IsPublic && shim.IsStatic);
        Assert.Empty(shim.Parameters);
        Assert.Equal(2, shim.Body.Instructions.Count);
        Assert.Same(getter, shim.Body.Instructions[0].Operand);
        Assert.Equal(OpCodes.Ret, shim.Body.Instructions[1].OpCode);
        Assert.Throws<InvalidOperationException>(() => TextSink.NativeLanguageReader(module));
    }

    [Fact]
    public void DifferentClientContractDoesNotGetAShim()
    {
        using var module = ModuleDefinition.CreateModule("SyntheticDifferentLanguage", ModuleKind.Dll);
        var language = new TypeDefinition("", "LanguageMain", TypeAttributes.Public, module.TypeSystem.Object);
        module.Types.Add(language);
        Assert.Throws<InvalidOperationException>(() => TextSink.NativeLanguageReader(module));
        Assert.Empty(language.Methods);
    }
}
