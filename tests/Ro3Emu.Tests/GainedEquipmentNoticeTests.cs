using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// The 31322 notice of an NPC purchase (the exchange ShopService.BuyAsync commits) and of a mail claim describes
/// each new piece of equipment as its bag item, as the drop notice does: LC_EquipmentHelper.CanFastEquip offers to
/// wear a piece only when it has an instance id, an appraised kEquipInfo and an iScore.
/// </summary>
public sealed class GainedEquipmentNoticeTests : MailTestsHost
{
    private const int Equipment = 3990002, Potion = 1000000, Zeny = 1;
    private const int ScoreKey = 2, AppraiseKey = 3; // ItemAttrTypeScore, ItemAttrTypeAppraise

    protected override string GameDataDir => SyntheticData.Dir;

    private GameSession Session(long role) =>
        app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == role);

    private static Task<GetRewardNotify> Notice(BotClient bot) =>
        Next(bot, CharacterService.GetRewardNotify, GetRewardNotify.Parser, n => n.Rewards.Any(r => r.ItemEntry == Equipment));

    private static void AssertBagPiece(GetRewardNotify notice, GameSession session)
    {
        var piece = Assert.Single(notice.Rewards, r => r.ItemEntry == Equipment);
        var bagPiece = Assert.Single(session.Items, i => i.Entry == Equipment);
        Assert.NotEqual(0, bagPiece.Guid);
        Assert.Equal(bagPiece.Guid, piece.ItemGuid);
        Assert.True(piece.ItemInfos.ContainsKey(ScoreKey));
        Assert.True(piece.ItemInfos.ContainsKey(AppraiseKey));
    }

    [Fact]
    public async Task NpcPurchaseNoticeDescribesBoughtEquipmentAsItsBagItem()
    {
        var (bot, role) = await InWorld("Buyer");
        await using var _ = bot;
        var session = Session(role);
        var character = app.Services.GetRequiredService<CharacterService>();
        // ShopService.BuyAsync's exchange: currency cost, configured goods, the notice left to the exchange.
        await app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
            character.ExchangeAsync(session, [(Zeny, 10L)], [(Equipment, 1), (Potion, 2)]));
        var notice = await Notice(bot);
        AssertBagPiece(notice, session);
        var potion = Assert.Single(notice.Rewards, r => r.ItemEntry == Potion);
        Assert.Equal((2, 0L), (potion.Stack, potion.ItemGuid));
    }

    [Fact]
    public async Task MailClaimNoticeDescribesClaimedEquipmentAsItsBagItem()
    {
        var (bot, role) = await InWorld("Reader");
        await using var _ = bot;
        string id = await Mail.SendRawAsync(role, "Gear", "Body", [new(Equipment, 1), new(Potion, 2)]);
        Assert.Equal(0, (await Claim(bot, id)).Result);
        var notice = await Notice(bot);
        AssertBagPiece(notice, Session(role));
        Assert.Equal(2, Assert.Single(notice.Rewards, r => r.ItemEntry == Potion).Stack);
    }
}
