using System.Globalization;

namespace Ro3Emu.Server.Voice;

/// <summary>
/// A relay room, keyed by one number: a party is its team id as the client sends it (team ids start at 1 &lt;&lt; 24, so
/// always positive); a guild voice channel "2_&lt;guildId&gt;_&lt;index&gt;" (LC_VoiceHelper.GetGuildVoiceChannelId, 2 =
/// enum_TalkType.Guild) is the negative -(guildId * <see cref="MaxGuildChannels"/> + index + 1), so the two kinds can
/// never share a key. Tavern rooms (talk type 3) are not served.
/// </summary>
public readonly record struct VoiceRoomKey(long Value)
{
    /// <summary>Room for 64 channels per guild; the game configures 3 (GuildSettingConfig.GuildVoiceChannelCount).</summary>
    public const int MaxGuildChannels = 64;

    public bool IsTeam => Value > 0;
    public bool IsGuild => Value < 0;

    /// <summary>The guild of a guild room (0 for a party).</summary>
    public long GuildId => IsGuild ? (-Value - 1) / MaxGuildChannels : 0;

    /// <summary>The 0-based panel channel of a guild room (0 for a party).</summary>
    public int ChannelIndex => IsGuild ? (int)((-Value - 1) % MaxGuildChannels) : 0;

    public static VoiceRoomKey Guild(long guildId, int index) => new(-(guildId * MaxGuildChannels + index + 1));

    public static bool TryParse(string? channel, out VoiceRoomKey key)
    {
        key = default;
        if (string.IsNullOrEmpty(channel)) return false;
        if (long.TryParse(channel, NumberStyles.None, CultureInfo.InvariantCulture, out long team))
        {
            if (team <= 0) return false;
            key = new VoiceRoomKey(team);
            return true;
        }
        var parts = channel.Split('_');
        if (parts.Length != 3 || parts[0] != "2") return false;
        if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long guild)
            || guild <= 0 || guild > (long.MaxValue - MaxGuildChannels) / MaxGuildChannels) return false;
        if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index >= MaxGuildChannels) return false;
        key = Guild(guild, index);
        return true;
    }
}
