using System.Text.Json;

namespace Ro3Emu.DataExport;

/// <summary>The pictures of the panel's public database beyond items, monsters, classes, maps and skills.</summary>
public static partial class IconExport
{
    /// <summary>Pets (head and illustration), mount appearances, the To-Do list's activity icons, the life jobs' icons
    /// and the cooking recipes' icons.</summary>
    private static void AddWikiPictures(string configDir, Action<string, string?, int> add)
    {
        foreach (var row in Rows(configDir, "PetBaseConfig"))
        {
            add("pets", Text(row, "_kHeadPic"), int.MaxValue);
            add("pets", Text(row, "_kEncyclopediaPic"), 512);
        }
        foreach (var row in Rows(configDir, "WardRobeConfig"))
            if (row.TryGetProperty("_iType", out var type) && type.ValueKind == JsonValueKind.Number && type.GetInt64() == MountAppearance)
                add("mounts", Text(row, "_kIconB"), int.MaxValue);
        foreach (var row in Rows(configDir, "DailyActivityConfig"))
            if (row.TryGetProperty("_kIcon", out var icons) && icons.ValueKind == JsonValueKind.Array && icons.GetArrayLength() > 0)
                add("events", icons[0].ValueKind == JsonValueKind.String ? icons[0].GetString() : null, 256);
        foreach (var row in Rows(configDir, "LifeFormulaConfig"))
            add("items", Text(row, "_kFormulaIcon"), int.MaxValue);
    }

    /// <summary>WardRobeConfig _iType 6: a mount's appearance (its _iSystemID is the MountsConfig id).</summary>
    private const long MountAppearance = 6;
}
