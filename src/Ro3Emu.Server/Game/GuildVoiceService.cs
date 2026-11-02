using System.Text.Json;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Guild voice channels (MG_GuildVoiceChannel 83700-83723; client LC_DAL_GuildVoice / LC_GuildVoiceServer /
/// LC_GuildVoiceHelper): GuildSettingConfig.GuildVoiceChannelCount channels per guild, 0-based on the wire, each in
/// Free mode (anyone talks) or Order mode (the leader, holders of the GuildVoice right 23 and members given the mic).
/// The client rebuilds its panel from every 83709 UpdateNotify (a receiver missing from members leaves), so every
/// change is pushed in full to everybody in the channel; 83710/83712/83713 are empty handlers in this client build
/// and 83714/83715 only log, so they are not sent. In memory only: channels start empty after a restart.
///
/// Leaving the guild or rank changes come in through <see cref="GuildService.MembershipChanged"/>, and edited rank
/// rights through <see cref="GuildService.RightsChanged"/>. A once-a-second sweep also drops offline or replaced
/// sessions and members no longer in that guild (there is no logout event to hook). The audio
/// itself is the voice relay's: room "2_&lt;guildId&gt;_&lt;index&gt;" admits who <see cref="IsSeated"/> and holds the mic
/// off for whoever may not speak (<see cref="MaySpeak"/>).
///
/// Invites (UI_Sys_Chat_InviteSetup*): the window asks 83720 for whom it may list, a row's Invite sends 83707 for one
/// player, the invitee gets 83716 as a main-screen tip and answers 83708 (accept joins the channel). Cooldown and the
/// "no invitations for 10 minutes" box are GuildSettingConfig values, kept per invitee as GuildVoiceInviteState is.
/// 83717 has no handler and 83719 is never sent by this client. 83718 despite its name is the chat typing state
/// (<see cref="WorldCoreService"/>).
/// </summary>
public sealed class GuildVoiceService(GameData data, GuildStore store, GuildService guilds, SessionRegistry sessions,
    GameLoop loop, TimeProvider clock, IOptions<ServerOptions> options) : Messaging.IMessageModule, IGameTicker
{
    public const uint JoinMsg = 83700, LeaveMsg = 83701, SetSpeakModeMsg = 83702, ApplyMicMsg = 83703, HandleApplyMsg = 83704;
    public const uint KickMsg = 83705, UnbanMsg = 83706, UpdateNotify = 83709, KickedNotify = 83711;
    public const uint BlackListMsg = 83721, SetSpeakPermissionMsg = 83722, AllMemberCountMsg = 83723;
    public const uint InviteMsg = 83707, InviteResponseMsg = 83708, InviteNotify = 83716, NotInCooldownMsg = 83720;

    /// <summary>enum_GuildPermission.GuildVoice (Lua_GameDefine), GuildPermissionConfig row 23.</summary>
    public const int VoiceRight = 23;

    /// <summary>The client sets no limit on the mic request reason; this bound is ours.</summary>
    public const int MaxReasonLength = 50;

    private const int DefaultChannelCount = 3;
    private const long DefaultBlockMs = 86_400_000;
    private const long DefaultInviteCooldownMs = 15_000, DefaultInviteRejectMs = 600_000;

    /// <summary>The client invites one player per click; the bound on a longer list is ours.</summary>
    private const int MaxInviteTargets = 100;
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(1);

    private sealed class Seat(GameSession session, long joinMs)
    {
        public GameSession Session { get; } = session;
        public long JoinMs { get; } = joinMs;
        public bool Granted { get; set; }
    }

    private sealed record Application(long Role, string Reason, long AtMs);

    private sealed class Channel(int index)
    {
        public int Index { get; } = index;
        public GuildSpeakAuthorityType Mode { get; set; } = GuildSpeakAuthorityType.Free;
        public Dictionary<long, Seat> Members { get; } = [];
        public List<Application> Applications { get; } = [];
        public Dictionary<long, long> Blocked { get; } = []; // role -> blocked until, unix ms
    }

    private readonly object _sync = new();
    private readonly Dictionary<long, Channel[]> _byGuild = [];
    private readonly Dictionary<long, (long Guild, int Index)> _where = [];
    private readonly HashSet<long> _rechecking = []; // roles with a sweep recheck already posted
    // invitee -> (last invited, declines invites until), unix ms: GuildVoiceAllData.memberVoiceStates
    private readonly Dictionary<long, (long InvitedMs, long RejectUntilMs)> _inviteStates = [];
    private TimeSpan _lastSweep;

    public void Map(Messaging.MessageRoutes routes)
    {
        routes.Map(AllMemberCountMsg, "guildVoice", (s, _, _) => s.SendGameAsync(AllMemberCountMsg, Counts(s)));
        routes.Map(JoinMsg, "guildVoice", (s, _, body) => JoinAsync(s, GuildVoiceChannelJoinReq.Parser.ParseFrom(body.Span)));
        routes.Map(LeaveMsg, "guildVoice", (s, _, body) => LeaveAsync(s, GuildVoiceChannelLeaveReq.Parser.ParseFrom(body.Span)));
        routes.Map(SetSpeakModeMsg, "guildVoice", (s, _, body) => SetModeAsync(s, GuildVoiceChannelSetSpeakModeReq.Parser.ParseFrom(body.Span)));
        routes.Map(ApplyMicMsg, "guildVoice", (s, _, body) => ApplyAsync(s, GuildVoiceChannelApplyMicReq.Parser.ParseFrom(body.Span)));
        routes.Map(HandleApplyMsg, "guildVoice", (s, _, body) => HandleApplyAsync(s, GuildVoiceChannelHandleApplyReq.Parser.ParseFrom(body.Span)));
        routes.Map(SetSpeakPermissionMsg, "guildVoice", (s, _, body) => SetPermissionAsync(s, GuildVoiceChannelSetSpeakPermissionReq.Parser.ParseFrom(body.Span)));
        routes.Map(KickMsg, "guildVoice", (s, _, body) => KickAsync(s, GuildVoiceChannelKickPlayerReq.Parser.ParseFrom(body.Span)));
        routes.Map(UnbanMsg, "guildVoice", (s, _, body) => UnbanAsync(s, GuildVoiceChannelUnbanPlayerReq.Parser.ParseFrom(body.Span)));
        routes.Map(BlackListMsg, "guildVoice", (s, _, body) => BlackListAsync(s, GuildVoiceChannelGetBlackListReq.Parser.ParseFrom(body.Span)));
        routes.Map(NotInCooldownMsg, "guildVoice", (s, _, _) => s.SendGameAsync(NotInCooldownMsg, Invitable(s)));
        routes.Map(InviteMsg, "guildVoice", (s, _, body) => InviteAsync(s, GuildVoiceChannelInviteReq.Parser.ParseFrom(body.Span)));
        routes.Map(InviteResponseMsg, "guildVoice", (s, _, body) => InviteResponseAsync(s, GuildVoiceChannelInviteResponseReq.Parser.ParseFrom(body.Span)));
    }

    /// <summary>A member's guild or rank changed (GuildService.MembershipChanged, inside its commit): checked on the loop.</summary>
    public void OnMembershipChanged(GameSession session)
    {
        if (session.Role is { } role) loop.Post(() => RecheckAsync(role.Id));
    }

    /// <summary>登出或切換角色時，只移除目前連線所屬的席位，再同步頻道。</summary>
    public async Task LeaveRoleAsync(GameSession session)
    {
        if (session.Role is not { } role) return;
        GuildStore.State? guild;
        Channel? left;
        lock (_sync)
        {
            if (!OwnsSeat(role.Id, session) || !_where.TryGetValue(role.Id, out var at)) return;
            guild = store.Get(at.Guild);
            left = Unseat(role.Id);
        }
        if (guild != null && left != null) await PushAsync(guild, left);
    }

    /// <summary>Rank rights were committed: refresh each occupied channel once, even when no rank changed.</summary>
    public void OnRightsChanged(long guildId) => loop.Post(async () =>
    {
        if (store.Get(guildId) is not { } guild) return;
        Channel[] occupied;
        lock (_sync)
            occupied = _byGuild.TryGetValue(guildId, out var channels)
                ? [.. channels.Where(c => c.Members.Count > 0)] : [];
        foreach (var channel in occupied) await PushAsync(guild, channel);
    });

    public void Tick(GameTime time)
    {
        if (time.Now - _lastSweep < SweepInterval) return;
        _lastSweep = time.Now;
        List<long> seated;
        lock (_sync)
        {
            PruneBlocks();
            PruneInviteStates();
            seated = [.. _where.Keys];
        }
        if (seated.Count == 0) return;
        var online = sessions.All().Where(s => s.Role != null).ToDictionary(s => s.Role!.Id);
        foreach (long role in seated)
            if (!OwnsSeat(role, online.GetValueOrDefault(role)) || store.ForRole(role)?.Id != Where(role)?.Guild)
            {
                lock (_sync) if (!_rechecking.Add(role)) continue;
                loop.Post(async () =>
                {
                    try { await RecheckAsync(role); }
                    finally { lock (_sync) _rechecking.Remove(role); }
                });
            }
    }

    /// <summary>For the relay: <paramref name="role"/> sits in channel <paramref name="index"/> of <paramref name="guildId"/>.</summary>
    public bool IsSeated(long role, long guildId, int index)
    {
        lock (_sync) return _where.TryGetValue(role, out var at) && at.Guild == guildId && at.Index == index;
    }

    /// <summary>For the relay: false only for a seated member of a channel in Order mode who has not been given the
    /// mic (the leader and holders of the GuildVoice right always may).</summary>
    public bool MaySpeak(long role)
    {
        lock (_sync)
        {
            if (!_where.TryGetValue(role, out var at) || !_byGuild.TryGetValue(at.Guild, out var channels)) return true;
            var channel = channels[at.Index];
            if (channel.Mode == GuildSpeakAuthorityType.Free || !channel.Members.TryGetValue(role, out var seat)) return true;
            return store.Get(at.Guild) is not { } guild || CanSpeak(guild, channel, role, seat);
        }
    }

    // ---- requests ----

    private GuildVoiceChannelGetAllMemberCountRsp Counts(GameSession s)
    {
        var reply = new GuildVoiceChannelGetAllMemberCountRsp();
        if (Guild(s) is not { } guild) { reply.ErrorCode = ErrorCodes.GuildVoiceNotInGuild; return reply; }
        lock (_sync)
            foreach (var channel in Channels(guild.Id))
                reply.ChannelCounts.Add(new ChannelMemberCount { ChannelIndex = channel.Index, MemberCount = channel.Members.Count });
        return reply;
    }

    private Task JoinAsync(GameSession s, GuildVoiceChannelJoinReq req) => SeatAsync(s, req.ChannelIndex,
        (code, info) => s.SendGameAsync(JoinMsg, new GuildVoiceChannelJoinRsp { ErrorCode = code, ChannelInfo = info }));

    /// <summary>Seats the caller in <paramref name="index"/> and answers through <paramref name="reply"/> (83700, or 83708
    /// for an accepted invite: both carry the channel the client joins with _handleJoinVoiceChannel).</summary>
    private async Task SeatAsync(GameSession s, int index, Func<int, GuildVoiceChannelInfo?, Task> reply)
    {
        long self = s.Role!.Id;
        var guild = Guild(s);
        int code;
        GuildVoiceChannelInfo? info = null;
        Channel? joined = null;
        Channel? left = null;
        GuildStore.State? formerGuild = null;
        lock (_sync)
        {
            // A fresh login may join before the next sweep. Remove its predecessor's seat and mic request
            // in the same transition as the new join, before any notification can suspend this command.
            if (_where.TryGetValue(self, out var previous) && !OwnsSeat(self, s))
            {
                formerGuild = store.Get(previous.Guild);
                left = Unseat(self);
            }
            code = guild == null ? ErrorCodes.GuildVoiceNotInGuild
                : !ValidIndex(index) ? ErrorCodes.GuildVoiceBadChannel
                : _where.TryGetValue(self, out var at) ? (at.Guild == guild.Id && at.Index == index
                    ? ErrorCodes.GuildVoiceAlreadyInChannel : ErrorCodes.GuildVoiceInAnotherChannel)
                : IsBlocked(Channels(guild.Id)[index], self) ? ErrorCodes.GuildVoiceBlocked
                : ChannelFull(Channels(guild.Id)[index]) ? ErrorCodes.Unknown
                : 0;
            if (code == 0)
            {
                joined = Channels(guild!.Id)[index];
                joined.Members[self] = new Seat(s, NowMs());
                _where[self] = (guild.Id, index);
                info = Info(guild, joined);
            }
        }
        await reply(code, info);
        if (left != null && left != joined && formerGuild != null) await PushAsync(formerGuild, left);
        if (joined != null) await PushAsync(guild!, joined, except: self);
    }

    private async Task LeaveAsync(GameSession s, GuildVoiceChannelLeaveReq req)
    {
        long self = s.Role!.Id;
        var guild = Guild(s);
        Channel? left = null;
        lock (_sync)
            if (guild != null && OwnsSeat(self, s) && _where.TryGetValue(self, out var at)
                && at.Guild == guild.Id && at.Index == req.ChannelIndex)
                left = Unseat(self);
        await s.SendGameAsync(LeaveMsg, new GuildVoiceChannelLeaveRsp
        {
            ErrorCode = guild == null ? ErrorCodes.GuildVoiceNotInGuild : left == null ? ErrorCodes.GuildVoiceNotInChannel : 0,
        });
        if (left != null) await PushAsync(guild!, left);
    }

    private async Task SetModeAsync(GameSession s, GuildVoiceChannelSetSpeakModeReq req)
    {
        var (code, guild, channel) = Managed(s, req.ChannelIndex);
        if (code == 0)
            lock (_sync) SwitchMode(channel!, req.SpeakMode);
        await s.SendGameAsync(SetSpeakModeMsg, new GuildVoiceChannelSetSpeakModeRsp { ErrorCode = code });
        if (code == 0) await PushAsync(guild!, channel!);
    }

    private async Task ApplyAsync(GameSession s, GuildVoiceChannelApplyMicReq req)
    {
        long self = s.Role!.Id;
        var (code, guild, channel) = Seated(s, req.ChannelIndex);
        string reason = req.Reason ?? "";
        lock (_sync)
            if (code == 0)
            {
                // Seated() let go of the lock: the seat may be gone by now (a Leave or a Kick in between).
                code = !channel!.Members.TryGetValue(self, out var seat) ? ErrorCodes.GuildVoiceNotInChannel
                    : channel.Mode == GuildSpeakAuthorityType.Free ? ErrorCodes.GuildVoiceFreeMode
                    : IsManager(guild!, self) ? ErrorCodes.GuildVoiceManagerNeedsNoApply
                    : seat.Granted ? ErrorCodes.GuildVoiceAlreadyCanSpeak
                    : channel.Applications.Any(a => a.Role == self) ? ErrorCodes.GuildVoiceAlreadyApplied
                    : reason.Length > MaxReasonLength ? ErrorCodes.GuildVoiceReasonTooLong
                    : 0;
                if (code == 0) channel.Applications.Add(new Application(self, reason, NowMs()));
            }
        await s.SendGameAsync(ApplyMicMsg, new GuildVoiceChannelApplyMicRsp { ErrorCode = code });
        if (code == 0) await PushAsync(guild!, channel!);
    }

    private async Task HandleApplyAsync(GameSession s, GuildVoiceChannelHandleApplyReq req)
    {
        var (code, guild, channel) = Managed(s, req.ChannelIndex);
        lock (_sync)
            if (code == 0)
            {
                code = channel!.Mode != GuildSpeakAuthorityType.Order ? ErrorCodes.GuildVoiceNotOrderMode
                    : channel.Applications.RemoveAll(a => a.Role == req.TargetPlayerId) == 0 ? ErrorCodes.GuildVoiceNotApplied
                    : 0;
                if (code == 0 && req.IsAgree && channel.Members.TryGetValue(req.TargetPlayerId, out var seat)) seat.Granted = true;
            }
        await s.SendGameAsync(HandleApplyMsg, new GuildVoiceChannelHandleApplyRsp { ErrorCode = code });
        if (code == 0) await PushAsync(guild!, channel!);
    }

    /// <summary>The permission panel's Save (UI_Sys_Chat_PermissionSetupWidgetCtrl._onClickSave): the dropdown's mode
    /// and the changed mics in one request. This client never sends 83702, so the mode is taken from here.</summary>
    private async Task SetPermissionAsync(GameSession s, GuildVoiceChannelSetSpeakPermissionReq req)
    {
        var (code, guild, channel) = Managed(s, req.ChannelIndex);
        lock (_sync)
            if (code == 0)
            {
                // All or nothing: the batch is checked before any seat changes.
                foreach (var item in req.PermissionItems)
                {
                    if (!channel!.Members.ContainsKey(item.TargetPlayerId)) { code = ErrorCodes.GuildVoiceNotInChannel; break; }
                    if (!item.GrantPermission && IsLeader(guild!, item.TargetPlayerId)) { code = ErrorCodes.GuildVoiceLeaderCannotMute; break; }
                }
                if (code == 0) SwitchMode(channel!, req.SpeakMode); // first, so mics given in this Save survive its reset
                if (code == 0)
                    foreach (var item in req.PermissionItems)
                    {
                        channel!.Members[item.TargetPlayerId].Granted = item.GrantPermission;
                        channel.Applications.RemoveAll(a => a.Role == item.TargetPlayerId);
                    }
            }
        await s.SendGameAsync(SetSpeakPermissionMsg, new GuildVoiceChannelSetSpeakPermissionRsp { ErrorCode = code });
        if (code == 0) await PushAsync(guild!, channel!);
    }

    private async Task KickAsync(GameSession s, GuildVoiceChannelKickPlayerReq req)
    {
        long self = s.Role!.Id;
        var (code, guild, channel) = Managed(s, req.ChannelIndex);
        lock (_sync)
            if (code == 0)
            {
                code = req.TargetPlayerId == self ? ErrorCodes.GuildVoiceCannotKickSelf
                    : IsLeader(guild!, req.TargetPlayerId) ? ErrorCodes.GuildVoiceCannotKickLeader
                    : !channel!.Members.ContainsKey(req.TargetPlayerId) ? ErrorCodes.GuildVoiceNotInChannel
                    : 0;
                if (code == 0)
                {
                    Unseat(req.TargetPlayerId);
                    channel!.Blocked[req.TargetPlayerId] = NowMs() + Setting("GuildVoiceChannelBlacklistDuration", DefaultBlockMs);
                }
            }
        await s.SendGameAsync(KickMsg, new GuildVoiceChannelKickPlayerRsp { ErrorCode = code });
        if (code != 0) return;
        if (Online(req.TargetPlayerId) is { } target)
            await target.SendGameAsync(KickedNotify, new GuildVoiceChannelKickedNotify { ChannelIndex = channel!.Index });
        await PushAsync(guild!, channel!);
    }

    private async Task UnbanAsync(GameSession s, GuildVoiceChannelUnbanPlayerReq req)
    {
        var (code, _, channel) = Managed(s, req.ChannelIndex, mustBeSeated: false);
        lock (_sync)
            if (code == 0 && !(channel!.Blocked.Remove(req.TargetPlayerId, out long until) && until > NowMs()))
                code = ErrorCodes.GuildVoiceNotBlocked;
        await s.SendGameAsync(UnbanMsg, new GuildVoiceChannelUnbanPlayerRsp { ErrorCode = code, TargetPlayerId = req.TargetPlayerId });
    }

    private async Task BlackListAsync(GameSession s, GuildVoiceChannelGetBlackListReq req)
    {
        var (code, _, channel) = Managed(s, req.ChannelIndex, mustBeSeated: false);
        var reply = new GuildVoiceChannelGetBlackListRsp { ErrorCode = code };
        lock (_sync)
            if (code == 0)
            {
                long now = NowMs();
                foreach (var (role, until) in channel!.Blocked)
                    if (until > now) reply.BlackList[role] = until;
            }
        await s.SendGameAsync(BlackListMsg, reply);
    }

    // ---- invites ----

    /// <summary>
    /// 83720 when the invite window opens (UI_Sys_Chat_InviteSetupWindowCtrl._requestNotInCdPlayers): the window lists
    /// exactly these players (LC_GuildVoiceHelper.RequestInvitableGuildMemberListWithCallback reads CanInvitePlayerIds).
    /// (inferred) Everyone an invite would reach: online members of the caller's guild but the caller, seated in no
    /// channel, past their invite cooldown and not declining invites. The message name only says "not in cooldown".
    /// </summary>
    private GuildVoiceGetNotInCdPlayerResp Invitable(GameSession s)
    {
        var reply = new GuildVoiceGetNotInCdPlayerResp();
        if (Guild(s) is not { } guild) { reply.ErrorCode = ErrorCodes.GuildVoiceNotInGuild; return reply; }
        var online = OnlineRoles(guild.Members.Keys);
        lock (_sync)
        {
            long now = NowMs(), cooldown = InviteCooldownMs();
            foreach (long role in guild.Members.Keys.Order())
                if (role != s.Role!.Id && online.ContainsKey(role) && InviteRefusal(role, now, cooldown) == 0) reply.UserIds.Add(role);
        }
        return reply;
    }

    /// <summary>
    /// 83707 from a row's Invite (UI_Sys_Chat_InviteSetup_ListWidgetCtrl._onClickInvite: the caller's joined channel and
    /// one player). Every invitee that may be invited gets 83716 and starts its cooldown; the reply lists each invitee with
    /// its code, and the client marks those with 0 as invited (LastInvitedPlayerIds) after its "invitation sent" tip.
    /// (inferred) Any member seated in the channel may invite: the member panel shows Invite to everybody and keeps only
    /// Set to managers (UI_Sys_Chat_MemberWidgetCtrl.RefreshView). (inferred) When nobody could be invited the reply
    /// carries the first refusal, so the client shows the reason instead of "invitation sent".
    /// </summary>
    private async Task InviteAsync(GameSession s, GuildVoiceChannelInviteReq req)
    {
        var (code, guild, channel) = Seated(s, req.ChannelIndex);
        // 這是語音服務容量保護；尚無原服專用的滿員錯誤碼，沿用通用失敗。
        lock (_sync)
            if (code == 0 && ChannelFull(channel!)) code = ErrorCodes.Unknown;
        var reply = new GuildVoiceChannelInviteRsp { ErrorCode = code };
        List<GameSession> invited = [];
        long cooldown = InviteCooldownMs();
        List<long> targets = [.. req.TargetPlayerIds.Distinct().Take(MaxInviteTargets)];
        if (code == 0 && targets.Count == 0) reply.ErrorCode = ErrorCodes.Parameter;
        else if (code == 0)
        {
            var online = OnlineRoles(targets);
            lock (_sync)
            {
                long now = NowMs();
                foreach (long target in targets)
                {
                    online.TryGetValue(target, out var to);
                    int refusal = !guild!.Members.ContainsKey(target) ? ErrorCodes.GuildVoiceTargetNotInGuild
                        : to == null ? ErrorCodes.GuildVoiceTargetOffline
                        : InviteRefusal(target, now, cooldown);
                    reply.PlayersResult.Add(new GuildVoiceChannelInviteFailedPlayer { PlayerId = target, ErrorCode = refusal });
                    if (refusal != 0) continue;
                    _inviteStates[target] = (now, _inviteStates.GetValueOrDefault(target).RejectUntilMs);
                    invited.Add(to!);
                }
            }
            if (invited.Count == 0) reply.ErrorCode = reply.PlayersResult[0].ErrorCode;
        }
        await s.SendGameAsync(InviteMsg, reply);
        // timeoutSeconds is read as milliseconds (OnGuildVoiceChannelInviteNotify divides it by 1000 for the tip's bar).
        // (inferred) The tip lasts as long as the cooldown, so the next invite of the same player never stacks on a live tip.
        var notify = new GuildVoiceChannelInviteNotify { ChannelIndex = req.ChannelIndex, InviterPlayerId = s.Role!.Id, TimeoutSeconds = (int)cooldown };
        foreach (var to in invited)
            if (ReferenceEquals(Online(to.Role!.Id), to)) await to.SendGameAsync(InviteNotify, notify);
    }

    /// <summary>
    /// 83708 from the invite tip (UI_Sys_Main_TeamTipsCellWidgetCtrl._onGuildVoiceChannelInviteResponse): Join, Ignore,
    /// or the tip running out, which sends a refusal. rejectAll is the tip's box "Do not accept Guild Voice invitations
    /// for 10 minutes" (UIWords 34115): refused from now for GuildVoiceChannelInviteRejectDuration, (inferred) also when
    /// the box is ticked on Join. Accepting joins the channel as 83700 does ((inferred) no pending invite is checked:
    /// anybody in the guild may join anyway). A refusal gets no reply: OnGuildVoiceChannelInviteResponse joins with the
    /// reply's channelInfo on every errorCode 0 and would index the missing channel.
    /// </summary>
    private async Task InviteResponseAsync(GameSession s, GuildVoiceChannelInviteResponseReq req)
    {
        long self = s.Role!.Id;
        if (req.RejectAll)
            lock (_sync)
                _inviteStates[self] = (_inviteStates.GetValueOrDefault(self).InvitedMs,
                    NowMs() + Setting("GuildVoiceChannelInviteRejectDuration", DefaultInviteRejectMs));
        if (!req.IsAccept) return;
        await SeatAsync(s, req.ChannelIndex, (code, info) =>
            s.SendGameAsync(InviteResponseMsg, new GuildVoiceChannelInviteResponseRsp { ErrorCode = code, ChannelInfo = info }));
    }

    /// <summary>Why <paramref name="role"/> cannot be invited now, 0 when it can; under the lock.</summary>
    private int InviteRefusal(long role, long now, long cooldown) =>
        _where.ContainsKey(role) ? ErrorCodes.GuildVoiceTargetInChannel
        : !_inviteStates.TryGetValue(role, out var state) ? 0
        : state.RejectUntilMs > now ? ErrorCodes.GuildVoiceInviteDeclined
        : state.InvitedMs + cooldown > now ? ErrorCodes.GuildVoiceInviteCooldown
        : 0;

    /// <summary>Forgets invite states whose cooldown and refusal both ran out; under the lock.</summary>
    private void PruneInviteStates()
    {
        if (_inviteStates.Count == 0) return;
        long now = NowMs(), cooldown = InviteCooldownMs();
        foreach (var (role, state) in _inviteStates.ToList())
            if (state.InvitedMs + cooldown <= now && state.RejectUntilMs <= now) _inviteStates.Remove(role);
    }

    private long InviteCooldownMs() => Setting("GuildVoiceChannelInviteCooldown", DefaultInviteCooldownMs);

    /// <summary>The sessions of those of <paramref name="roles"/> who are in the game.</summary>
    private Dictionary<long, GameSession> OnlineRoles(IEnumerable<long> roles)
    {
        var wanted = roles.ToHashSet();
        Dictionary<long, GameSession> online = [];
        foreach (var session in sessions.All())
            if (session.Role is { } role && wanted.Contains(role.Id)) online.TryAdd(role.Id, session);
        return online;
    }

    // ---- membership ----

    /// <summary>Drops <paramref name="role"/> from its channel when its session is gone/replaced or it left the guild; on a
    /// rank change (it stays) the channel is pushed again, since who may speak in Order mode can have changed.</summary>
    private async Task RecheckAsync(long role)
    {
        var at = Where(role);
        if (at == null || store.Get(at.Value.Guild) is not { } guild) { await DropAsync(role); return; }
        bool stays = OwnsSeat(role, Online(role)) && store.ForRole(role)?.Id == guild.Id;
        Channel channel;
        lock (_sync)
        {
            channel = Channels(guild.Id)[at.Value.Index];
            if (!stays) Unseat(role);
        }
        await PushAsync(guild, channel);
    }

    /// <summary>The guild itself is gone: the seat just goes.</summary>
    private Task DropAsync(long role)
    {
        lock (_sync) Unseat(role);
        return Task.CompletedTask;
    }

    /// <summary>
    /// A switch of mode starts a fresh round: nobody holds a granted mic and no request is open. Setting the mode the
    /// channel already has (the panel sends it with every Save) changes nothing. Under the lock.
    /// </summary>
    private static void SwitchMode(Channel channel, GuildSpeakAuthorityType requested)
    {
        var mode = requested == GuildSpeakAuthorityType.Order ? GuildSpeakAuthorityType.Order : GuildSpeakAuthorityType.Free;
        if (channel.Mode == mode) return;
        channel.Mode = mode;
        foreach (var seat in channel.Members.Values) seat.Granted = false;
        channel.Applications.Clear();
    }

    /// <summary>Removes <paramref name="role"/> from its channel (with its mic request); under the lock.</summary>
    private Channel? Unseat(long role)
    {
        if (!_where.Remove(role, out var at) || !_byGuild.TryGetValue(at.Guild, out var channels)) return null;
        var channel = channels[at.Index];
        channel.Members.Remove(role);
        channel.Applications.RemoveAll(a => a.Role == role);
        return channel;
    }

    // ---- state helpers ----

    /// <summary>The caller's channel <paramref name="index"/> when the caller sits in it.</summary>
    private (int Code, GuildStore.State? Guild, Channel? Channel) Seated(GameSession s, int index)
    {
        if (Guild(s) is not { } guild) return (ErrorCodes.GuildVoiceNotInGuild, null, null);
        if (!ValidIndex(index)) return (ErrorCodes.GuildVoiceBadChannel, guild, null);
        lock (_sync)
            return Where(s.Role!.Id) is { } at && at.Guild == guild.Id && at.Index == index && OwnsSeat(s.Role.Id, s)
                ? (0, guild, Channels(guild.Id)[index])
                : (ErrorCodes.GuildVoiceNotInChannel, guild, null);
    }

    /// <summary>As <see cref="Seated"/>, and the caller is the leader or holds the GuildVoice right.</summary>
    private (int Code, GuildStore.State? Guild, Channel? Channel) Managed(GameSession s, int index, bool mustBeSeated = true)
    {
        var (code, guild, channel) = mustBeSeated ? Seated(s, index) : Unseated(s, index);
        if (code != 0) return (code, guild, channel);
        return IsManager(guild!, s.Role!.Id) ? (0, guild, channel) : (ErrorCodes.GuildVoiceNoPermission, guild, channel);
    }

    /// <summary>Block-list reads and unblocking: a manager need not sit in the channel.</summary>
    private (int Code, GuildStore.State? Guild, Channel? Channel) Unseated(GameSession s, int index)
    {
        if (Guild(s) is not { } guild) return (ErrorCodes.GuildVoiceNotInGuild, null, null);
        if (!ValidIndex(index)) return (ErrorCodes.GuildVoiceBadChannel, guild, null);
        lock (_sync) return (0, guild, Channels(guild.Id)[index]);
    }

    private Channel[] Channels(long guildId)
    {
        if (!_byGuild.TryGetValue(guildId, out var channels))
            _byGuild[guildId] = channels = [.. Enumerable.Range(0, ChannelCount()).Select(i => new Channel(i))];
        return channels;
    }

    private (long Guild, int Index)? Where(long role)
    {
        lock (_sync) return _where.TryGetValue(role, out var at) ? at : null;
    }

    private bool OwnsSeat(long role, GameSession? session)
    {
        lock (_sync)
            return session != null && _where.TryGetValue(role, out var at)
                && _byGuild[at.Guild][at.Index].Members.TryGetValue(role, out var seat)
                && ReferenceEquals(seat.Session, session);
    }

    private bool IsBlocked(Channel channel, long role)
    {
        if (!channel.Blocked.TryGetValue(role, out long until)) return false;
        if (until > NowMs()) return true;
        channel.Blocked.Remove(role); // expired
        return false;
    }

    /// <summary>Forgets blocks that ran out, and the channels of guilds with nobody seated and nobody blocked; under
    /// the lock.</summary>
    private void PruneBlocks()
    {
        long now = NowMs();
        foreach (var (guildId, channels) in _byGuild.ToList())
        {
            foreach (var channel in channels)
                foreach (var (role, until) in channel.Blocked.ToList())
                    if (until <= now) channel.Blocked.Remove(role);
            if (channels.All(c => c.Members.Count == 0 && c.Blocked.Count == 0 && c.Mode == GuildSpeakAuthorityType.Free))
                _byGuild.Remove(guildId);
        }
    }

    private bool ValidIndex(int index) => index >= 0 && index < ChannelCount();

    private static bool IsLeader(GuildStore.State guild, long role) => guild.Members.TryGetValue(role, out var m) && m.Official == 1;

    private bool IsManager(GuildStore.State guild, long role) => guilds.HasRight(guild, role, VoiceRight);

    private bool CanSpeak(GuildStore.State guild, Channel channel, long role, Seat seat) =>
        channel.Mode == GuildSpeakAuthorityType.Free || seat.Granted || IsManager(guild, role);

    /// <summary>The full channel state the client replaces its own with; under the lock.</summary>
    private GuildVoiceChannelInfo Info(GuildStore.State guild, Channel channel)
    {
        var info = new GuildVoiceChannelInfo { ChannelIndex = channel.Index, SpeakMode = channel.Mode, TotalMemberCount = channel.Members.Count };
        foreach (var (role, seat) in channel.Members.OrderBy(m => m.Value.JoinMs))
            info.Members.Add(new GuildVoiceChannelMember { PlayerId = role, JoinTime = seat.JoinMs, HasSpeakPermission = CanSpeak(guild, channel, role, seat) });
        foreach (var a in channel.Applications)
            info.ApplyList.Add(new ApplyInfo { PlayerId = a.Role, Reason = a.Reason, ApplyTime = a.AtMs });
        return info;
    }

    /// <summary>83709 with the whole channel to everybody in it (optionally but one, who just got it in a reply).</summary>
    private async Task PushAsync(GuildStore.State guild, Channel channel, long except = 0)
    {
        GuildVoiceChannelUpdateNotify notify;
        List<(long Role, GameSession Session)> members;
        lock (_sync)
        {
            notify = new GuildVoiceChannelUpdateNotify { ChannelIndex = channel.Index, ChannelInfo = Info(guild, channel) };
            members = [.. channel.Members.Where(m => m.Key != except).Select(m => (m.Key, m.Value.Session))];
        }
        foreach (var (role, session) in members)
            if (ReferenceEquals(Online(role), session)) await session.SendGameAsync(UpdateNotify, notify);
    }

    private GuildStore.State? Guild(GameSession s) => s.Role == null ? null : store.ForRole(s.Role.Id);

    private GameSession? Online(long role) => sessions.All().FirstOrDefault(s => s.Role?.Id == role);

    private bool ChannelFull(Channel channel) => channel.Members.Count >= options.Value.Voice.MaxRoomSize;

    private long NowMs() => clock.GetUtcNow().ToUnixTimeMilliseconds();

    private int ChannelCount() => (int)Math.Clamp(Setting("GuildVoiceChannelCount", DefaultChannelCount), 1, 16);

    private long Setting(string key, long fallback)
    {
        if (!data.Table("GuildSettingConfig").TryGetValue(key, out var row)
            || !row.TryGetProperty("_kValue", out var value)) return fallback;
        return value.ValueKind == JsonValueKind.Number ? (long)(GameData.Number(value) ?? fallback) : fallback;
    }
}
