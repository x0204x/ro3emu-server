namespace Ro3Emu.ClientTools;

/// <summary>Resolves optional files that are added outside the publisher's verified official baseline.</summary>
internal static class ReleaseFileSources
{
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
