using System.Text.Json;
using System.Text.Json.Nodes;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class ShortcutBarSyncTests
{
    [Fact]
    public async Task AssigningAndClearingSlotsUpdatesTheExistingClientSlotsWithoutRelogging()
    {
        await using var host = await SyntheticHost.StartAsync();
        await ConfigurePotion(host);
        long role, potion;
        await using (var bot = await host.InWorldAsync("Alice"))
        {
            role = (long)bot.Self!.Uuid;
            var login = await Next(bot);
            Assert.NotNull(login.ShortcutBarInfos?.All);
            var slots = login.ShortcutBarInfos.All.Value;
            Assert.Equal(Enumerable.Range(0, 6), slots.Select(s => s.SlotIndex));
            Assert.All(slots, s => Assert.Equal(0, s.ItemGuid));
            potion = login.PackInfo[1].All.Items.Values.Single().ItemGuid;

            var assigned = await Save(bot, 5, potion);
            Assert.Null(assigned.ShortcutBarInfos.All);
            var changes = assigned.ShortcutBarInfos.Inc.Value;
            Assert.Equal(6, changes.Count);
            Assert.True(changes[5].HasItemGuid);
            Assert.Equal(potion, changes[5].ItemGuid);
            Assert.All(changes.Take(5), s => Assert.False(s.HasItemGuid));

            var moved = await Save(bot, 0, potion);
            Assert.True(moved.ShortcutBarInfos.Inc.Value[5].HasItemGuid);
            Assert.Equal(0, moved.ShortcutBarInfos.Inc.Value[5].ItemGuid);
            Assert.Equal(potion, moved.ShortcutBarInfos.Inc.Value[0].ItemGuid);
            var cleared = await Save(bot, 0, 0);
            Assert.True(cleared.ShortcutBarInfos.Inc.Value[0].HasItemGuid);
            Assert.Equal(0, cleared.ShortcutBarInfos.Inc.Value[0].ItemGuid);
            Assert.All(cleared.ShortcutBarInfos.Inc.Value.Skip(1), s => Assert.False(s.HasItemGuid));
            await Save(bot, 0, potion);

            var stored = DBItemsInfo.Parser.ParseFrom(host.Service<RoleStore>().LoadBagState(role)!);
            var binding = Assert.Single(stored.ShortcutBarInfos);
            Assert.Equal(0, binding.SlotIndex);
            Assert.Equal(potion, binding.ItemGuid);
        }
        await using var again = await host.InWorldAsync("Alice", create: false);
        var restored = (await Next(again)).ShortcutBarInfos.All.Value;
        Assert.Equal(6, restored.Count);
        Assert.Equal(potion, restored[0].ItemGuid);
        Assert.All(restored.Skip(1), s => Assert.Equal(0, s.ItemGuid));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task MovingAShortcutClearsOldBindingsWithoutMovingOrConsumingTheItem(int oldBindings)
    {
        await using var host = await SyntheticHost.StartAsync();
        await ConfigurePotion(host);
        await using var bot = host.NewBot();
        await bot.LoginAsync("alice", "pw");
        await bot.EnterGateAsync();
        long role = (await bot.CreateRoleAsync("Alice")).RoleBaseInfo.Id;
        var roles = host.Service<RoleStore>();
        var progress = roles.LoadProgress(role);
        progress.StarterKit = true;
        roles.SaveProgress(role, progress);
        var item = new InvItem { Entry = 1000000, Stack = 7, Pack = 1, Sort = 1 };
        host.Service<ItemStore>().Insert(role, item);
        var preferences = new DBItemsInfo();
        for (int slot = 0; slot < oldBindings; slot++)
            preferences.ShortcutBarInfos.Add(new ShortcutBarInfo { SlotIndex = slot, ItemGuid = item.Guid });
        roles.SaveBagState(role, Google.Protobuf.MessageExtensions.ToByteArray(preferences));
        await bot.EnterWorldAsync(role);
        await Next(bot);

        var moved = await Save(bot, 5, item.Guid);
        var changes = moved.ShortcutBarInfos.Inc.Value;
        Assert.Equal(item.Guid, changes[5].ItemGuid);
        foreach (var change in changes.Take(oldBindings))
        {
            Assert.True(change.HasItemGuid);
            Assert.Equal(0, change.ItemGuid);
        }
        var saved = DBItemsInfo.Parser.ParseFrom(roles.LoadBagState(role)!);
        var binding = Assert.Single(saved.ShortcutBarInfos);
        Assert.Equal(5, binding.SlotIndex);
        Assert.Equal(item.Guid, binding.ItemGuid);
        var owned = Assert.Single(host.Service<ItemStore>().Load(role));
        Assert.Equal(item.Guid, owned.Guid);
        Assert.Equal(7, owned.Stack);
        Assert.Equal(0, owned.Grid);
        Assert.Empty(moved.PackInfo);
    }

    private static Task ConfigurePotion(SyntheticHost host) => host.Service<GameLoop>().InvokeAsync(() =>
    {
        var table = (IDictionary<string, JsonElement>)host.Service<GameData>().Table("ItemConfig");
        var row = JsonNode.Parse(table["1000000"].GetRawText()) ?? throw new InvalidOperationException("Missing fixture potion.");
        row["_iIsUse"] = 1;
        row["_iUseEffect"] = JsonNode.Parse("[[100,1,7,0]]"); // invented recovery amount for this synthetic fixture
        table["1000000"] = JsonSerializer.SerializeToElement(row);
        return Task.CompletedTask;
    });

    private static async Task<DBItemsInfoIncrement> Save(BotClient bot, int slot, long guid)
    {
        await bot.SendGameAsync(CharacterService.ShortcutBarSave, new ShortcutBarSaveReq { SlotIndex = slot, ItemGuid = guid });
        var response = await bot.Inbox.WaitAsync(e => e.IsGame(CharacterService.ShortcutBarSave), TimeSpan.FromSeconds(5));
        Assert.Equal(0, ShortcutBarSaveRes.Parser.ParseFrom(response.Body).Result);
        return await Next(bot);
    }

    private static async Task<DBItemsInfoIncrement> Next(BotClient bot) => DBItemsInfoIncrement.Parser.ParseFrom(
        (await bot.Inbox.WaitAsync(e => e.IsGame(CharacterService.ItemsSync), TimeSpan.FromSeconds(5))).Body);
}
