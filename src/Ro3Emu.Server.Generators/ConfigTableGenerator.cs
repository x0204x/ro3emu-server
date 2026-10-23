using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ro3Emu.Server.Generators;

/// <summary>
/// Implements the typed views of the client's config tables. For each <c>[ConfigTable("Name")] partial record struct
/// X(JsonElement Row)</c> it implements every <c>[ConfigField("_iField")] partial</c> property with the reader its type
/// selects (ConfigValue), and adds <c>TableName</c>, <c>Info</c> (the declared fields), <c>Get</c> and <c>All</c>. It
/// lists every view in <c>ConfigTables.All</c>, which the server checks against the game data at startup.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ConfigTableGenerator : IIncrementalGenerator
{
    private const string Namespace = "Ro3Emu.Server.Data.Config";
    private const string TableAttribute = Namespace + ".ConfigTableAttribute";
    private const string FieldAttribute = Namespace + ".ConfigFieldAttribute";
    private const string Data = "global::Ro3Emu.Server.Data.GameData";
    private const string Linq = "global::System.Linq.Enumerable";
    private const string Collections = "global::System.Collections.Generic";
    private const string Array = "global::System.Text.Json.JsonValueKind.Array";

    private static readonly DiagnosticDescriptor NotAView = new("RO3CFG001", "Not a config view",
        "[ConfigTable] '{0}' must be a partial record struct whose primary constructor is (JsonElement Row)",
        "ConfigTables", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedType = new("RO3CFG002", "Unsupported config field type",
        "[ConfigField] '{0}' has type '{1}'; use long, int, double, bool, string, long[], double[], long[][], "
        + "double[][], string[], (long Id, double Value)[] or JsonElement?", "ConfigTables", DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NotPartial = new("RO3CFG003", "Config field is not a partial property",
        "[ConfigField] '{0}' must be a partial property with a get accessor only", "ConfigTables",
        DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateField = new("RO3CFG004", "Duplicate config field",
        "Field '{0}' is declared twice in '{1}'", "ConfigTables", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly Dictionary<string, (string Kind, string Reader)> Kinds = new()
    {
        ["long"] = ("Integral", "Integral"),
        ["int"] = ("Integral32", "Integral32"),
        ["double"] = ("Number", "Number"),
        ["bool"] = ("Bool", "Bool"),
        ["string"] = ("Text", "Text"),
        ["long[]"] = ("Ints", "Ints"),
        ["double[]"] = ("Numbers", "Numbers"),
        ["long[][]"] = ("IntRows", "IntRows"),
        ["double[][]"] = ("NumberRows", "NumberRows"),
        ["string[]"] = ("Texts", "Texts"),
        ["(long Id, double Value)[]"] = ("Pairs", "Pairs"),
        ["System.Text.Json.JsonElement?"] = ("Raw", "Raw"),
    };

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var views = context.SyntaxProvider.ForAttributeWithMetadataName(TableAttribute,
            static (node, _) => node is RecordDeclarationSyntax,
            static (ctx, _) => Describe(ctx));
        context.RegisterSourceOutput(views, static (spc, view) => Emit(spc, view));
        context.RegisterSourceOutput(views.Collect(), static (spc, all) => EmitRegistry(spc, all));
    }

    private static View Describe(GeneratorAttributeSyntaxContext ctx)
    {
        var type = (INamedTypeSymbol)ctx.TargetSymbol;
        var attribute = ctx.Attributes[0];
        string table = FirstArgument(attribute);
        bool optional = Named(attribute, "Optional"), grouped = Named(attribute, "Grouped");
        var diagnostics = new List<Diag>();
        var record = (RecordDeclarationSyntax)ctx.TargetNode;
        bool shaped = type.IsRecord && type.TypeKind == TypeKind.Struct && type.ContainingType == null
            && record.Modifiers.Any(SyntaxKind.PartialKeyword)
            && record.ParameterList?.Parameters.Count == 1
            && record.ParameterList.Parameters[0].Identifier.Text == "Row"
            && type.GetMembers("Row").OfType<IPropertySymbol>().FirstOrDefault()?.Type.ToDisplayString()
                == "System.Text.Json.JsonElement";
        if (!shaped) diagnostics.Add(new Diag(NotAView, record.Identifier.GetLocation(), type.Name, ""));

        var fields = new List<Field>();
        var seen = new HashSet<string>();
        foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
        {
            var fieldAttribute = property.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == FieldAttribute);
            if (fieldAttribute == null) continue;
            var location = property.Locations.FirstOrDefault() ?? Location.None;
            string name = FirstArgument(fieldAttribute);
            string typeName = property.Type.ToDisplayString();
            if (!property.IsPartialDefinition || property.SetMethod != null || property.GetMethod == null)
                diagnostics.Add(new Diag(NotPartial, location, property.Name, ""));
            else if (!Kinds.TryGetValue(typeName, out var kind))
                diagnostics.Add(new Diag(UnsupportedType, location, property.Name, typeName));
            else if (!seen.Add(name))
                diagnostics.Add(new Diag(DuplicateField, location, name, type.Name));
            else
                fields.Add(new Field(property.Name, name, typeName, kind.Kind, kind.Reader,
                    Named(fieldAttribute, "Optional"), SyntaxFacts.GetText(property.DeclaredAccessibility)));
        }
        string ns = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString();
        return new View(ns, type.Name, type.IsReadOnly, table, optional, grouped,
            new EquatableArray<Field>([.. fields]), new EquatableArray<Diag>([.. diagnostics]));
    }

    private static string FirstArgument(AttributeData attribute) =>
        attribute.ConstructorArguments.Length > 0 ? attribute.ConstructorArguments[0].Value as string ?? "" : "";

    private static bool Named(AttributeData attribute, string name) =>
        attribute.NamedArguments.FirstOrDefault(a => a.Key == name).Value.Value is true;

    private static void Emit(SourceProductionContext spc, View view)
    {
        foreach (var d in view.Diagnostics)
            spc.ReportDiagnostic(Diagnostic.Create(d.Descriptor, d.Location, d.First, d.Second));
        if (view.Diagnostics.Count > 0) return;
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/> by Ro3Emu.Server.Generators.ConfigTableGenerator");
        sb.AppendLine("#nullable enable");
        if (view.Namespace.Length > 0) sb.Append("namespace ").Append(view.Namespace).AppendLine(";");
        sb.AppendLine();
        sb.Append(view.ReadOnly ? "readonly " : "").Append("partial record struct ").AppendLine(view.Name);
        sb.AppendLine("{");
        sb.Append("    public const string TableName = \"").Append(view.Table).AppendLine("\";");
        sb.AppendLine();
        sb.Append("    public static global::").Append(Namespace)
            .Append(".ConfigTableInfo Info { get; } = new(TableName, ").Append(Literal(view.Optional)).Append(", ")
            .Append(Literal(view.Grouped)).AppendLine(",");
        sb.AppendLine("    [");
        foreach (var f in view.Fields)
            sb.Append("        new(\"").Append(f.Name).Append("\", global::").Append(Namespace).Append(".ConfigKind.")
                .Append(f.Kind).Append(", ").Append(Literal(f.Optional)).AppendLine("),");
        sb.AppendLine("    ]);");
        sb.AppendLine();
        if (view.Grouped) EmitGroupedAccess(sb, view.Name);
        else EmitAccess(sb, view.Name);
        foreach (var f in view.Fields)
        {
            sb.AppendLine();
            sb.Append("    ").Append(f.Accessibility).Append(" partial ").Append(Qualified(f.Type)).Append(' ')
                .Append(f.Property).Append(" => global::").Append(Namespace).Append(".ConfigValue.").Append(f.Reader)
                .Append("(Row, \"").Append(f.Name).AppendLine("\");");
        }
        sb.AppendLine("}");
        spc.AddSource(Hint(view) + ".g.cs", sb.ToString());
    }

    private static void EmitAccess(StringBuilder sb, string name)
    {
        sb.Append("    public static ").Append(name).Append("? Get(").Append(Data).AppendLine(" data, long id) =>");
        sb.Append("        data.Row(TableName, id) is { } row ? new ").Append(name).AppendLine("(row) : null;");
        sb.AppendLine();
        sb.Append("    public static ").Append(Collections).Append(".IEnumerable<").Append(name).Append("> All(")
            .Append(Data).AppendLine(" data) =>");
        sb.Append("        ").Append(Linq).Append(".Select(data.Table(TableName).Values, row => new ").Append(name)
            .AppendLine("(row));");
    }

    private static void EmitGroupedAccess(StringBuilder sb, string name)
    {
        sb.Append("    public static ").Append(Collections).Append(".IReadOnlyList<").Append(name).Append("> Get(")
            .Append(Data).AppendLine(" data, long id) =>");
        sb.Append("        data.Row(TableName, id) is { } group && global::Ro3Emu.Server.Data.GameData.TryGroupedRows(group, out var rows)")
            .AppendLine();
        sb.Append("            ? [.. ").Append(Linq).Append(".Select(rows, row => new ").Append(name)
            .AppendLine("(row))] : [];");
        sb.AppendLine();
        sb.Append("    public static ").Append(Collections).Append(".IEnumerable<").Append(name).Append("> All(")
            .Append(Data).AppendLine(" data) =>");
        sb.Append("        ").Append(Linq).AppendLine(".SelectMany(data.Table(TableName).Values, group =>");
        sb.Append("            global::Ro3Emu.Server.Data.GameData.TryGroupedRows(group, out var rows)").AppendLine();
        sb.Append("                ? ").Append(Linq).Append(".Select(rows, row => new ").Append(name)
            .AppendLine("(row))");
        sb.Append("                : ").Append(Linq).Append(".Empty<").Append(name).AppendLine(">());");
    }

    private static void EmitRegistry(SourceProductionContext spc, ImmutableArray<View> views)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/> by Ro3Emu.Server.Generators.ConfigTableGenerator");
        sb.AppendLine("#nullable enable");
        sb.Append("namespace ").Append(Namespace).AppendLine(";");
        sb.AppendLine();
        sb.AppendLine("/// <summary>Every declared config table view, checked against the game data at startup.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public static class ConfigTables");
        sb.AppendLine("{");
        sb.Append("    public static ").Append(Collections)
            .AppendLine(".IReadOnlyList<ConfigTableInfo> All { get; } =");
        sb.AppendLine("    [");
        foreach (var view in views.Where(v => v.Diagnostics.Count == 0).OrderBy(v => v.Table, StringComparer.Ordinal))
            sb.Append("        global::").Append(Hint(view)).AppendLine(".Info,");
        sb.AppendLine("    ];");
        sb.AppendLine("}");
        spc.AddSource("ConfigTables.g.cs", sb.ToString());
    }

    private static string Literal(bool value) => value ? "true" : "false";

    private static string Hint(View view) => (view.Namespace.Length > 0 ? view.Namespace + "." : "") + view.Name;

    private static string Qualified(string type) =>
        type == "System.Text.Json.JsonElement?" ? "global::System.Text.Json.JsonElement?" : type;

    private sealed record View(string Namespace, string Name, bool ReadOnly, string Table, bool Optional, bool Grouped,
        EquatableArray<Field> Fields, EquatableArray<Diag> Diagnostics);

    private sealed record Field(string Property, string Name, string Type, string Kind, string Reader, bool Optional,
        string Accessibility);

    private sealed record Diag(DiagnosticDescriptor Descriptor, Location Location, string First, string Second);
}
