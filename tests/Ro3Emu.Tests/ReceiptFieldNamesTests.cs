using System.Collections;
using System.Reflection;
using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Tests;

/// <summary>
/// The names that receipts store for a plan. EventPlanFingerprint hashes every instance field of a plan and of the
/// server values it holds as "declaring type/field", and EconomyChildIntent stores the plan's fields under the same
/// keys, with the values' public properties as JSON. A renamed or moved field or property therefore makes an operation
/// recorded before the change refuse its replay or recovery. The expected list is Snapshots/receipt-field-names.txt:
/// regenerate it with RO3EMU_UPDATE_SNAPSHOTS=1 only for a change that is meant to refuse older receipts.
/// </summary>
public sealed class ReceiptFieldNamesTests
{
    private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.DeclaredOnly;

    [Fact]
    public void NamesWrittenIntoReceiptsAreUnchanged()
    {
        var actual = Names();
        string file = Path.Combine(ClientData.RepoRoot, "tests", "Ro3Emu.Tests", "Snapshots",
            "receipt-field-names.txt");
        if (Environment.GetEnvironmentVariable("RO3EMU_UPDATE_SNAPSHOTS") == "1")
            File.WriteAllText(file, string.Join("\n", actual) + "\n");
        var expected = File.ReadAllText(file).Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var removed = expected.Except(actual).Take(20).Select(n => "gone " + n);
        var added = actual.Except(expected).Take(20).Select(n => "new  " + n);
        var changes = removed.Concat(added).ToList();
        Assert.True(changes.Count == 0, "names written into receipts changed (older receipts would no longer "
            + "match):\n" + string.Join("\n", changes));
    }

    /// <summary>Every DbPlan, then the server types its fields can hold, with their subclasses and implementations.
    /// Collections are written item by item, so only their element types count.</summary>
    private static List<string> Names()
    {
        var types = typeof(DbPlan).Assembly.GetTypes();
        var reached = new HashSet<Type>();
        var queue = new Queue<Type>();
        foreach (var plan in types.Where(t => t.IsSubclassOf(typeof(DbPlan)))) Reach(plan);
        while (queue.TryDequeue(out var type))
        {
            foreach (var derived in types.Where(t => Derives(t, type))) Reach(derived);
            foreach (var field in Fields(type)) Reach(field.FieldType);
            foreach (var property in Properties(type)) Reach(property.PropertyType);
            foreach (var collection in type.GetInterfaces().Where(i => i.IsGenericType)) Reach(collection);
        }
        return
        [
            .. reached.Where(t => !typeof(IEnumerable).IsAssignableFrom(t))
                .SelectMany(t => Fields(t).Select(f => Definition(f.DeclaringType)!.FullName + "/" + f.Name)
                    .Concat(Properties(t).Select(p => t.FullName + "#" + p.Name)))
                .Distinct().Order(StringComparer.Ordinal),
        ];

        void Reach(Type type)
        {
            if (type.IsArray) { Reach(type.GetElementType()!); return; }
            if (type.IsGenericType)
            {
                foreach (var argument in type.GetGenericArguments()) Reach(argument);
                type = type.GetGenericTypeDefinition();
            }
            if (type.Assembly != typeof(DbPlan).Assembly || type.IsEnum || type.IsGenericParameter) return;
            if (type.IsSubclassOf(typeof(Delegate)) || type.Name.Contains('<')) return;
            if (reached.Add(type)) queue.Enqueue(type);
        }
    }

    private static bool Derives(Type candidate, Type type) => candidate != type &&
        (Definition(candidate.BaseType) == type || candidate.GetInterfaces().Any(i => Definition(i) == type));

    private static Type? Definition(Type? type) =>
        type is { IsGenericType: true } ? type.GetGenericTypeDefinition() : type;

    /// <summary>The instance fields EventPlanFingerprint walks: the type's own and its bases' up to DbPlan.</summary>
    private static IEnumerable<FieldInfo> Fields(Type type)
    {
        for (var current = type; current != null && current != typeof(DbPlan) && current != typeof(object);
             current = current.BaseType)
            foreach (var field in current.GetFields(Declared)) yield return field;
    }

    /// <summary>The public properties System.Text.Json writes for a value EconomyChildIntent serializes.</summary>
    private static IEnumerable<PropertyInfo> Properties(Type type) =>
        type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.GetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0);
}
