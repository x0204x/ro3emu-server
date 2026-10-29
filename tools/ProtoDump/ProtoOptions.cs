using System.Globalization;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Type = Google.Protobuf.Reflection.FieldDescriptorProto.Types.Type;

namespace Ro3Emu.ProtoDump;

/// <summary>依 descriptor 宣告讀取 option 的 wire 值；不依名稱推定任何執行規則。</summary>
internal sealed class ProtoOptions
{
    private readonly Dictionary<string, DescriptorProto> _messages = [];
    private readonly Dictionary<string, EnumDescriptorProto> _enums = [];
    private readonly Dictionary<(string Owner, int Number), (string Name, FieldDescriptorProto Field)> _extensions = [];

    public ProtoOptions(FileDescriptorSet set)
    {
        AddFile(DescriptorReflection.Descriptor.ToProto());
        foreach (var file in set.File) AddFile(file);
    }

    private void AddFile(FileDescriptorProto file)
    {
        foreach (var entry in file.EnumType) _enums[Qualified(file.Package, entry.Name)] = entry;
        foreach (var entry in file.MessageType) AddMessage(entry, file.Package);
        AddExtensions(file.Extension, file.Package);
    }

    private void AddMessage(DescriptorProto message, string prefix)
    {
        string name = Qualified(prefix, message.Name);
        _messages[name] = message;
        foreach (var entry in message.EnumType) _enums[Qualified(name, entry.Name)] = entry;
        foreach (var nested in message.NestedType) AddMessage(nested, name);
        AddExtensions(message.Extension, name);
    }

    private void AddExtensions(IEnumerable<FieldDescriptorProto> fields, string prefix)
    {
        foreach (var field in fields)
            _extensions[(Trim(field.Extendee), field.Number)] = (Qualified(prefix, field.Name), field);
    }

    public List<string> Format(IMessage? options) => options == null ? [] : Read(options.ToByteArray(), options.Descriptor.FullName, false);

    private List<string> Read(byte[] bytes, string message, bool aggregate)
    {
        if (!_messages.TryGetValue(Trim(message), out var schema)) throw new InvalidDataException($"Unknown option message schema: {message}");
        var results = new List<string>();
        using var input = new CodedInputStream(bytes);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            int number = WireFormat.GetTagFieldNumber(tag);
            var field = schema.Field.FirstOrDefault(f => f.Number == number);
            string name;
            if (field != null) name = field.Name;
            else if (_extensions.TryGetValue((Trim(message), number), out var extension))
            { field = extension.Field; name = aggregate ? $"[{extension.Name}]" : $"({extension.Name})"; }
            else throw new InvalidDataException($"Unknown option {message} field {number}; refusing to drop it.");
            if (field.Name == "uninterpreted_option")
                throw new InvalidDataException($"Unresolved uninterpreted_option in {message}; supply its descriptor before exporting.");
            var wire = WireFormat.GetTagWireType(tag);
            if (wire == WireFormat.WireType.LengthDelimited && field.Label == FieldDescriptorProto.Types.Label.Repeated
                && field.Type is not (Type.String or Type.Bytes or Type.Message or Type.Group))
            {
                using var packed = new CodedInputStream(input.ReadBytes().ToByteArray());
                while (!packed.IsAtEnd) results.Add(Assignment(name, Scalar(packed, field), aggregate));
            }
            else
            {
                if (wire != WireType(field.Type)) throw new InvalidDataException($"Option wire type mismatch: {message}.{field.Name}");
                string value = field.Type == Type.Message
                    ? "{ " + string.Join(" ", Read(input.ReadBytes().ToByteArray(), field.TypeName, true)) + " }"
                    : Scalar(input, field);
                results.Add(Assignment(name, value, aggregate));
            }
        }
        return results;
    }

    private static string Assignment(string name, string value, bool aggregate) => aggregate ? $"{name}: {value}" : $"{name} = {value}";

    private string Scalar(CodedInputStream input, FieldDescriptorProto field) => field.Type switch
    {
        Type.Double => Floating(input.ReadDouble()),
        Type.Float => Floating(input.ReadFloat()),
        Type.Int64 => input.ReadInt64().ToString(CultureInfo.InvariantCulture),
        Type.Uint64 => input.ReadUInt64().ToString(CultureInfo.InvariantCulture),
        Type.Int32 => input.ReadInt32().ToString(CultureInfo.InvariantCulture),
        Type.Fixed64 => input.ReadFixed64().ToString(CultureInfo.InvariantCulture),
        Type.Fixed32 => input.ReadFixed32().ToString(CultureInfo.InvariantCulture),
        Type.Bool => input.ReadBool() ? "true" : "false",
        Type.String => Quote(input.ReadString()),
        Type.Bytes => QuoteBytes(input.ReadBytes().ToByteArray()),
        Type.Uint32 => input.ReadUInt32().ToString(CultureInfo.InvariantCulture),
        Type.Enum => EnumValue(field.TypeName, input.ReadEnum()),
        Type.Sfixed32 => input.ReadSFixed32().ToString(CultureInfo.InvariantCulture),
        Type.Sfixed64 => input.ReadSFixed64().ToString(CultureInfo.InvariantCulture),
        Type.Sint32 => input.ReadSInt32().ToString(CultureInfo.InvariantCulture),
        Type.Sint64 => input.ReadSInt64().ToString(CultureInfo.InvariantCulture),
        _ => throw new InvalidDataException($"Unsupported option type: {field.Type} ({field.Name})"),
    };

    private string EnumValue(string type, int value) => _enums.TryGetValue(Trim(type), out var schema)
        && schema.Value.FirstOrDefault(v => v.Number == value) is { } entry ? entry.Name
        : throw new InvalidDataException($"Unknown option enum value: {type}={value}");

    private static string Floating(double value) => double.IsNaN(value) ? "nan" : double.IsPositiveInfinity(value) ? "inf"
        : double.IsNegativeInfinity(value) ? "-inf" : value.ToString("R", CultureInfo.InvariantCulture);

    private static WireFormat.WireType WireType(Type type) => type switch
    {
        Type.Double or Type.Fixed64 or Type.Sfixed64 => WireFormat.WireType.Fixed64,
        Type.Float or Type.Fixed32 or Type.Sfixed32 => WireFormat.WireType.Fixed32,
        Type.String or Type.Bytes or Type.Message => WireFormat.WireType.LengthDelimited,
        Type.Group => throw new InvalidDataException("Group options are not supported; refusing to drop them."),
        _ => WireFormat.WireType.Varint,
    };

    public static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        foreach (char c in value)
            result.Append(c switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                < ' ' or '\u007f' => "\\" + Convert.ToString(c, 8).PadLeft(3, '0'),
                _ => c.ToString(),
            });
        return result.Append('"').ToString();
    }

    private static string QuoteBytes(byte[] value) => "\"" + string.Concat(value.Select(b => "\\" + Convert.ToString(b, 8).PadLeft(3, '0'))) + "\"";
    private static string Qualified(string prefix, string name) => prefix.Length == 0 ? name : prefix + "." + name;
    private static string Trim(string name) => name.TrimStart('.');
}
