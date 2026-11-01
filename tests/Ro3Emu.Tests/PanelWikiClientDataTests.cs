using System.Text.Json;

namespace Ro3Emu.Tests;

/// <summary>The panel's game database on your own exported game data: the numbers a player can check in the client.</summary>
public sealed class PanelWikiClientDataTests
{
    private static string Text(JsonElement runs) => string.Concat(runs.EnumerateArray().Select(r => r.GetProperty("text").GetString()));

    [ClientDataFact]
    public async Task TheDatabasePagesReadTheClientsOwnNumbers()
    {
        var (host, panel) = await PanelTests.StartAsync(o => o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data"));
        await using var _ = host;
        using var browser = new PanelTests.PanelBrowser(panel);

        // Card: Lunatic, four stars, levels 0-15 of a green weapon card, CRIT shown as value / 100.
        var lunatic = await browser.GetJsonAsync("/api/cards/1420100");
        Assert.Equal(4, lunatic.GetProperty("tiers").GetArrayLength());
        Assert.Equal(16, lunatic.GetProperty("levels").GetArrayLength());
        Assert.Contains("CRIT +1", lunatic.GetProperty("tiers")[0].GetProperty("effects").EnumerateArray().Select(Text));

        // Knight's own tree and Bash's first level as the skill tip writes it.
        var knight = await browser.GetJsonAsync("/api/classes/1200");
        Assert.Equal(10, knight.GetProperty("trees")[0].GetProperty("cells").GetArrayLength());
        var bash = await browser.GetJsonAsync("/api/skills/11001");
        Assert.Contains("46% + 26", Text(bash.GetProperty("levels")[0].GetProperty("description")));

        var potion = (await browser.GetJsonAsync("/api/crafting")).GetProperty("recipes").EnumerateArray().Single(r => r.GetProperty("id").GetInt64() == 20001);
        Assert.Equal("Chef Lv 1", potion.GetProperty("requirement").GetString());
        Assert.Contains(1001101, potion.GetProperty("products").EnumerateArray().Select(p => p.GetProperty("itemId").GetInt64()));

        var speeds = (await browser.GetJsonAsync("/api/mounts")).GetProperty("mounts").EnumerateArray().Select(m => m.GetProperty("speedPercent").GetDouble()).ToHashSet();
        Assert.Subset(new HashSet<double> { 50, 80, 120 }, speeds);

        var prontera = await browser.GetJsonAsync("/api/maps/10100/world");
        Assert.Equal("Town", prontera.GetProperty("type").GetString());
        Assert.True(prontera.GetProperty("kafra").GetBoolean());
        Assert.Contains(prontera.GetProperty("npcs").EnumerateArray(), n => n.GetProperty("functions").EnumerateArray().Any(f => f.GetString()!.Contains("Kafra")));

        var poring = await browser.GetJsonAsync("/api/monsters/5000001/combat");
        Assert.Contains(poring.GetProperty("exp").EnumerateArray(), r => r.GetProperty("kind").GetString() == "Kill" && r.GetProperty("min").GetInt64() == 5);
        Assert.Equal(50, poring.GetProperty("taken").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "Wind").GetProperty("percent").GetInt32());

        Assert.Equal(10, (await browser.GetJsonAsync("/api/elements")).GetProperty("elements").GetArrayLength());
        Assert.NotEmpty((await browser.GetJsonAsync("/api/events")).GetProperty("events").EnumerateArray());
        Assert.Equal(17, (await browser.GetJsonAsync("/api/mvps")).GetProperty("mvps").GetArrayLength());
        Assert.Equal(21, (await browser.GetJsonAsync("/api/pets/10001")).GetProperty("stars").GetArrayLength());
    }
}
