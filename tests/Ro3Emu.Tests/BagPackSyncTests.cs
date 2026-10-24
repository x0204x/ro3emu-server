using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class BagPackSyncTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FirstGearInAnUnseenPackReinitializesTheVisibleBag(bool emptyOtherPack, bool awardGear)
    {
        await using var host = await SyntheticHost.StartAsync();
        await using var bot = host.NewBot();
        await bot.LoginAsync("alice", "pw");
        await bot.EnterGateAsync();
        long role = (await bot.CreateRoleAsync("Alice")).RoleBaseInfo.Id;
        var store = host.Service<ItemStore>();
        var progress = host.Service<RoleStore>().LoadProgress(role);
        progress.StarterKit = true;
        host.Service<RoleStore>().SaveProgress(role, progress);
        InvItem[] gear =
        [
            new() { Entry = 2010121, Stack = 1, Pack = 2, Grid = 1, Attrs = new() { [3] = 1 } },
            new() { Entry = 2550120, Stack = 1, Pack = 2, Grid = 3, Attrs = new() { [3] = 1 } },
        ];
        foreach (var item in gear) store.Insert(role, item);
        store.Insert(role, new InvItem { Entry = 1000000, Stack = 2, Pack = 1, Sort = 1 });
        await bot.EnterWorldAsync(role);
        var login = await Next(bot);
        Assert.True(login.SyncAllMark);
        Assert.DoesNotContain(2, login.PackInfo.Keys);
        if (emptyOtherPack)
        {
            await host.Service<GameLoop>().InvokeAsync(async () =>
                await host.Service<CharacterService>().ClearBagAsync(host.Service<SessionRegistry>().All().Single(s => s.Role?.Id == role)));
            var cleared = await Next(bot);
            Assert.False(cleared.SyncAllMark);
        }

        long returnedGuid = gear[0].Guid;
        if (awardGear)
        {
            Assert.True(await host.Service<GameLoop>().InvokeAsync(() => host.Service<CharacterService>().ApplyAwardAsync(
                host.Service<SessionRegistry>().All().Single(s => s.Role?.Id == role),
                new AwardPlan(0, 0, [new AwardGrant(gear[0].Entry, 1)]))));
            returnedGuid = store.Load(role).Single(i => i.Pack == 2 && i.Grid == 0).Guid;
        }
        else await Unequip(bot, gear[0]);
        var returned = await Next(bot);
        Assert.True(returned.SyncAllMark); // invokes InitPackData, including the new pack's item callbacks
        Assert.Equal(gear[0].Entry, returned.PackInfo[2].All.Items[returnedGuid].ItemEntry);
        Assert.All(returned.PackInfo.Values, pack => Assert.NotEmpty(pack.All.SortedIndex));
        Assert.Equal(0, store.Load(role).Single(i => i.Guid == returnedGuid).Grid);

        await Unequip(bot, gear[1]);
        var added = await Next(bot);
        Assert.False(added.SyncAllMark); // an existing pack still uses its ordinary item-level increment
        Assert.Equal(gear[1].Entry, added.PackInfo[2].Inc.Items[gear[1].Guid].All.ItemEntry);
    }

    [Fact]
    public async Task EconomicPublicationDropsRetainedEmptyPacksBeforeTheFullSync()
    {
        await using var host = await SyntheticHost.StartAsync();
        await using var bot = host.NewBot();
        await bot.LoginAsync("alice", "pw");
        await bot.EnterGateAsync();
        long role = (await bot.CreateRoleAsync("Alice")).RoleBaseInfo.Id;
        var roles = host.Service<RoleStore>();
        var progress = roles.LoadProgress(role);
        progress.StarterKit = true;
        roles.SaveProgress(role, progress);
        var store = host.Service<ItemStore>();
        store.Insert(role, new InvItem { Entry = 1000000, Stack = 1, Pack = 1, Sort = 1 });
        await bot.EnterWorldAsync(role);
        Assert.DoesNotContain(2, (await Next(bot)).PackInfo.Keys);
        var session = host.Service<SessionRegistry>().All().Single(s => s.Role?.Id == role);
        var character = host.Service<CharacterService>();
        var loop = host.Service<GameLoop>();
        await loop.InvokeAsync(() => character.ClearBagAsync(session));
        Assert.False((await Next(bot)).SyncAllMark);

        // Exercise the publication boundary used after market/auction commits with a durable synthetic item.
        var gear = new InvItem { Entry = 2010121, Stack = 1, Pack = 2, Sort = 1, Attrs = new() { [3] = 1 } };
        await loop.InvokeAsync(async () =>
        {
            store.Insert(role, gear);
            session.Items.Add(gear);
            await character.CaptureEconomicItemsNotice(session)();
        });
        var update = await Next(bot);
        Assert.True(update.SyncAllMark);
        Assert.Equal(2, Assert.Single(update.PackInfo).Key);
        Assert.Equal(gear.Guid, Assert.Single(update.PackInfo[2].All.SortedIndex.Keys));
        Assert.Equal(gear.Entry, update.PackInfo[2].All.Items[gear.Guid].ItemEntry);
    }

    private static async Task Unequip(BotClient bot, InvItem item)
    {
        await bot.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq
        {
            ItemGUID = item.Guid, Pos = (EquipGridType)item.Grid, Opr = (EquipItemOprType)1,
        });
        var response = await bot.Inbox.WaitAsync(e => e.IsGame(CharacterService.EquipItemOpr), TimeSpan.FromSeconds(5));
        Assert.Equal(0, EquipItemOprResp.Parser.ParseFrom(response.Body).Result);
    }

    private static async Task<DBItemsInfoIncrement> Next(BotClient bot) => DBItemsInfoIncrement.Parser.ParseFrom(
        (await bot.Inbox.WaitAsync(e => e.IsGame(CharacterService.ItemsSync), TimeSpan.FromSeconds(5))).Body);
}
