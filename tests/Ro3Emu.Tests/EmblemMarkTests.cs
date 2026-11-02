using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class EmblemTests
{
    private async Task Feed(BotClient alice, long guid, int stacks)
    {
        await Gm("alice", $"!item 1257002 {stacks}");
        var req = new UpgradeEmblemReq { EmblemGuid = guid };
        req.ConsumeItems[1257002] = stacks;
        await alice.SendGameAsync(EmblemService.Upgrade, req);
        Assert.Equal(0, (await Next(alice, EmblemService.Upgrade, UpgradeEmblemResp.Parser)).Result);
    }

    private async Task<long> GrantEcho(BotClient alice, int entry)
    {
        await Gm("alice", $"!item {entry}");
        var change = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser, m => m.PackInfo.TryGetValue(5, out var pack)
            && (pack.All != null ? pack.All.Items.Values : pack.Inc.Items.Values.Select(v => v.All)).Any(i => i?.ItemEntry == entry));
        var pack = change.PackInfo[5];
        return (pack.All != null ? pack.All.Items.Values : pack.Inc.Items.Values.Select(v => v.All)).Single(i => i?.ItemEntry == entry).ItemGuid;
    }

    private async Task UntilOffline(long role)
    {
        var sessions = _app.Services.GetRequiredService<SessionRegistry>();
        for (var until = DateTime.UtcNow.AddSeconds(10); sessions.All().Any(s => s.Role?.Id == role);)
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("the role did not leave");
            await Task.Delay(10);
        }
    }

    private static void AssertRealMark(long mark) => Assert.InRange(mark, 1001, 1004);

    /// <summary>
    /// A 0 in _kInitialMark is a random element, not an empty one: UI_Sys_SoulUpgradePanelCtrl.PlayChaosMarkUpgradeSound
    /// reads the lit slot's mark from the item after the level-up and plays that mark's sound.
    /// </summary>
    [ClientDataFact]
    public async Task ARandomMarkSlotLightsWithARealMarkAndKeepsIt()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!quest done 10103040");
        await Next(alice, FunctionService.FunctionOpenSync, FunctionOpenInfoIncrement.Parser, m => m.FunctionOpenInfos.ContainsKey(55));
        long guid = await GrantEcho(alice, 1850101); // [[5,0],[10,0],[15,0],[20,0]], the quests' echo
        var store = _app.Services.GetRequiredService<ItemStore>();
        long role = (long)alice.Self!.Uuid;
        Assert.False(store.Load(role).Single(i => i.Guid == guid).Attrs!.ContainsKey(1021)); // below level 5 nothing is lit

        await Feed(alice, guid, 3); // level 11: slots 1 and 2
        var first = store.Load(role).Single(i => i.Guid == guid).Attrs!;
        AssertRealMark(first[1021]);
        AssertRealMark(first[1022]);
        Assert.False(first.ContainsKey(1023));

        await Feed(alice, guid, 3); // level 15: slot 3, and the lit marks stay as rolled
        var second = store.Load(role).Single(i => i.Guid == guid).Attrs!;
        Assert.Equal(first[1021], second[1021]);
        Assert.Equal(first[1022], second[1022]);
        AssertRealMark(second[1023]);
    }

    [ClientDataFact]
    public async Task AFixedMarkStaysAndTheRandomSlotAfterItRolls()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!quest done 10103040");
        await Next(alice, FunctionService.FunctionOpenSync, FunctionOpenInfoIncrement.Parser, m => m.FunctionOpenInfos.ContainsKey(55));
        long guid = await GrantEcho(alice, 1850111); // [[5,1001],[10,0],[15,0],[20,0]], from the GM tool's box 1300045
        await Feed(alice, guid, 3);
        var change = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
            m => m.PackInfo.ContainsKey(5) && m.PackInfo[5].Inc?.Items.ContainsKey(guid) == true
                && m.PackInfo[5].Inc.Items[guid].Inc?.ItemInfos.ContainsKey(1021) == true);
        var infos = change.PackInfo[5].Inc.Items[guid].Inc.ItemInfos;
        Assert.Equal(1001, infos[1021].All.LongVal);
        AssertRealMark(infos[1022].All.LongVal);
    }

    /// <summary>Echoes levelled before random marks were rolled hold 0 in their lit slots: login gives them their marks once.</summary>
    [ClientDataFact]
    public async Task LoginRollsTheLitMarksOfEchoesLevelledWithoutThem()
    {
        long role, random, fixedFirst, young;
        var accounts = _app.Services.GetRequiredService<AccountStore>();
        await using (var alice = await InWorld("Alice"))
        {
            role = (long)alice.Self!.Uuid;
            random = await GrantEcho(alice, 1850101);
            fixedFirst = await GrantEcho(alice, 1850111);
            young = await GrantEcho(alice, 1850121);
        }
        await UntilOffline(role);
        FixtureSql.Execute(accounts, "UPDATE item SET attrs=$a WHERE guid=$g",
            ("$a", "{\"1001\":10,\"1002\":0,\"1003\":20100,\"1021\":0,\"1022\":0}"), ("$g", random));
        FixtureSql.Execute(accounts, "UPDATE item SET attrs=$a WHERE guid=$g",
            ("$a", "{\"1001\":10,\"1002\":0,\"1003\":20100,\"1021\":1001,\"1022\":0}"), ("$g", fixedFirst));
        FixtureSql.Execute(accounts, "UPDATE item SET attrs=$a WHERE guid=$g",
            ("$a", "{\"1001\":3,\"1002\":0,\"1003\":1900}"), ("$g", young));

        long[] marks;
        await using (var again = await InWorld("Alice", create: false))
        {
            var items = (await Next(again, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser)).PackInfo[5].All.Items;
            marks = [items[random].ItemInfos[1021].LongVal, items[random].ItemInfos[1022].LongVal, items[fixedFirst].ItemInfos[1022].LongVal];
            Assert.All(marks, AssertRealMark);
            Assert.Equal(1001, items[fixedFirst].ItemInfos[1021].LongVal);
            Assert.False(items[random].ItemInfos.ContainsKey(1023));
            Assert.False(items[young].ItemInfos.ContainsKey(1021));
        }
        await UntilOffline(role);
        await using var third = await InWorld("Alice", create: false);
        var kept = (await Next(third, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser)).PackInfo[5].All.Items;
        Assert.Equal(marks, new[] { kept[random].ItemInfos[1021].LongVal, kept[random].ItemInfos[1022].LongVal, kept[fixedFirst].ItemInfos[1022].LongVal });
    }

    [ClientDataFact]
    public async Task GmLevelledEchoGetsRealMarks()
    {
        await using var alice = await InWorld("Alice");
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == "alice");
        string reply = await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
            _app.Services.GetRequiredService<EmblemService>().GmCreateAsync(session, 1850101, 20));
        Assert.Contains("level 20", reply);
        var attrs = _app.Services.GetRequiredService<ItemStore>().Load(session.Role!.Id).Single(i => i.Entry == 1850101).Attrs!;
        for (int slot = 0; slot < 4; slot++) AssertRealMark(attrs[1021 + slot]);
    }
}
