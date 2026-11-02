using System.Collections;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Builds the client's AUTO_SYNC increments (msg 50xx, romsg.XIncrement) by comparing two versions of a DB object
/// (romsg.X), following the rule every *_Increment.proto is generated from:
/// <list type="bullet">
/// <item>field N of X travels as field N*10; scalars are <c>optional</c> (presence = changed);</item>
/// <item>a message field is a wrapper <c>{all = 1, inc = 2}</c>: <c>all</c> when new, else the nested increment;</item>
/// <item>a map is <c>map&lt;K, {all = 1, inc = 2, del = 3}&gt;</c>: <c>all</c> for a key the client does not have
/// (an <c>inc</c> on a new key is decoded with the wrong type), <c>inc</c> for a changed message
/// value, <c>del</c> for a removed key;</item>
/// <item>a repeated field is <c>{all = RepeatedOrigin{value}, inc = ...}</c>: the whole list when it changed.</item>
/// </list>
/// Fields 1 / 2 of the increment are <c>version</c> / <c>syncAllMark</c>.
/// </summary>
public static class AutoSync
{
    private const int VersionField = 1;
    private const int SyncAllField = 2;

    /// <summary>The increment type of a DB object: romsg.X -> romsg.XIncrement.</summary>
    public static MessageDescriptor IncrementOf(MessageDescriptor descriptor) => Increments.GetOrAdd(descriptor, static d =>
        d.File.FindTypeByName<MessageDescriptor>(d.Name + "Increment")
        ?? FindIncrement(d)
        ?? throw new ArgumentException($"no increment type for {d.FullName}"));

    // Resolved once per type: the lookup concatenated names and walked every proto file on each sync.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<MessageDescriptor, MessageDescriptor> Increments = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<MessageDescriptor, Func<IMessage>> Factories = new();

    private static MessageDescriptor? FindIncrement(MessageDescriptor descriptor)
    {
        // Increments live in the *_Increment.proto next to the base file.
        foreach (var file in descriptor.File.Dependencies.Concat(AllFiles(descriptor.File)))
            if (file.FindTypeByName<MessageDescriptor>(descriptor.Name + "Increment") is { } found) return found;
        return null;
    }

    private static readonly Lazy<IReadOnlyList<FileDescriptor>> Files = new(() =>
        [.. typeof(Romsg.RoleBaseInfo).Assembly.GetTypes()
            .Where(t => t.Name.EndsWith("Reflection", StringComparison.Ordinal) && t.GetProperty("Descriptor")?.GetValue(null) is FileDescriptor)
            .Select(t => (FileDescriptor)t.GetProperty("Descriptor")!.GetValue(null)!)]);

    private static IEnumerable<FileDescriptor> AllFiles(FileDescriptor _) => Files.Value;

    /// <summary>The first message for a dataset: everything, with syncAllMark.</summary>
    public static IMessage Full(IMessage now, long version)
    {
        var inc = Diff(null, now, IncrementOf(now.Descriptor), full: true);
        SetHeader(inc, version, syncAll: true);
        return inc;
    }

    /// <summary>What changed from <paramref name="before"/> to <paramref name="now"/>; null when nothing did.</summary>
    public static IMessage? Changes(IMessage before, IMessage now, long version)
    {
        var inc = Diff(before, now, IncrementOf(now.Descriptor), full: false);
        if (IsEmpty(inc)) return null;
        SetHeader(inc, version, syncAll: false);
        return inc;
    }

    private static void SetHeader(IMessage inc, long version, bool syncAll)
    {
        var fields = inc.Descriptor.Fields;
        if (fields[VersionField] is { } v) v.Accessor.SetValue(inc, version);
        if (syncAll && fields[SyncAllField] is { } s) s.Accessor.SetValue(inc, true);
    }

    private static bool IsEmpty(IMessage inc) =>
        inc.Descriptor.Fields.InFieldNumberOrder().All(f => f.FieldNumber is VersionField or SyncAllField || !Has(inc, f));

    private static bool Has(IMessage message, FieldDescriptor f) =>
        f.IsMap || f.IsRepeated ? ((ICollection)f.Accessor.GetValue(message)).Count > 0
        : f.HasPresence ? f.Accessor.HasValue(message)
        : !IsDefault(f.Accessor.GetValue(message));

