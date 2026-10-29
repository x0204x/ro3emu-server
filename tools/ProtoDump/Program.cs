// 將 descriptor 還原為 .proto；只轉換資料，不載入或執行 client。
using Google.Protobuf.Reflection;
using Ro3Emu.ProtoDump;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: ProtoDump <proto.pb.bytes> <outDir>");
    return 1;
}

// Input is either a serialized FileDescriptorSet, or a directory of decompiled protoc-generated
// *Reflection.cs files whose embedded base64 descriptors are collected.
FileDescriptorSet set;
if (Directory.Exists(args[0]))
{
    set = new FileDescriptorSet();
    var call = new System.Text.RegularExpressions.Regex(@"FromBase64String\(((?:""[A-Za-z0-9+/=]*""\s*\+?\s*)+)\)");
    var piece = new System.Text.RegularExpressions.Regex(@"""([A-Za-z0-9+/=]*)""");
    foreach (var cs in Directory.EnumerateFiles(args[0], "*Reflection.cs", SearchOption.AllDirectories))
        foreach (System.Text.RegularExpressions.Match m in call.Matches(File.ReadAllText(cs)))
        {
            var b64 = string.Concat(piece.Matches(m.Groups[1].Value).Select(p => p.Groups[1].Value));
            set.File.Add(FileDescriptorProto.Parser.ParseFrom(Convert.FromBase64String(b64)));
        }
}
else
{
    set = FileDescriptorSet.Parser.ParseFrom(File.ReadAllBytes(args[0]));
}
// 先完成全部轉換；遇到無法表示的 option 時，不留下部分輸出。
var outputs = new DescriptorWriter(set).Render();
foreach (var (name, text) in outputs)
{
    var root = Path.GetFullPath(args[1]) + Path.DirectorySeparatorChar;
    var path = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
    if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Unsafe descriptor path: {name}");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, text);
}
Console.WriteLine($"{set.File.Count} files, {set.File.Sum(f => f.MessageType.Count)} top-level messages, " +
    $"{set.File.Sum(f => f.EnumType.Count)} top-level enums, {set.File.Sum(f => f.Service.Count)} services");
return 0;
