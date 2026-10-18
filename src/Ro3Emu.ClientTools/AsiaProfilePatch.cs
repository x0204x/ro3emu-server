using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Ro3Emu.ClientTools;

/// <summary>
/// Repoints the Recovery/Asia profile adapter. That adapter prefers region-profile.ini over the ordinary URL
/// literals, so string replacement alone leaves this client talking to the publisher's SDK and server list.
/// </summary>
public static class AsiaProfilePatch
{
    private const string AdapterType = "RecoveryServerListAdapter";

    public static string Patch(ModuleDefinition module, string serverBaseUrl, string loginPath, string serverListPathAndQuery)
    {
        var adapter = module.GetType(AdapterType)
            ?? throw new InvalidOperationException($"{AdapterType} not found in RO3.AsiaProfile.dll");

        ReplaceGetter(adapter, "AuthLoginUrl", serverBaseUrl + loginPath);
        ReplaceGetter(adapter, "ServerListUrl", serverBaseUrl + serverListPathAndQuery);
        ReplaceGetter(adapter, "RecoveryGmtUrl", serverBaseUrl);
        DisableExternalNoticeImages(adapter);
        return "Asia Recovery SDK, server list and GMT origins -> " + serverBaseUrl
            + "; external notice images disabled";
    }

    private static void ReplaceGetter(TypeDefinition type, string propertyName, string value)
    {
        var property = type.Properties.SingleOrDefault(p => p.Name == propertyName)
            ?? throw new InvalidOperationException($"{AdapterType}.{propertyName} not found");
        var getter = property.GetMethod
            ?? throw new InvalidOperationException($"{AdapterType}.{propertyName} has no getter");
        if (!getter.IsStatic || getter.Parameters.Count != 0 || getter.ReturnType.FullName != "System.String")
            throw new InvalidOperationException($"{AdapterType}.{propertyName} has an unexpected signature");

        getter.Body.Instructions.Clear();
        getter.Body.ExceptionHandlers.Clear();
        getter.Body.Variables.Clear();
        getter.Body.InitLocals = false;
        var il = getter.Body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldstr, value));
        il.Append(il.Create(OpCodes.Ret));
    }

    private static void DisableExternalNoticeImages(TypeDefinition adapter)
    {
        var method = adapter.Methods.SingleOrDefault(m => m.Name == "IsAllowedNoticeImageRequest")
            ?? throw new InvalidOperationException($"{AdapterType}.IsAllowedNoticeImageRequest not found");
        if (!method.IsStatic || method.Parameters.Count != 1
            || method.Parameters[0].ParameterType.FullName != "System.String"
            || method.ReturnType.FullName != "System.Boolean")
        {
            throw new InvalidOperationException(
                $"{AdapterType}.IsAllowedNoticeImageRequest has an unexpected signature");
        }

        method.Body.Instructions.Clear();
        method.Body.ExceptionHandlers.Clear();
        method.Body.Variables.Clear();
        method.Body.InitLocals = false;
        var il = method.Body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Ret));
    }
}
