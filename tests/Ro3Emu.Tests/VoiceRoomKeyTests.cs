using Ro3Emu.Server.Voice;

namespace Ro3Emu.Tests;

/// <summary>
/// The relay's room keys (src/Ro3Emu.Server/Voice/VoiceRoomKey.cs): a party is its team id as the client sends it,
/// a guild voice channel is "2_&lt;guildId&gt;_&lt;index&gt;" (LC_VoiceHelper.GetGuildVoiceChannelId); the two never share
/// a key.
/// </summary>
public sealed class VoiceRoomKeyTests
{
    [Fact]
    public void APartyIsItsTeamId()
    {
        Assert.True(VoiceRoomKey.TryParse("16777217", out var key));
        Assert.True(key.IsTeam);
        Assert.False(key.IsGuild);
        Assert.Equal(16777217, key.Value);
    }

    [Theory]
    [InlineData("2_1_0", 1L, 0)]
    [InlineData("2_1_2", 1L, 2)]
    [InlineData("2_987654321_1", 987654321L, 1)]
    public void AGuildChannelKeepsItsGuildAndIndex(string channel, long guild, int index)
    {
        Assert.True(VoiceRoomKey.TryParse(channel, out var key));
        Assert.True(key.IsGuild);
        Assert.False(key.IsTeam);
        Assert.Equal((guild, index), (key.GuildId, key.ChannelIndex));
        Assert.Equal(key, VoiceRoomKey.Guild(guild, index));
    }

    [Fact]
    public void GuildChannelsAndPartiesNeverShareAKey()
    {
        var keys = new HashSet<long>();
        for (long guild = 1; guild <= 50; guild++)
            for (int index = 0; index < 3; index++)
                Assert.True(keys.Add(VoiceRoomKey.Guild(guild, index).Value));
        Assert.All(keys, k => Assert.True(k < 0));
        Assert.True(VoiceRoomKey.TryParse("1", out var party));
        Assert.DoesNotContain(party.Value, keys);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("+5")]
    [InlineData(" 5")]
    [InlineData("abc")]
    [InlineData("2_")]
    [InlineData("2_1")]
    [InlineData("2_0_0")]      // no guild 0
    [InlineData("2_1_-1")]
    [InlineData("2_1_64")]     // past the channel range
    [InlineData("2_1_0_0")]
    [InlineData("3_1_0")]      // another talk type (tavern rooms are not served)
    [InlineData("2_x_0")]
    [InlineData("2_99999999999999999999_0")]
    public void AnythingElseIsNoRoom(string channel) => Assert.False(VoiceRoomKey.TryParse(channel, out _));
}
