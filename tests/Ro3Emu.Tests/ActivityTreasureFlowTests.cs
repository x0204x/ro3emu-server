using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Maps;
using System.Text.Json;

namespace Ro3Emu.Tests;

public sealed partial class ActivityAdventuresTests
{
    // G 的 StartHunt／GetAreaCenter 僅支援當前地圖；拒絕外圖選點不得消耗圖或寫入進度。
    [ClientDataTheory]
    [InlineData(1112001, 10101)]
    [InlineData(1112002, 10111)]
    public async Task TreasureMapWithoutEligibleLocalPointPreservesBagCooldownAndCursor(int itemId, int mapId)
    {
        var window = _app.Services.GetRequiredService<ActivityClock>()
            .Segments(1017, _clock.GetUtcNow().ToUnixTimeSeconds()).Single();
        _clock.Set(window.Start + 60);
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99");
        await Gm("alice", $"!warp {mapId}");
        await Gm("alice", $"!item {itemId} 2");
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Name == "Alice");
        Assert.Equal(mapId, session.SceneEntry);
        Assert.Equal(RuntimeMapKind.PublicLine, session.RuntimeMap.Kind);
        Assert.DoesNotContain(TreasureHuntMapPointConfig.All(_app.Services.GetRequiredService<GameData>()),
            point => point.MapId == mapId && (itemId == 1112002 ? point.Id >= 2000 : point.Id < 2000));
        long roleId = session.Role!.Id;
        long guid = session.Items.Single(i => i.Entry == itemId).Guid;
        var roles = _app.Services.GetRequiredService<RoleStore>();
        var owners = _app.Services.GetRequiredService<OwnerRegistry>();
        var loop = _app.Services.GetRequiredService<GameLoop>();
        await loop.InvokeAsync(() => owners.RunSessionAsync("treasure local-point refusal test",
            [ReservationKey.Role(roleId)], session, async () =>
        {
            byte[]? runtimeBefore = roles.LoadActivityRuntime(roleId), bagBefore = roles.LoadBagState(roleId);
            byte[] liveBagBefore = session.BagState.Info.ToByteArray();
            string cursorBefore = JsonSerializer.Serialize(session.ActivityRuntime.AdventureTreasure);
            var cooldownBefore = session.ItemCds.OrderBy(p => p.Key).ToArray();

            await _app.Services.GetRequiredService<CharacterService>().UseItemAsync(session,
                new ItemUseReq { ItemGuid = guid, ItemCount = 1 });

            Assert.Equal(2, session.Items.Single(i => i.Guid == guid).Stack);
            Assert.Equal(2, _app.Services.GetRequiredService<ItemStore>().Load(roleId).Single(i => i.Guid == guid).Stack);
            Assert.Equal(cursorBefore, JsonSerializer.Serialize(session.ActivityRuntime.AdventureTreasure));
            Assert.Equal(cooldownBefore, session.ItemCds.OrderBy(p => p.Key).ToArray());
            Assert.Equal(liveBagBefore, session.BagState.Info.ToByteArray());
            Assert.Equal(runtimeBefore, roles.LoadActivityRuntime(roleId));
            Assert.Equal(bagBefore, roles.LoadBagState(roleId));
        }));
        Assert.Equal(ErrorCodes.ItemUseAreaForbidden,
            (await Next(alice, CharacterService.ItemUse, ItemUseResp.Parser)).Result);
    }

    // 本圖接取、完整資產重送與重新登入應沿用同一筆已提交游標，不重新抽點或再次扣圖。
    [ClientDataFact]
    public async Task RareTreasureMapStartsLocallyAndFullReplayAndRelogPreserveTheSameHunt()
    {
        var window = _app.Services.GetRequiredService<ActivityClock>()
            .Segments(1017, _clock.GetUtcNow().ToUnixTimeSeconds()).Single();
        _clock.Set(window.Start + 60);
        var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99");
        await Gm("alice", "!warp 10214");
        await Gm("alice", "!item 1112002 2");
        var registry = _app.Services.GetRequiredService<SessionRegistry>();
        var session = registry.All().Single(s => s.Role?.Name == "Alice");
        long roleId = session.Role!.Id;
        long guid = session.Items.Single(i => i.Entry == 1112002).Guid;
        await alice.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = guid, ItemCount = 1 });
        Assert.Equal(0, (await Next(alice, CharacterService.ItemUse, ItemUseResp.Parser)).Result);
        var started = await Next(alice, 5064, DBTreasureInfoIncrement.Parser, info => info.PointId > 0);
        var point = TreasureHuntMapPointConfig.Get(_app.Services.GetRequiredService<GameData>(), started.PointId);
        Assert.NotNull(point);
        Assert.True(point.Value.Id >= 2000);
        Assert.Equal(session.SceneEntry, point.Value.MapId);
        Assert.Equal(1, session.Items.Single(i => i.Guid == guid).Stack);
        string committedCursor = JsonSerializer.Serialize(session.ActivityRuntime.AdventureTreasure);

        await alice.SendGameAsync(1023, new PlayerAssetReq { AssetType = { 57 } });
        var replayed = await Next(alice, 5064, DBTreasureInfoIncrement.Parser,
            info => info.SyncAllMark && info.PointId == started.PointId);
        Assert.Equal(started.PointId, replayed.PointId);
        Assert.Equal(committedCursor, JsonSerializer.Serialize(session.ActivityRuntime.AdventureTreasure));
        Assert.Equal(1, session.Items.Single(i => i.Guid == guid).Stack);

        await alice.DisposeAsync();
        await QuestProbe.OfflineAsync(_app, "alice");
        await using var again = await InWorld("Alice", create: false);
        var restored = await Next(again, 5064, DBTreasureInfoIncrement.Parser,
            info => info.SyncAllMark && info.PointId == started.PointId);
        Assert.Equal(started.PointId, restored.PointId);
        var restoredSession = registry.All().Single(s => s.Role?.Id == roleId);
        Assert.Equal(committedCursor, JsonSerializer.Serialize(restoredSession.ActivityRuntime.AdventureTreasure));
        Assert.Equal(1, restoredSession.Items.Single(i => i.Guid == guid).Stack);
        Assert.Equal(1, _app.Services.GetRequiredService<ItemStore>().Load(roleId).Single(i => i.Guid == guid).Stack);
        var runtime = JsonSerializer.Deserialize<ActivityRuntimeState>(
            _app.Services.GetRequiredService<RoleStore>().LoadActivityRuntime(roleId)!);
        Assert.NotNull(runtime);
        Assert.Equal(committedCursor, JsonSerializer.Serialize(runtime.AdventureTreasure));
    }
}