    /// <summary>Increment of <paramref name="before"/> -> <paramref name="now"/> as a message of type <paramref name="incType"/>.</summary>
    private static IMessage Diff(IMessage? before, IMessage now, MessageDescriptor incType, bool full)
    {
        var inc = NewMessage(incType);
        foreach (var field in now.Descriptor.Fields.InFieldNumberOrder())
        {
            var incField = incType.FindFieldByNumber(field.FieldNumber * 10);
            if (incField == null) continue; // not synced to the client
            object value = field.Accessor.GetValue(now);
            object? old = before == null ? null : field.Accessor.GetValue(before);

            if (field.IsMap)
            {
                DiffMap(field, (IDictionary)value, (IDictionary?)old, incField, inc);
            }
            else if (field.IsRepeated)
            {
                var list = (IList)value;
                if (old != null && SameList(list, (IList)old)) continue;
                if (old == null && list.Count == 0) continue;
                var wrapper = NewMessage(incField.MessageType);
                // A list of messages the client already has with the same length: element-wise increments
                // (ParseRepeatedTypeIncrement merges by position, so change handlers on [field, i] fire).
                var incList = wrapper.Descriptor.FindFieldByName("inc");
                if (old is IList oldList && oldList.Count == list.Count && field.FieldType == FieldType.Message
                    && incList?.FieldType == FieldType.Message && incList.MessageType.FindFieldByName("value") is { } incValues
                    && incValues.FieldType == FieldType.Message)
                {
                    var holder = NewMessage(incList.MessageType);
                    var items = (IList)incValues.Accessor.GetValue(holder);
                    for (int i = 0; i < list.Count; i++)
                        items.Add(Diff((IMessage)oldList[i]!, (IMessage)list[i]!, incValues.MessageType, full: false));
                    incList.Accessor.SetValue(wrapper, holder);
                    incField.Accessor.SetValue(inc, wrapper);
                    continue;
                }
                var allField = wrapper.Descriptor.FindFieldByName("all");
                var origin = NewMessage(allField.MessageType);
                var target = (IList)origin.Descriptor.FindFieldByName("value").Accessor.GetValue(origin);
                foreach (var item in list) target.Add(item);
                allField.Accessor.SetValue(wrapper, origin);
                incField.Accessor.SetValue(inc, wrapper);
            }
            else if (field.FieldType == FieldType.Message)
            {
                bool present = field.Accessor.HasValue(now);
                bool wasPresent = before != null && field.Accessor.HasValue(before);
                if (!present || (wasPresent && Equals(value, old))) continue;
                var wrapper = NewMessage(incField.MessageType);
                SetAllOrInc(wrapper, wasPresent ? (IMessage)old! : null, (IMessage)value);
                incField.Accessor.SetValue(inc, wrapper);
            }
            else
            {
                if (before != null && Equals(value, old)) continue;
                if (before == null && !full && IsDefault(value)) continue;
                incField.Accessor.SetValue(inc, value);
            }
        }
        return inc;
    }

    private static void DiffMap(FieldDescriptor field, IDictionary now, IDictionary? old, FieldDescriptor incField, IMessage inc)
    {
        var incMap = (IDictionary)incField.Accessor.GetValue(inc);
        var wrapperType = incField.MessageType.FindFieldByNumber(2).MessageType; // map entry value = the {all, inc, del} wrapper
        foreach (DictionaryEntry e in now)
        {
            object? previous = old != null && old.Contains(e.Key) ? old[e.Key] : null;
            if (previous != null && Equals(previous, e.Value)) continue;
            var wrapper = NewMessage(wrapperType);
            if (e.Value is IMessage message) SetAllOrInc(wrapper, previous as IMessage, message);
            else wrapper.Descriptor.FindFieldByName("all").Accessor.SetValue(wrapper, e.Value!);
            incMap[e.Key] = wrapper;
        }
        if (old == null) return;
        foreach (DictionaryEntry e in old)
        {
            if (now.Contains(e.Key)) continue;
            var wrapper = NewMessage(wrapperType);
            wrapper.Descriptor.FindFieldByName("del").Accessor.SetValue(wrapper, true);
            incMap[e.Key] = wrapper;
        }
    }

    /// <summary>A wrapper {all, inc}: the whole value when the client has none, else its own increment.</summary>
    private static void SetAllOrInc(IMessage wrapper, IMessage? previous, IMessage now)
    {
        var incField = wrapper.Descriptor.FindFieldByName("inc");
        if (previous == null || incField == null || incField.FieldType != FieldType.Message)
        {
            wrapper.Descriptor.FindFieldByName("all").Accessor.SetValue(wrapper, now);
            return;
        }
        incField.Accessor.SetValue(wrapper, Diff(previous, now, incField.MessageType, full: false));
    }

    /// <summary>An empty message of the type (was a parse of an empty buffer: one CodedInputStream per message).</summary>
    private static IMessage NewMessage(MessageDescriptor type) => Factories.GetOrAdd(type, static t =>
        System.Linq.Expressions.Expression.Lambda<Func<IMessage>>(System.Linq.Expressions.Expression.New(t.ClrType)).Compile())();

    private static bool SameList(IList a, IList b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!Equals(a[i], b[i])) return false;
        return true;
    }

    private static bool IsDefault(object? value) => value switch
    {
        null => true,
        string s => s.Length == 0,
        ByteString b => b.IsEmpty,
        bool b => !b,
        Enum e => Convert.ToInt64(e) == 0,
        IConvertible c => Convert.ToDouble(c) == 0,
        _ => false,
    };
}
