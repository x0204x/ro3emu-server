using System.Text;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Label = Google.Protobuf.Reflection.FieldDescriptorProto.Types.Label;
using Type = Google.Protobuf.Reflection.FieldDescriptorProto.Types.Type;

namespace Ro3Emu.ProtoDump;

internal sealed class DescriptorWriter(FileDescriptorSet set)
{
    private readonly ProtoOptions _options = new(set);

    public Dictionary<string, string> Render()
    {
        var outputs = new Dictionary<string, string>();
        foreach (var file in set.File) outputs.Add(file.Name, FileText(file));
        return outputs;
    }

    private string FileText(FileDescriptorProto file)
    {
        string syntax = string.IsNullOrEmpty(file.Syntax) ? "proto2" : file.Syntax;
        if (syntax is not ("proto2" or "proto3")) throw new InvalidDataException($"Unsupported syntax: {syntax}");
        var sb = new StringBuilder();
        sb.AppendLine($"syntax = {ProtoOptions.Quote(syntax)};");
        if (file.HasPackage) sb.AppendLine($"package {file.Package};");
        for (int i = 0; i < file.Dependency.Count; i++)
        {
            string modifier = file.PublicDependency.Contains(i) ? "public " : file.WeakDependency.Contains(i) ? "weak " : "";
            sb.AppendLine($"import {modifier}{ProtoOptions.Quote(file.Dependency[i])};");
        }
        WriteOptions(sb, file.Options, 0);
        sb.AppendLine();
        foreach (var entry in file.EnumType) WriteEnum(sb, entry, 0);
        foreach (var entry in file.MessageType) WriteMessage(sb, entry, 0, syntax == "proto3");
        WriteExtensions(sb, file.Extension, 0, syntax == "proto3");
        foreach (var service in file.Service) WriteService(sb, service);
        return sb.ToString();
    }

    private void WriteOptions(StringBuilder sb, IMessage? options, int depth)
    {
        foreach (string value in _options.Format(options)) sb.AppendLine($"{Pad(depth)}option {value};");
    }

    private string Suffix(IMessage? options, IEnumerable<string>? additional = null)
    {
        var values = _options.Format(options);
        if (additional != null) values.InsertRange(0, additional);
        return values.Count == 0 ? "" : $" [{string.Join(", ", values)}]";
    }

    private void WriteEnum(StringBuilder sb, EnumDescriptorProto entry, int depth)
    {
        string pad = Pad(depth);
        sb.AppendLine($"{pad}enum {entry.Name} {{");
        WriteOptions(sb, entry.Options, depth + 1);
        foreach (var value in entry.Value) sb.AppendLine($"{pad}  {value.Name} = {value.Number}{Suffix(value.Options)};");
        foreach (var range in entry.ReservedRange) sb.AppendLine($"{pad}  reserved {range.Start} to {range.End};");
        WriteReservedNames(sb, entry.ReservedName, depth + 1);
        sb.AppendLine($"{pad}}}");
        sb.AppendLine();
    }

    private void WriteMessage(StringBuilder sb, DescriptorProto message, int depth, bool proto3)
    {
        string pad = Pad(depth);
        sb.AppendLine($"{pad}message {message.Name} {{");
        WriteOptions(sb, message.Options, depth + 1);
        var maps = message.NestedType.Where(n => n.Options?.MapEntry == true).ToDictionary(n => n.Name);
        foreach (var map in maps.Values) ValidateMap(map);
        foreach (var field in message.Field.Where(f => f.Proto3Optional && f.HasOneofIndex))
            if (message.OneofDecl[field.OneofIndex].Options is { } options && options.CalculateSize() != 0)
                throw new InvalidDataException($"Synthetic oneof options cannot be represented: {message.Name}.{field.Name}");
        foreach (var entry in message.EnumType) WriteEnum(sb, entry, depth + 1);
        foreach (var nested in message.NestedType.Where(n => n.Options?.MapEntry != true)) WriteMessage(sb, nested, depth + 1, proto3);
        // 保留真實 oneof 的選項及空宣告；proto3 optional 的 synthetic oneof 由編譯器重建。
        var written = new HashSet<int>();
        foreach (var field in message.Field)
        {
            if (field.HasOneofIndex && !field.Proto3Optional)
            {
                if (!written.Add(field.OneofIndex)) continue;
                WriteOneof(sb, message, field.OneofIndex, depth + 1, maps, proto3);
            }
            else WriteField(sb, field, depth + 1, maps, proto3, false);
        }
        for (int i = 0; i < message.OneofDecl.Count; i++)
            if (!written.Contains(i) && !message.Field.Any(f => f.HasOneofIndex && f.OneofIndex == i && f.Proto3Optional))
                WriteOneof(sb, message, i, depth + 1, maps, proto3);
        foreach (var range in message.ExtensionRange)
            sb.AppendLine($"{pad}  extensions {range.Start} to {End(range.End)}{Suffix(range.Options)};");
        WriteExtensions(sb, message.Extension, depth + 1, proto3);
        foreach (var range in message.ReservedRange) sb.AppendLine($"{pad}  reserved {range.Start} to {End(range.End)};");
        WriteReservedNames(sb, message.ReservedName, depth + 1);
        sb.AppendLine($"{pad}}}");
        sb.AppendLine();
    }

