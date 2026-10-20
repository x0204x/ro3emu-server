using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Bot;

/// <summary>
/// A bot member for manual guild voice tests: it applies to a guild, waits until it is accepted, sits in one guild
/// voice channel and, with <c>askMic</c>, asks for the mic whenever the channel is in Order mode, so one player can
/// try the leader's side (mode, approve or refuse, kick, block list) alone. Channel state only: the relay audio for
/// guild channels is not wired yet.
/// </summary>
public static class BotGuildVoice
{
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Poll = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Applies to <paramref name="guildId"/> and returns once the bot is a member (a leader with auto-accept takes it at
    /// once). A bot below the guild's join level is raised to 34 through the local server's GM endpoint first, which
    /// only answers on a server playing on this PC.
    /// </summary>
    public static async Task JoinGuildAsync(BotClient bot, string httpBase, string account, long guildId, Action<string> log, CancellationToken ct)
    {
        using var member = bot.Inbox.Expect(e => e.IsGame(GuildService.Full) && GuildFullInfoNtf.Parser.ParseFrom(e.Body).Guid == guildId);
        int result = await ApplyAsync(bot, guildId, ct);
        if (result == ErrorCodes.GuildJoinLevel)
        {
            log("below the guild's join level: raising to 34 with the GM command");
            using var http = new HttpClient { BaseAddress = new Uri(httpBase), Timeout = ReplyTimeout };
            (await http.PostAsync($"/ro3emu/gm?account={Uri.EscapeDataString(account)}", new StringContent("!lvl 34"), ct)).EnsureSuccessStatusCode();
            result = await ApplyAsync(bot, guildId, ct);
        }
        if (result == ErrorCodes.AlreadyInGuild) { log($"already in guild {guildId}"); return; }
        if (result != 0) throw new InvalidOperationException($"applying to guild {guildId} failed (result {result})");
        log($"applied to guild {guildId}; waiting to be accepted");
        while (true)
        {
            try { await member.WaitAsync(Poll, ct); break; }
            catch (TimeoutException) { }
        }
        log($"in guild {guildId}");
    }

    private static async Task<int> ApplyAsync(BotClient bot, long guildId, CancellationToken ct)
    {
        using var reply = bot.Inbox.Expect(e => e.IsGame(GuildService.Apply));
        await bot.SendGameAsync(GuildService.Apply, new GuildEnterApplyReq { GuildIds = { guildId } });
        var resp = GuildEnterApplyResp.Parser.ParseFrom((await reply.WaitAsync(ReplyTimeout, ct)).Body);
        return resp.ApplyResult.FirstOrDefault()?.Result ?? -1;
    }

    /// <summary>
    /// Joins voice channel <paramref name="channel"/> and stays until kicked or <paramref name="ct"/>; returns the
    /// join error code, or 0 after a kick or a stop. Logs every change of the member list, the mode and its own mic.
    /// With <paramref name="askMic"/> it asks once each time the channel turns to Order mode (a refusal is final for
    /// that round, so it never asks in a loop).
    /// </summary>
    public static async Task<int> SitAsync(BotClient bot, long roleId, int channel, bool askMic, Action<string> log, CancellationToken ct)
    {
        var mode = GuildSpeakAuthorityType.Free;
        using (var reply = bot.Inbox.Expect(e => e.IsGame(GuildVoiceService.JoinMsg)))
        {
            await bot.SendGameAsync(GuildVoiceService.JoinMsg, new GuildVoiceChannelJoinReq { ChannelIndex = channel });
            var joined = GuildVoiceChannelJoinRsp.Parser.ParseFrom((await reply.WaitAsync(ReplyTimeout, ct)).Body);
            if (joined.ErrorCode != 0) { log($"joining voice channel {channel} failed (code {joined.ErrorCode})"); return joined.ErrorCode; }
            log($"in guild voice channel {channel}");
            mode = await ReactAsync(bot, roleId, joined.ChannelInfo, mode, askMic, log, ct);
        }
        while (!ct.IsCancellationRequested)
        {
            BotEvent e;
            try { e = await bot.Inbox.WaitAsync(x => x.IsGame(GuildVoiceService.UpdateNotify) || x.IsGame(GuildVoiceService.KickedNotify), Poll).WaitAsync(ct); }
            catch (TimeoutException) { continue; }
            if (e.IsGame(GuildVoiceService.KickedNotify)) { log($"kicked from voice channel {channel}"); return 0; }
            mode = await ReactAsync(bot, roleId, GuildVoiceChannelUpdateNotify.Parser.ParseFrom(e.Body).ChannelInfo, mode, askMic, log, ct);
        }
        return 0;
    }

    /// <summary>Logs the channel and asks for the mic on a switch to Order mode; returns the mode seen.</summary>
    private static async Task<GuildSpeakAuthorityType> ReactAsync(BotClient bot, long roleId, GuildVoiceChannelInfo info,
        GuildSpeakAuthorityType before, bool askMic, Action<string> log, CancellationToken ct)
    {
        var self = info.Members.FirstOrDefault(m => m.PlayerId == roleId);
        bool canSpeak = self?.HasSpeakPermission == true;
        log($"channel {info.ChannelIndex}: {info.Members.Count} member(s), {info.SpeakMode} mode, my mic {(canSpeak ? "allowed" : "not allowed")}");
        if (!askMic || before == GuildSpeakAuthorityType.Order || info.SpeakMode != GuildSpeakAuthorityType.Order || canSpeak) return info.SpeakMode;
        using var reply = bot.Inbox.Expect(e => e.IsGame(GuildVoiceService.ApplyMicMsg));
        await bot.SendGameAsync(GuildVoiceService.ApplyMicMsg, new GuildVoiceChannelApplyMicReq { ChannelIndex = info.ChannelIndex, Reason = "echo bot" });
        int code = GuildVoiceChannelApplyMicRsp.Parser.ParseFrom((await reply.WaitAsync(ReplyTimeout, ct)).Body).ErrorCode;
        log(code == 0 ? "asked for the mic" : $"asking for the mic failed (code {code})");
        return info.SpeakMode;
    }
}
