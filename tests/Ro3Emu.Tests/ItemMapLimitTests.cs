using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Tests;

/// <summary>
/// ItemMapLimit, the scene's limit on the items used in it, read as the client reads it: MapConfig for ids up to 99999,
/// DungeonConfig from 1000001 (Lua_CFG_MapConfigBase.GetEntryPtr). ItemUseLimitConfig is not enforced. Invented rows.
/// </summary>
public sealed class ItemMapLimitTests : IDisposable
{
    private const long Open = 501, MapForbids = 502, DungeonOpen = 1000501, DungeonForbids = 1000502, DungeonOnly = 1000503;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3emu-itemlimit-" + Guid.NewGuid().ToString("N"));
    private readonly GameData _data;

    public ItemMapLimitTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "config"));
        Write("MapConfig", new Dictionary<string, object>
        {
            ["501"] = new { _iLimitType = 0, _kLimitItem = "" },
            ["502"] = new { _iLimitType = 1, _kLimitItem = Rows([1, 2]) },
        });
        Write("DungeonConfig", new Dictionary<string, object>
        {
            ["1000501"] = new { _iLimitType = 0, _kLimitItem = new { } },
            ["1000502"] = new { _iLimitType = 1, _kLimitItem = Rows([1, 2], [2, 301]) },
            ["1000503"] = new { _iLimitType = 2, _kLimitItem = Rows([2, 202]) },
        });
        Write("ItemUseLimitConfig", new Dictionary<string, object>
        {
            ["1001"] = new { _iID = 1001, _iLimitType = 1, _kParam = (long[])[501, 1000501] },
        });
        _data = new GameData(Options.Create(new ServerOptions { GameDataDir = _dir }), NullLogger<GameData>.Instance);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static int[][] Rows(params int[][] entries) => entries;

    private void Write(string table, object rows) =>
        File.WriteAllText(Path.Combine(_dir, "config", table + ".json"), JsonSerializer.Serialize(rows));

    private static JsonElement Item(int type, int subType) =>
        JsonDocument.Parse(JsonSerializer.Serialize(new { _iType = type, _iSubType = subType, _kUseLimit = (int[])[1001] })).RootElement;

    [Theory]
    [InlineData(Open, 2, 204, true)]
    [InlineData(MapForbids, 2, 204, false)] // [1, type]
    [InlineData(MapForbids, 3, 204, true)]
    [InlineData(DungeonOpen, 2, 204, true)] // a dungeon with no limit, and no MapConfig row
    [InlineData(DungeonForbids, 2, 204, false)]
    [InlineData(DungeonForbids, 3, 301, false)] // [2, subtype]
    [InlineData(DungeonForbids, 3, 302, true)]
    [InlineData(DungeonOnly, 2, 202, true)]
    [InlineData(DungeonOnly, 2, 204, false)]
    [InlineData(99999, 2, 204, true)] // no row: the client finds no limit
    [InlineData(1000000, 2, 204, true)] // between the two ranges
    [InlineData(0, 2, 204, true)]
    public void TheScenesOwnRowDecides(long scene, int type, int subType, bool allowed) =>
        Assert.Equal(allowed, ItemMapLimit.Allows(_data, scene, Item(type, subType)));

    /// <summary>The client's own tables decide the Yggdrasil Leaf (1002004, type 2, subtype 204): 159 dungeons have no limit.</summary>
    [ClientDataFact]
    public void TheYggdrasilLeafFollowsEachDungeonsLimit()
    {
        var data = new GameData(Options.Create(new ServerOptions { GameDataDir = Path.Combine(ClientData.RepoRoot, "data") }), NullLogger<GameData>.Instance);
        var leaf = data.Row("ItemConfig", 1002004)!.Value;
        Assert.True(ItemMapLimit.Allows(data, 1000101, leaf));
        Assert.False(ItemMapLimit.Allows(data, 1000502, leaf)); // [1, 2]: type 2
        Assert.False(ItemMapLimit.Allows(data, 1033052, leaf)); // whitelist [2, 202]
        int allowed = data.Table("DungeonConfig").Keys.Count(id => ItemMapLimit.Allows(data, long.Parse(id, System.Globalization.CultureInfo.InvariantCulture), leaf));
        Assert.InRange(allowed, 159, data.Table("DungeonConfig").Count - 1);
        Assert.True(ItemMapLimit.Allows(data, 10111, leaf)); // the starting map: ItemUseLimitConfig is not enforced
        Assert.True(ItemMapLimit.Allows(data, 10110, leaf));
    }
}