    private void WriteOneof(StringBuilder sb, DescriptorProto message, int index, int depth,
        Dictionary<string, DescriptorProto> maps, bool proto3)
    {
        var oneof = message.OneofDecl[index];
        sb.AppendLine($"{Pad(depth)}oneof {oneof.Name} {{");
        WriteOptions(sb, oneof.Options, depth + 1);
        foreach (var field in message.Field.Where(f => f.HasOneofIndex && f.OneofIndex == index))
            WriteField(sb, field, depth + 1, maps, proto3, true);
        sb.AppendLine($"{Pad(depth)}}}");
    }

    private void WriteExtensions(StringBuilder sb, IEnumerable<FieldDescriptorProto> fields, int depth, bool proto3)
    {
        foreach (var group in fields.GroupBy(f => f.Extendee))
        {
            sb.AppendLine($"{Pad(depth)}extend {Trim(group.Key)} {{");
            foreach (var field in group) WriteField(sb, field, depth + 1, [], proto3, false);
            sb.AppendLine($"{Pad(depth)}}}");
            sb.AppendLine();
        }
    }

    private void WriteField(StringBuilder sb, FieldDescriptorProto field, int depth,
        Dictionary<string, DescriptorProto> maps, bool proto3, bool inOneof)
    {
        if (field.Type == Type.Group) throw new InvalidDataException($"Group field is unsupported: {field.Name}");
        string label = inOneof ? "" : field.Label switch
        {
            Label.Repeated when IsMap(field, maps) => "",
            Label.Repeated => "repeated ",
            Label.Required => "required ",
            _ when field.Proto3Optional || !proto3 => "optional ",
            _ => "",
        };
        var extra = new List<string>();
        if (field.HasDefaultValue) extra.Add($"default = {Default(field)}");
        if (field.HasJsonName && field.JsonName != JsonName(field.Name)) extra.Add($"json_name = {ProtoOptions.Quote(field.JsonName)}");
        sb.AppendLine($"{Pad(depth)}{label}{FieldType(field, maps)} {field.Name} = {field.Number}{Suffix(field.Options, extra)};");
    }

    private void WriteService(StringBuilder sb, ServiceDescriptorProto service)
    {
        sb.AppendLine($"service {service.Name} {{");
        WriteOptions(sb, service.Options, 1);
        foreach (var method in service.Method)
        {
            string signature = $"  rpc {method.Name} ({(method.ClientStreaming ? "stream " : "")}{Trim(method.InputType)}) " +
                $"returns ({(method.ServerStreaming ? "stream " : "")}{Trim(method.OutputType)})";
            if (_options.Format(method.Options).Count == 0) sb.AppendLine(signature + ";");
            else { sb.AppendLine(signature + " {"); WriteOptions(sb, method.Options, 2); sb.AppendLine("  }"); }
        }
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private void ValidateMap(DescriptorProto map)
    {
        var onlyMapEntry = new MessageOptions { MapEntry = true };
        if (!map.Options.Equals(onlyMapEntry) || map.Field.Any(f => f.Options is { } options && options.CalculateSize() != 0)
            || map.ReservedName.Count != 0 || map.ReservedRange.Count != 0)
            throw new InvalidDataException($"Map entry metadata cannot be represented by map syntax: {map.Name}");
    }

    private static string Default(FieldDescriptorProto field) => field.Type switch
    {
        Type.String => ProtoOptions.Quote(field.DefaultValue),
        // descriptor 的 bytes default 本來就使用 C escape，不再重複跳脫。
        Type.Bytes => "\"" + field.DefaultValue + "\"",
        _ => field.DefaultValue,
    };

    private static void WriteReservedNames(StringBuilder sb, IEnumerable<string> names, int depth)
    {
        foreach (string name in names) sb.AppendLine($"{Pad(depth)}reserved {ProtoOptions.Quote(name)};");
    }

    private static string JsonName(string name)
    {
        var value = new StringBuilder();
        bool upper = false;
        foreach (char c in name)
            if (c == '_') upper = true;
            else { value.Append(upper ? char.ToUpperInvariant(c) : c); upper = false; }
        return value.ToString();
    }

    private static bool IsMap(FieldDescriptorProto field, Dictionary<string, DescriptorProto> maps) =>
        field.Type == Type.Message && maps.ContainsKey(Trim(field.TypeName).Split('.').Last());

    private static string FieldType(FieldDescriptorProto field, Dictionary<string, DescriptorProto> maps)
    {
        if (IsMap(field, maps))
        {
            var map = maps[Trim(field.TypeName).Split('.').Last()];
            return $"map<{FieldType(map.Field[0], [])}, {FieldType(map.Field[1], [])}>";
        }
        return field.Type switch
        {
            Type.Message or Type.Enum => Trim(field.TypeName),
            Type.Double => "double",
            Type.Float => "float",
            Type.Int64 => "int64",
            Type.Uint64 => "uint64",
            Type.Int32 => "int32",
            Type.Uint32 => "uint32",
            Type.Fixed64 => "fixed64",
            Type.Fixed32 => "fixed32",
            Type.Sfixed32 => "sfixed32",
            Type.Sfixed64 => "sfixed64",
            Type.Sint32 => "sint32",
            Type.Sint64 => "sint64",
            Type.Bool => "bool",
            Type.String => "string",
            Type.Bytes => "bytes",
            _ => throw new InvalidDataException($"Unsupported field type: {field.Type}"),
        };
    }

    private static string Pad(int depth) => new(' ', depth * 2);
    private static string Trim(string name) => name.TrimStart('.');
    private static string End(int exclusive) => exclusive == 536870912 ? "max" : (exclusive - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
