using Ro3Emu.Panel;

namespace Ro3Emu.Tests;

public sealed class PanelMailAudienceTests
{
    [Fact]
    public void AudienceCopiesTheResolvedIdsAndNamesBeforeTheInputListCanChange()
    {
        var target = new PanelMailTarget("everyone", "", OnlineOnly: false, MinLevel: 0);
        var input = new List<PanelMailRecipient> { new(17, "Original") };
        var audience = new PanelMailAudience(target, input);
        input[0] = new PanelMailRecipient(18, "Replacement");
        input.Add(new PanelMailRecipient(19, "Late"));

        Assert.Equal(1, audience.Count);
        Assert.Equal(new PanelMailRecipient(17, "Original"), Assert.Single(audience.Recipients));
        Assert.True(Assert.IsAssignableFrom<ICollection<PanelMailRecipient>>(audience.Recipients).IsReadOnly);
    }
}
