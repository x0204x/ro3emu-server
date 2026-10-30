using System.Text.Json;
using Ro3Emu.Panel;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class PanelToolsTests
{
    [Fact]
    public async Task AConfirmedMailAudienceCannotGainANewCharacterBeforeDelivery()
    {
        await using var first = await _host.InWorldAsync("FixedRecipient");
        var tools = _host.Service<IPanelAdminTools>();
        var target = new PanelMailTarget("everyone", "", OnlineOnly: true, MinLevel: 0);
        var audience = await tools.ResolveMailAudienceAsync(target);
        Assert.Equal(RoleId(first), Assert.Single(audience.Recipients).Id);

        // 使用真正的名冊變動插在確認與寄送之間；之後解析已是兩人，原快照仍只能寄一人。
        await using var late = await _host.InWorldAsync("LateRecipient");
        Assert.Equal(2, (await tools.MailRecipientsAsync(target)).Count);
        var result = await tools.SendMailAsync(Admin, audience,
            new PanelMail("Fixed audience", "自造測試郵件", "GM", [new PanelMailAttachment(Item, 3)]));

        Assert.Equal(new PanelMailResult(1, 0), result);
        var roles = _host.Service<RoleStore>();
        var firstBox = JsonSerializer.Deserialize<MailService.Mailbox>(roles.LoadMailbox(RoleId(first))!)!;
        var letter = Assert.Single(firstBox.Letters, l => l.Title == "Fixed audience");
        Assert.Equal([new MailService.Attachment(Item, 3)], letter.Attachments);
        if (roles.LoadMailbox(RoleId(late)) is { } bytes)
        {
            var lateBox = JsonSerializer.Deserialize<MailService.Mailbox>(bytes)!;
            Assert.DoesNotContain(lateBox.Letters, l => l.Title == "Fixed audience");
        }
    }
}
