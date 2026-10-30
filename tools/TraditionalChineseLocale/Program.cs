using System.Text.Json;
using Ro3Emu.DataExport;

if (args.Length < 4)
    throw new ArgumentException("用法：TraditionalChineseLocale <zh_CN.json> <en.json> <zh_TW.json> <輸出資料夾> [覆寫.tsv ...]");
string[] overrides = args.Skip(4).Select(File.ReadAllText).ToArray();
var result = TraditionalChineseMapBuilder.Build(args[0], args[1], args[2], args[3], overrides);
File.WriteAllText(Path.Combine(args[3], "coverage.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(new { result.SourceIds, result.TranslatedIds, result.TextEntries,
    result.AmbiguousTexts, MissingNative = result.MissingTraditionalIds.Length,
    Unresolved = result.UnresolvedIds.Length, PlaceholderMismatches = result.PlaceholderMismatches.Length }));
