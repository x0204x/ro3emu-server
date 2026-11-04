using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>82332 on the synthetic fixture: how many items one purchase of a commodity grants.</summary>
public sealed class ShopGoodsTests
{
    // Fixture shop class 900: 90001 shows its item x4 for 300 Zeny, 90002 shows it x1 for 100 Zeny.
    private const int Bundle = 90001, Single = 90002, Goods = 1200000;

    private static async Task<BuyCommodityResp> Buy(BotClient bot, int commodity, int count)
    {
        await bot.SendGameAsync(ShopService.BuyCommodity, new BuyCommodityReq { CommodityId = commodity, Count = count });
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(ShopService.BuyCommodity)
            && BuyCommodityResp.Parser.ParseFrom(e.Body).CommodityId == commodity, TimeSpan.FromSeconds(8));
        return BuyCommodityResp.Parser.ParseFrom(e.Body);
    }

    [Fact]
    public async Task ACommodityShownAsABundleGrantsTheWholeBundle()
    {
        await using var host = await SyntheticHost.StartAsync();
        await using var alice = await host.InWorldAsync("Alice");
        var character = host.Service<CharacterService>();
        var session = host.Service<SessionRegistry>().All().Single(s => s.Role != null);

        Assert.Equal(0, (await Buy(alice, Bundle, 1)).Result);
        Assert.Equal(4, character.CountOf(session, Goods));
        Assert.Equal(0, (await Buy(alice, Bundle, 2)).Result);
        Assert.Equal(12, character.CountOf(session, Goods));
        // The price is per purchase, whatever it holds: 3 x 300 + 100 spends the starting 1000 Zeny.
        Assert.Equal(0, (await Buy(alice, Single, 1)).Result);
        Assert.Equal(13, character.CountOf(session, Goods));
        Assert.Equal(0, character.CountOf(session, 1));
    }
}
