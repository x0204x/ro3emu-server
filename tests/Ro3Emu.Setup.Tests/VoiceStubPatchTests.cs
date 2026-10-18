using Mono.Cecil;
using Mono.Cecil.Cil;
using Ro3Emu.ClientTools;

namespace Ro3Emu.Setup.Tests;

/// <summary>The client's Assembly-CSharp.dll (RO3_CLIENT_DIR or D:\RO3\ROO_PC, the pristine backup first); read only.</summary>
static class ClientDll
{
    static readonly string Client = Environment.GetEnvironmentVariable("RO3_CLIENT_DIR") is { Length: > 0 } dir ? dir : @"D:\RO3\ROO_PC";

    public static readonly string? Path = new[]
    {
        System.IO.Path.Combine(Client, "ro3emu-backup", "Assembly-CSharp.dll"),
        System.IO.Path.Combine(Client, "ro3_Data", "Managed", "Assembly-CSharp.dll"),
    }.FirstOrDefault(File.Exists);

    /// <summary>Writing needs the client's own references (mscorlib and the rest of Managed).</summary>
    static readonly DefaultAssemblyResolver Resolver = NewResolver();

    static DefaultAssemblyResolver NewResolver()
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(System.IO.Path.Combine(Client, "ro3_Data", "Managed"));
        return resolver;
    }

    public static AssemblyDefinition Read() => AssemblyDefinition.ReadAssembly(Path!, new ReaderParameters { InMemory = true, AssemblyResolver = Resolver });

    /// <summary>Writes and reads back, so the test sees what the client would load.</summary>
    public static AssemblyDefinition RoundTrip(AssemblyDefinition assembly)
    {
        var stream = new MemoryStream();
        assembly.Write(stream);
        stream.Position = 0;
        return AssemblyDefinition.ReadAssembly(stream, new ReaderParameters { AssemblyResolver = Resolver });
    }
}

public sealed class ClientDllFactAttribute : FactAttribute
{
    public ClientDllFactAttribute()
    {
        if (ClientDll.Path == null) Skip = "the RO3 client is not installed (set RO3_CLIENT_DIR)";
    }
}

public class VoiceStubPatchTests
{
    static TypeDefinition Adapter(AssemblyDefinition assembly) => assembly.MainModule.GetType(VoiceStubPatch.AdapterType);

    static MethodDefinition Method(AssemblyDefinition assembly, string name, int parameters) =>
        Adapter(assembly).Methods.Single(m => m.Name == name && m.Parameters.Count == parameters);

    static bool Patched(AssemblyDefinition assembly) => assembly.MainModule.AssemblyReferences.Any(r => r.Name == "Ro3Emu.ClientHooks");

    [ClientDllFact]
    public void InitJoinAndLeaveNoLongerReachGme()
    {
        using var original = ClientDll.Read();
        string[] entries = ["Obf_yyC", "Obf_dYC", "Obf_gYC"]; // Init, JoinChannel, LeaveChannel
        if (!Patched(original))
        {
            // The walk finds the original's way into GME (Init -> context login, Join -> EnterRoom, the auth buffer P/Invoke).
            var before = VoiceStubPatch.NativeCalls(original.MainModule, entries);
            // Init's native context login shows the walk sees GME before the patch. (Join and the auth buffer P/Invoke
            // were asserted here too, but only ever against an already patched DLL; in the original the static walk
            // does not follow their path.)
            Assert.Contains(before, c => c.StartsWith("Obf_yyC ->"));
        }

        var report = VoiceStubPatch.Patch(original.MainModule);
        Assert.StartsWith("voice -> stub", report);
        using var patched = ClientDll.RoundTrip(original);
        Assert.Empty(VoiceStubPatch.NativeCalls(patched.MainModule, entries));
        Assert.Empty(VoiceStubPatch.NativeCalls(patched.MainModule)); // nor any other adapter method or the constructor
    }

    [ClientDllFact]
    public void JoinReportsSuccessThroughTheAdaptersOwnCallback()
    {
        using var assembly = ClientDll.Read();
        VoiceStubPatch.Patch(assembly.MainModule);
        using var patched = ClientDll.RoundTrip(assembly);

        var join = Method(patched, "Obf_dYC", 3).Body.Instructions;
        var call = join.Single(i => i.OpCode == OpCodes.Call && ((MethodReference)i.Operand).Name == "Obf_PYC");
        Assert.Equal("OK.", call.Previous.Operand);
        Assert.Equal(OpCodes.Ldc_I4_0, call.Previous.Previous.OpCode);
        Assert.Equal(OpCodes.Ldc_I4_1, call.Next.OpCode); // JoinChannel returns true
        Assert.Equal(OpCodes.Ret, call.Next.Next.OpCode);

        var joined = Method(patched, "Obf_PYC", 5).Body.Instructions;
        Assert.Contains(joined, i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "OnJoinChannel");
        Assert.Contains(joined, i => i.OpCode == OpCodes.Call && ((MethodReference)i.Operand).Name == "Obf_XyC");

        var leave = Method(patched, "Obf_gYC", 1).Body.Instructions;
        Assert.Contains(leave, i => i.OpCode == OpCodes.Call && ((MethodReference)i.Operand).Name == "Obf_qYC");
        Assert.Contains(Method(patched, "Obf_qYC", 3).Body.Instructions, i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "OnLeaveChannel");

        // The constructor makes no GME context but does make the joined map IsInChannel reads.
        var ctor = Adapter(patched).Methods.Single(m => m.IsConstructor && !m.IsStatic).Body.Instructions;
        Assert.Contains(ctor, i => i.OpCode == OpCodes.Stfld && ((FieldReference)i.Operand).Name == "Obf_zeb");
        Assert.DoesNotContain(ctor, i => i.Operand is MethodReference m && m.Name == "Obf_nnA");
        // The app key never reaches a call any more (GetUserSign returns "").
        Assert.DoesNotContain(Adapter(patched).Methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions),
            i => i.OpCode == OpCodes.Ldstr && ((string)i.Operand).StartsWith("2d1a66f3", StringComparison.Ordinal));
    }

    [ClientDllFact]
    public void PatchingTwiceChangesNothing()
    {
        using var assembly = ClientDll.Read();
        VoiceStubPatch.Patch(assembly.MainModule);
        using var once = ClientDll.RoundTrip(assembly);
        var first = Dump(once);
        Assert.StartsWith("voice -> stub", VoiceStubPatch.Patch(once.MainModule));
        using var twice = ClientDll.RoundTrip(once);
        Assert.Equal(first, Dump(twice));
    }

    static string Dump(AssemblyDefinition assembly) => string.Join("\n", Adapter(assembly).Methods.Where(m => m.HasBody)
        .Select(m => m.FullName + "\n  " + string.Join("\n  ", m.Body.Instructions.Select(i => i.ToString()))));
}
