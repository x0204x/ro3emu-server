using Mono.Cecil;
using Mono.Cecil.Cil;
using Ro3Emu.ClientTools;

namespace Ro3Emu.Setup.Tests;

public sealed class AsiaProfilePatchTests
{
    [Fact]
    public void RecoveryAdapterGettersAreReplacedWithTheSelectedServer()
    {
        using var module = ModuleDefinition.CreateModule("RO3.AsiaProfile", ModuleKind.Dll);
        var adapter = new TypeDefinition("", "RecoveryServerListAdapter",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, module.TypeSystem.Object);
        module.Types.Add(adapter);
        AddStringProperty(module, adapter, "AuthLoginUrl", "https://asia.invalid/sdk/login");
        AddStringProperty(module, adapter, "ServerListUrl", "https://asia.invalid/serverlist/v2/168/serverlist.json");
        AddStringProperty(module, adapter, "RecoveryGmtUrl", "https://asia.invalid");
        AddNoticeImageGuard(module, adapter);

        AsiaProfilePatch.Patch(module, "http://127.0.0.1:5380", "/sdk/login",
            "/serverlist/v2/168/serverlist.json?inline=true&force_refresh=true");

        Assert.Equal("http://127.0.0.1:5380/sdk/login", GetterLiteral(adapter, "AuthLoginUrl"));
        Assert.Equal("http://127.0.0.1:5380/serverlist/v2/168/serverlist.json?inline=true&force_refresh=true",
            GetterLiteral(adapter, "ServerListUrl"));
        Assert.Equal("http://127.0.0.1:5380", GetterLiteral(adapter, "RecoveryGmtUrl"));
        Assert.Equal(OpCodes.Ldc_I4_0,
            adapter.Methods.Single(m => m.Name == "IsAllowedNoticeImageRequest").Body.Instructions[0].OpCode);
    }

    [Fact]
    public void UnexpectedAdapterShapeFailsClosed()
    {
        using var module = ModuleDefinition.CreateModule("RO3.AsiaProfile", ModuleKind.Dll);
        Assert.Throws<InvalidOperationException>(() => AsiaProfilePatch.Patch(module, "http://127.0.0.1:5380",
            "/sdk/login", "/serverlist/v2/168/serverlist.json?inline=true&force_refresh=true"));
    }

    private static void AddStringProperty(ModuleDefinition module, TypeDefinition type, string name, string original)
    {
        var getter = new MethodDefinition("get_" + name,
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
            module.TypeSystem.String);
        var il = getter.Body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldstr, original));
        il.Append(il.Create(OpCodes.Ret));
        type.Methods.Add(getter);
        type.Properties.Add(new PropertyDefinition(name, PropertyAttributes.None, module.TypeSystem.String) { GetMethod = getter });
    }

    private static string GetterLiteral(TypeDefinition type, string property) =>
        (string)type.Properties.Single(p => p.Name == property).GetMethod.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldstr).Operand;

    private static void AddNoticeImageGuard(ModuleDefinition module, TypeDefinition type)
    {
        var method = new MethodDefinition("IsAllowedNoticeImageRequest",
            MethodAttributes.Public | MethodAttributes.Static,
            module.TypeSystem.Boolean);
        method.Parameters.Add(new ParameterDefinition("url", ParameterAttributes.None, module.TypeSystem.String));
        var il = method.Body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Ret));
        type.Methods.Add(method);
    }
}
