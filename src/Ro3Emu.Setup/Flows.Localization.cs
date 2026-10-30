using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ro3Emu.ClientTools;
using Ro3Emu.DataExport;

namespace Ro3Emu.Setup;

internal static partial class Flows
{
    // 玩家顯示快取與 curated server data 分開，重建顯示檔不會使匯入資料標記失效。
    private static string PlayerLocalizationDir => Path.Combine(SetupHome.Root, "localization");
    private static readonly string[] LocalizationNames =
        ["loc_en.tsv", TraditionalChineseMapBuilder.TextFile, TraditionalChineseMapBuilder.SidFile];
    private sealed record LocalizationCache(int Format, string Inputs, Dictionary<string, string> Outputs);

    internal static IReadOnlyList<string> TraditionalChineseOverrides()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("localization/zh_tw_overrides.tsv")
            ?? throw new InvalidOperationException("缺少程式自有的繁中覆寫資源。");
        using var reader = new StreamReader(stream);
        return [reader.ReadToEnd()];
    }

    internal static void EnsureLocMap(string client) => EnsureLocMap(client, PlayerLocalizationDir,
        (source, path, options) => GameDataExport.WriteLocMap(source, path, options));

    /// <summary>可注入純自造 JSON writer 的相同產品分派；測試不執行原生 Lua。</summary>
    internal static void EnsureLocMap(string client, string directory,
        Func<string, string, GameDataExport.Options, int> writeMaps)
    {
        var options = new GameDataExport.Options
        {
            Translations = Translations(), TraditionalChineseOverrides = TraditionalChineseOverrides(), Log = SetupLog.Write,
        };
        try
        {
            string inputs = LocalizationInputs(client, options);
            if (LocalizationFresh(directory, inputs)) return;
            foreach (string name in LocalizationNames)
                if (File.Exists(Path.Combine(directory, name)) && !GeneratedLocalization(Path.Combine(directory, name), name))
                    throw new ExportException("保留自訂語系快取，未自動覆寫：" + name);
            Ui.Step("準備原生繁中與英文顯示文字");
            Directory.CreateDirectory(directory);
            int count = writeMaps(client, Path.Combine(directory, "loc_en.tsv"), options);
            if (LocalizationNames.Any(name => !GeneratedLocalization(Path.Combine(directory, name), name)))
                throw new ExportException("語系輸出缺少完整英文／繁中配對檔。");
            if (LocalizationInputs(client, options) != inputs) throw new ExportException("建立期間客戶端語系來源已變更。");
            var stamp = new LocalizationCache(1, inputs, LocalizationNames.ToDictionary(name => name,
                name => LocalizationHash(Path.Combine(directory, name)), StringComparer.Ordinal));
            WriteLocalizationStamp(directory, stamp);
            Ui.Ok($"已從你的三語來源建立 {count} 筆英文及繁中配對檔");
        }
        catch (Exception e) when (e is ExportException or IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            throw new SetupException("玩家語系檔準備失敗，尚未修補遊戲：" + e.Message,
                "保留現有自訂檔案；請修復同一客戶端的三語來源或完整語系配對後重試。");
        }
    }

    private static string LocalizationInputs(string client, GameDataExport.Options options)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string source in GameDataExport.LocSourceFiles(client))
        {
            if (!File.Exists(source)) throw new ExportException("客戶端缺少語系來源：" + Path.GetFileName(source));
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(source) + "\n" + LocalizationHash(source) + "\n"));
        }
        foreach (string text in options.Translations.Concat(options.TraditionalChineseOverrides))
            hash.AppendData(Encoding.UTF8.GetBytes(text.Length + ":" + text));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static bool LocalizationFresh(string directory, string inputs)
    {
        string path = Path.Combine(directory, "locmap.sources.json");
        if (!File.Exists(path)) return false;
        try
        {
            var stamp = JsonSerializer.Deserialize<LocalizationCache>(File.ReadAllBytes(path));
            return stamp is { Format: 1 } && stamp.Inputs == inputs && stamp.Outputs is { Count: 3 }
                && LocalizationNames.All(name => GeneratedLocalization(Path.Combine(directory, name), name)
                    && stamp.Outputs.TryGetValue(name, out var expected) && LocalizationHash(Path.Combine(directory, name)) == expected);
        }
        catch (Exception e) when (e is IOException or JsonException) { return false; }
    }

    private static void WriteLocalizationStamp(string directory, LocalizationCache stamp)
    {
        string target = Path.Combine(directory, "locmap.sources.json"), temporary = target + "." + Guid.NewGuid().ToString("N") + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(stamp));
        File.Move(temporary, target, overwrite: true);
    }

    private static string LocalizationHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static bool GeneratedLocalization(string path, string name)
    {
        if (!File.Exists(path)) return false;
        string? header = File.ReadLines(path).FirstOrDefault()?.TrimStart('\uFEFF');
        return name == "loc_en.tsv" ? header?.StartsWith(ClientPatcher.GeneratedLocHeader, StringComparison.Ordinal) == true
            : header == TraditionalChineseMapBuilder.Header;
    }

    internal static void CopyLocalizationMaps(string directory, string client, Action<int>? copiedPairFileForTest = null)
    {
        string destination = Path.Combine(client, "ro3emu");
        string[] pair = [TraditionalChineseMapBuilder.TextFile, TraditionalChineseMapBuilder.SidFile];
        if (pair.Any(name => !GeneratedLocalization(Path.Combine(directory, name), name)))
            throw new SetupException("繁中來源配對不完整，尚未修補遊戲。", "請先重新產生完整的繁中與 SID 檔案。");
        bool custom = pair.Any(name => File.Exists(Path.Combine(destination, name))
            && !GeneratedLocalization(Path.Combine(destination, name), name));
        if (custom && pair.Any(name => !File.Exists(Path.Combine(destination, name))))
            throw new SetupException("自訂繁中配對缺少一個檔案；已保留所有原檔。", "請補齊自訂配對或自行備份移走該檔後重試。");
        Directory.CreateDirectory(destination);
        string english = Path.Combine(destination, "loc_en.tsv"), source = Path.Combine(directory, "loc_en.tsv");
        if (File.Exists(source) && (!File.Exists(english) || GeneratedLocalization(english, "loc_en.tsv")))
            File.Copy(source, english, overwrite: true);
        if (!custom) CopyGeneratedLocalizationPair(directory, destination, pair, copiedPairFileForTest);
    }

    private static void CopyGeneratedLocalizationPair(string source, string destination, string[] names, Action<int>? copied)
    {
        string work = Path.Combine(destination, ".locpair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        int changed = 0;
        try
        {
            foreach (string name in names)
            {
                File.Copy(Path.Combine(source, name), Path.Combine(work, name));
                if (File.Exists(Path.Combine(destination, name)))
                    File.Copy(Path.Combine(destination, name), Path.Combine(work, name + ".before"));
            }
            foreach (string name in names)
            {
                File.Move(Path.Combine(work, name), Path.Combine(destination, name), overwrite: true);
                changed++;
                copied?.Invoke(changed);
            }
        }
        catch
        {
            foreach (string name in names.Take(changed))
                if (File.Exists(Path.Combine(work, name + ".before")))
                    File.Copy(Path.Combine(work, name + ".before"), Path.Combine(destination, name), overwrite: true);
                else File.Delete(Path.Combine(destination, name));
            throw;
        }
        finally { Directory.Delete(work, recursive: true); }
    }
}
