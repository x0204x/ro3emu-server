using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class ActivityAdventuresTests
{
    private sealed record ResumeHunt(BotClient Bot, GameSession Session, long Guid, long PointId);

    private sealed class ResumeEvents(CharacterService character, GameSession session)
    {
        public int Used { get; private set; }
        public int Changed { get; private set; }
        private void OnUsed(GameSession s, int entry, int count)
        {
            if (ReferenceEquals(s, session) && entry == 1112001) Used += count;
        }
        private void OnChanged(GameSession s)
        {
            if (ReferenceEquals(s, session)) Changed++;
        }
        public void Attach() { character.ItemUsed.Add(OnUsed); character.StateChanged.Add(OnChanged); }
        public void Detach() { character.ItemUsed.Remove(OnUsed); character.StateChanged.Remove(OnChanged); }
    }

    private Task ResumeOwned(GameSession session, Func<Task> action) =>
        _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
            _app.Services.GetRequiredService<OwnerRegistry>().RunSessionAsync("treasure resume fixture",
                [ReservationKey.Role(session.Role!.Id)], session, action));

    private string ResumeSnapshot(GameSession session)
    {
        var roles = _app.Services.GetRequiredService<RoleStore>();
        long role = session.Role!.Id;
        return JsonSerializer.Serialize(new
        {
            Cursor = JsonSerializer.Serialize(session.ActivityRuntime.AdventureTreasure),
            Bag = Convert.ToBase64String(session.BagState.Info.ToByteArray()),
            BagDb = Convert.ToBase64String(roles.LoadBagState(role) ?? []),
            RuntimeDb = Convert.ToBase64String(roles.LoadActivityRuntime(role) ?? []),
            Items = session.Items.OrderBy(i => i.Guid).ToArray(),
            ItemsDb = _app.Services.GetRequiredService<ItemStore>().Load(role).OrderBy(i => i.Guid).ToArray(),
            ItemCds = session.ItemCds.OrderBy(p => p.Key).ToArray(),
            GroupCds = session.ItemGroupCds.OrderBy(p => p.Key).ToArray(),
            UseTimes = session.BagState.Info.UseTimes.OrderBy(p => p.Key).ToArray(),
            Tasks = Convert.ToBase64String(session.Tasks?.ToByteArray() ?? []),
            TasksDb = Convert.ToBase64String(roles.LoadTasks(role) ?? []),
        });
    }

    private async Task<string> ResumeSnapshotAsync(GameSession session)
    {
        string snapshot = "";
        await ResumeOwned(session, () => { snapshot = ResumeSnapshot(session); return Task.CompletedTask; });
        return snapshot;
    }

    private async Task<ResumeHunt> StartResumeHuntAsync()
    {
        var window = _app.Services.GetRequiredService<ActivityClock>()
            .Segments(1017, _clock.GetUtcNow().ToUnixTimeSeconds()).Single();
        _clock.Set(window.Start + 60);
        var bot = await InWorld("Alice");
        await Gm("alice", "!lvl 99");
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Name == "Alice");
        if (session.SceneEntry != 10111) await Gm("alice", "!warp 10111");
        await Gm("alice", "!item 1112001 2");
        await ResumeOwned(session, () =>
        {
            // 只在測試快取加入人造冷卻與使用上限，確保恢復沒有沿用消耗流程；不改凍結資料。
            var table = (IDictionary<string, JsonElement>)_app.Services.GetRequiredService<GameData>().Table("ItemConfig");
            var row = JsonNode.Parse(table["1112001"].GetRawText())!;
            row["_iCD"] = 120000;
            row["_kUseTimes"] = new JsonArray(1, 4);
            table["1112001"] = JsonSerializer.SerializeToElement(row);
            return Task.CompletedTask;
        });
        long guid = session.Items.Single(i => i.Entry == 1112001).Guid;
        int from = bot.Inbox.Snapshot().Count;
        await bot.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = guid, ItemCount = 1 });
        var response = await Next(bot, CharacterService.ItemUse, ItemUseResp.Parser);
        Assert.Equal(0, response.Result);
        Assert.Equal(1, response.ItemCount);
        Assert.Equal(1112001, response.ItemId);
        var cursor = await Next(bot, 5064, DBTreasureInfoIncrement.Parser, i => i.PointId > 0);
        await Next(bot, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            view => view.Bases.Any(actor => actor.Uuid == (ulong)(SceneService.TreasureUidPrefix | session.Role!.Id)));
        var frames = bot.Inbox.Snapshot().Skip(from).ToArray();
        int ack = Array.FindIndex(frames, e => e.IsGame(CharacterService.ItemUse));
        int point = Array.FindIndex(frames, e => e.IsGame(5064)
            && DBTreasureInfoIncrement.Parser.ParseFrom(e.Body).PointId == cursor.PointId);
        Assert.True(ack >= 0 && point > ack, "初次扣圖的 ACK 必須先於尋寶點投影。");
        await ResumeOwned(session, () =>
        {
            Assert.Equal(10111, session.SceneEntry);
            Assert.Equal(1, session.Items.Single(i => i.Guid == guid).Stack);
            Assert.Equal(1, _app.Services.GetRequiredService<ItemStore>().Load(session.Role!.Id).Single(i => i.Guid == guid).Stack);
            Assert.True(session.ItemCds[1112001] > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Assert.Equal(1, session.BagState.Info.UseTimes[1112001]);
            return Task.CompletedTask;
        });
        return new(bot, session, guid, cursor.PointId);
    }

    private static void AssertResumeFrameOrder(BotClient bot, int from, uint ackId, long pointId)
    {
        var frames = bot.Inbox.Snapshot().Skip(from).ToArray();
        int ack = Array.FindIndex(frames, e => e.IsGame(ackId));
        int point = Array.FindIndex(frames, e => e.IsGame(5064)
            && DBTreasureInfoIncrement.Parser.ParseFrom(e.Body) is { SyncAllMark: true } state && state.PointId == pointId);
        Assert.True(ack >= 0 && point > ack, "恢復必須先回覆該請求，再重送同點完整 5064。");
        Assert.DoesNotContain(frames, e => e.IsGame(CharacterService.ItemsSync) || e.IsGame(CharacterService.ItemsCdNotify));
        if (ackId == CharacterService.ShortcutBarUse)
            Assert.DoesNotContain(frames, e => e.IsGame(CharacterService.ItemUse));
    }

    [ClientDataFact]
    public async Task TreasureResumeDuringCooldownReplaysOnlyCommittedPointWithoutAnyUseEffects()
    {
        var hunt = await StartResumeHuntAsync();
        await using var bot = hunt.Bot;
        var watch = new ResumeEvents(_app.Services.GetRequiredService<CharacterService>(), hunt.Session);
        string before = await ResumeSnapshotAsync(hunt.Session);
        await ResumeOwned(hunt.Session, () => { watch.Attach(); return Task.CompletedTask; });
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                int from = bot.Inbox.Snapshot().Count;
                await bot.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = hunt.Guid, ItemCount = 1 });
                var response = await Next(bot, CharacterService.ItemUse, ItemUseResp.Parser);
                Assert.Equal(0, response.Result);
                Assert.Equal(hunt.Guid, response.ItemGuid);
                Assert.Equal(0, response.ItemCount);
                Assert.Equal(0, response.ItemId);
                Assert.Equal(0, response.ItemCd);
                Assert.Equal(0, response.ShareCd);
                Assert.Equal(0, response.ItemTimes);
                await Next(bot, 5064, DBTreasureInfoIncrement.Parser,
                    info => info.SyncAllMark && info.PointId == hunt.PointId);
                AssertResumeFrameOrder(bot, from, CharacterService.ItemUse, hunt.PointId);
                Assert.Equal(before, await ResumeSnapshotAsync(hunt.Session));
                Assert.Equal(0, watch.Used);
                Assert.Equal(0, watch.Changed);
            }
        }
        finally { await ResumeOwned(hunt.Session, () => { watch.Detach(); return Task.CompletedTask; }); }
    }

    [ClientDataFact]
    public async Task TreasureResumeShortcutRepliesBeforeFullPointWithoutExtraDirectReply()
    {
        var hunt = await StartResumeHuntAsync();
        await using var bot = hunt.Bot;
        await bot.SendGameAsync(CharacterService.ShortcutBarSave, new ShortcutBarSaveReq { SlotIndex = 0, ItemGuid = hunt.Guid });
        Assert.Equal(0, (await Next(bot, CharacterService.ShortcutBarSave, ShortcutBarSaveRes.Parser)).Result);
        // 等待存快捷列造成的 5009，避免把上一個請求的合法同步算成恢復副作用。
        await Next(bot, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
            info => info.ShortcutBarInfos != null);
        string before = await ResumeSnapshotAsync(hunt.Session);
        var watch = new ResumeEvents(_app.Services.GetRequiredService<CharacterService>(), hunt.Session);
        await ResumeOwned(hunt.Session, () => { watch.Attach(); return Task.CompletedTask; });
        try
        {
            int from = bot.Inbox.Snapshot().Count;
            await bot.SendGameAsync(CharacterService.ShortcutBarUse, new ShortcutBarUseReq { SlotIndex = 0 });
            var response = await Next(bot, CharacterService.ShortcutBarUse, ShortcutBarUseRes.Parser);
            Assert.Equal(0, response.Result);
            Assert.Equal(0, response.SlotIndex);
            await Next(bot, 5064, DBTreasureInfoIncrement.Parser,
                info => info.SyncAllMark && info.PointId == hunt.PointId);
            AssertResumeFrameOrder(bot, from, CharacterService.ShortcutBarUse, hunt.PointId);
            Assert.Equal(before, await ResumeSnapshotAsync(hunt.Session));
            Assert.Equal(0, watch.Used);
            Assert.Equal(0, watch.Changed);
        }
        finally { await ResumeOwned(hunt.Session, () => { watch.Detach(); return Task.CompletedTask; }); }
        // 此測試只證明伺服器 CD 不變；原生 StartSlotCD 的呈現由玩家端另驗。
    }

    [ClientDataFact]
    public async Task TreasureResumeRefusesExpiredLockedDeadAndForeignGuidWithoutRecovering()
    {
        var hunt = await StartResumeHuntAsync();
        await using var bot = hunt.Bot;
        var character = _app.Services.GetRequiredService<CharacterService>();
        foreach (string invalid in new[] { "expired", "locked", "dead", "foreign-guid" })
        {
            int from = bot.Inbox.Snapshot().Count;
            await ResumeOwned(hunt.Session, async () =>
            {
                var cursor = hunt.Session.ActivityRuntime.AdventureTreasure;
                var item = hunt.Session.Items.Single(i => i.Guid == hunt.Guid);
                long began = cursor.Began;
                bool wasDead = hunt.Session.Dead, wasLocked = item.IsLocked;
                try
                {
                    // 受控快取 fixture 不寫 DB；到期時保留現有物品 CD，不能誤進恢復分支。
                    if (invalid == "expired") cursor.Began -= 10801;
                    if (invalid == "locked") item.IsLocked = true;
                    if (invalid == "dead") hunt.Session.Dead = true;
                    string before = ResumeSnapshot(hunt.Session);
                    long guid = invalid == "foreign-guid" ? long.MaxValue - 11 : hunt.Guid;
                    Assert.False(invalid == "foreign-guid" && hunt.Session.Items.Any(i => i.Guid == guid));
                    await character.UseItemAsync(hunt.Session, new ItemUseReq { ItemGuid = guid, ItemCount = 1 });
                    Assert.Equal(before, ResumeSnapshot(hunt.Session));
                }
                finally { cursor.Began = began; hunt.Session.Dead = wasDead; item.IsLocked = wasLocked; }
            });
            var response = await Next(bot, CharacterService.ItemUse, ItemUseResp.Parser);
            Assert.NotEqual(0, response.Result);
            Assert.DoesNotContain(bot.Inbox.Snapshot().Skip(from), frame => frame.IsGame(5064));
        }
    }

    [ClientDataFact]
    public async Task TreasureResumePreservesAnOutsideMapCursorAcrossReplayAndRelog()
    {
        var hunt = await StartResumeHuntAsync();
        var bot = hunt.Bot;
        long roleId = hunt.Session.Role!.Id;
        string cursor = JsonSerializer.Serialize(hunt.Session.ActivityRuntime.AdventureTreasure);
        await Gm("alice", "!warp 10101");
        Assert.Equal(10101, hunt.Session.SceneEntry);
        string before = await ResumeSnapshotAsync(hunt.Session);
        int from = bot.Inbox.Snapshot().Count;
        await bot.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = hunt.Guid, ItemCount = 1 });
        var response = await Next(bot, CharacterService.ItemUse, ItemUseResp.Parser);
        Assert.Equal(0, response.Result);
        Assert.Equal(0, response.ItemCount);
        Assert.Equal(0, response.ItemId);
        await Next(bot, 5064, DBTreasureInfoIncrement.Parser,
            info => info.SyncAllMark && info.PointId == hunt.PointId);
        AssertResumeFrameOrder(bot, from, CharacterService.ItemUse, hunt.PointId);
        Assert.Equal(before, await ResumeSnapshotAsync(hunt.Session));
        Assert.Equal(cursor, JsonSerializer.Serialize(hunt.Session.ActivityRuntime.AdventureTreasure));
        await bot.DisposeAsync();
        await QuestProbe.OfflineAsync(_app, "alice");
        await using var again = await InWorld("Alice", create: false);
        var restored = await Next(again, 5064, DBTreasureInfoIncrement.Parser,
            info => info.SyncAllMark && info.PointId == hunt.PointId);
        var current = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == roleId);
        Assert.Equal(hunt.PointId, restored.PointId);
        Assert.Equal(cursor, JsonSerializer.Serialize(current.ActivityRuntime.AdventureTreasure));
        Assert.Equal(1, current.Items.Single(i => i.Guid == hunt.Guid).Stack);
        await again.SendGameAsync(MsgId.PlayerAsset, new PlayerAssetReq { AssetType = { 57 } });
        await Next(again, 5064, DBTreasureInfoIncrement.Parser,
            info => info.SyncAllMark && info.PointId == hunt.PointId);
        Assert.Equal(cursor, JsonSerializer.Serialize(current.ActivityRuntime.AdventureTreasure));
        Assert.Equal(1, _app.Services.GetRequiredService<ItemStore>().Load(roleId).Single(i => i.Guid == hunt.Guid).Stack);
    }
}
