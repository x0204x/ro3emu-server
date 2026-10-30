namespace Ro3Emu.ClientTools;

/// <summary>Resolves optional files that are added outside the publisher's verified official baseline.</summary>
internal static class ReleaseFileSources
{
    /// <summary>繁中覆寫兩檔必須一起提供；保留 SID，避免同字異義只靠英文字串猜測。</summary>
    internal static IReadOnlyList<KeyValuePair<string, string>> TraditionalChinesePack(string clientDir, string? explicitPack)
    {
        string folder = Path.GetFullPath(explicitPack ?? Path.Combine(clientDir, "ro3emu"));
        string[] names = ["loc_zh_TW.tsv", "loc_zh_TW.sids.tsv"];
        if (explicitPack is null && !names.Any(n => File.Exists(Path.Combine(folder, n)))) return [];
        var result = new List<KeyValuePair<string, string>>();
        foreach (string name in names)
        {
            string path = Path.Combine(folder, name);
            if (!File.Exists(path)) throw new FileNotFoundException("繁中覆寫缺少配對檔案：" + name, path);
            if (File.ReadLines(path).FirstOrDefault()?.TrimStart('\uFEFF') != "# RO3Emu localization profile zh-TW v1")
                throw new InvalidDataException("未知的繁中覆寫格式：" + name);
            result.Add(new("ro3emu/" + name, path));
        }
        return result;
    }

    internal static KeyValuePair<string, string>? LocalizationMap(string clientDir, string? explicitMap)
    {
        string path = Path.GetFullPath(explicitMap ?? Path.Combine(clientDir, "ro3emu", "loc_en.tsv"));
        if (File.Exists(path)) return new("ro3emu/loc_en.tsv", path);
        if (explicitMap is not null) throw new FileNotFoundException("the localization map does not exist", path);
        return null;
    }

    internal static KeyValuePair<string, string>? SkillTraceMarker(string stagingDir, bool enabled)
    {
        if (!enabled) return null;
        string path = Path.GetFullPath(Path.Combine(stagingDir, "ro3emu", "skilltrace.on"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        return new("ro3emu/skilltrace.on", path);
    }
}
