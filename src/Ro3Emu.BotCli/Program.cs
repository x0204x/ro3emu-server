using System.Globalization;
using Ro3Emu.Bot;

// A bot player for live tests next to the real client.
// usage: Ro3Emu.BotCli <name> [server http base] [data dir]
// Then type commands on stdin (or pipe a file):
//   walk <x> <z>          walk there in steps (WALK every 0.1 s, like the client)
//   circle <r> <secs>     walk around the current spot
//   say <channel> <text>  chat in a group, e.g. say 4_1 hello  (map chat is "4_<sceneId>")
//   whisper <roleId> <text>
//   face <deg>
//   wait <secs>
//   party                 create a party;  invite <roleId>  invite into it;  friend <roleId>  send a friend request
//   approve <roleId>      as the party's leader, admit a player who asked to join (the client's Finder "Join")
//   teamaccept            accept the last party invitation (42005 → 42006);  leader <roleId>  hand the party over (42007);  teamleave (42001)
//   revive                the death panel's Revive (31013, empty like the client's)
//   send <msgId> <type> <json>   any game message as protobuf JSON, e.g. send 30451 DungeonTeamTransferReq {"targetGuid":"100012"}
//   gmop <op> <a,b,c>     a GM panel op (30300), e.g. gmop 35 <buffId>,0,<roleId> puts a buff on a role (0 = self)
//   ready                 answer the last match ready check (82636 → 82637);  confirm  agree to the party's dungeon (42033 → 30406)
//   like <roleId>         like a player's personal card (82554);  casual  sign up for Mock Battle (43504, activity 3304)
//   match <entryId>       join a match queue (82681, e.g. 2 = Survivor);  enter <sceneEntry>  ask to enter a dungeon (30400)
//   guildhome             enter the own guild's home (32256)
//   guildapply <guildId>  apply to a guild;  guildhelp <guildId>  respond to a guild being founded;  guildleave
//   guildagree <roleId>   admit an applicant (32224; needs the approval right)
//   tavernnew <name>      open a Tavern room (prints its guid);  tavern <guid>  enter one;  tavernleave
//   tavernmic [reason]    ask for a seat;  tavernaccept  accept the last Tavern invitation
//   taverngift <roleId> <itemId> [count]   give a drink to a member (1200801-1200804)
//   (the Tavern opens at level 36: "!lvl 36" on the bot's account first; chat in a room: say 10_<guid> <text>)
//   quit
// usage: Ro3Emu.BotCli voice-echo <server http base> <account> <password> [roleName] [--delay-ms 1000] [--data-dir <dir>] [--guild <guildId> [--channel 0] [--ask-mic]] [--tavern <guid>|new [--ask-mic]]
//   --guild: instead of a party, apply to that guild, wait to be accepted, sit in its voice channel and echo there
//   (when the server's voice relay is on); --ask-mic asks for the mic each time the channel turns to Order mode.
//   --tavern: enter that Tavern room (or open a new one) and echo in its voice; --ask-mic asks for a seat whenever
//   the room is in Manual or Order mode.
//   waits for a party invite, accepts it and echoes the party's voice back (see BotVoiceEcho)
if (args.Length > 0 && args[0] == "voice-echo")
{
    Environment.ExitCode = await VoiceEchoAsync(args[1..]);
    return;
}

string name = args.Length > 0 ? args[0] : "Botty";
string http = args.Length > 1 ? args[1] : "http://127.0.0.1:5380";
string dataDir = args.Length > 2 ? args[2] : FindDataDir();

await using var bot = new BotClient(http, File.ReadAllText(Path.Combine(dataDir, "gate_rsa_public.pem")))
{ OperatorToken = Ro3Emu.Server.Http.OperatorToken.Read(dataDir) };
await bot.LoginAsync(name.ToLowerInvariant(), "bot");
await bot.EnterGateAsync();
long roleId = bot.Roles.FirstOrDefault(r => r.Name == name)?.Id ?? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
var self = await bot.EnterWorldAsync(roleId);
float x = self.MovementInfo.Pos.X, y = self.MovementInfo.Pos.Y, z = self.MovementInfo.Pos.Z;
Console.WriteLine($"{name} ({roleId}) in world at {x:F1},{z:F1}");

// Heartbeat like the real client, so the session looks alive.
_ = Task.Run(async () =>
{
    while (true)
    {
        await Task.Delay(5000);
        try { await bot.SendGameAsync(1, new Romsg.Ping()); } catch { return; }
    }
});

// Follow scene switches (a warp, a dungeon entry) like the client: report the scene loaded and take the new position.
_ = Task.Run(async () =>
{
    while (true)
    {
        try
        {
            var sw = Romsg.SwitchSceneLineResp.Parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(Ro3Emu.Server.Game.MsgId.SwitchSceneLine), TimeSpan.FromDays(1))).Body);
            if (sw.Result != 0) continue;
            // Take a loading screen's time: an observer's client removes a leaving unit with a delay
            // (LC_UnitManager.CacheWaitRemoveUnit), so an instant re-entry would be merged into the unit being removed.
            await Task.Delay(2000);
            await bot.SendGameAsync(Ro3Emu.Server.Game.MsgId.SceneLoaded, new Romsg.SceneLoadedReq { SceneId = sw.SceneEntry });
            var moved = Romsg.EntitySelf.Parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(Ro3Emu.Server.Game.MsgId.EntitySelf), TimeSpan.FromSeconds(15))).Body);
            x = moved.Base.MovementInfo.Pos.X; y = moved.Base.MovementInfo.Pos.Y; z = moved.Base.MovementInfo.Pos.Z;
            Console.WriteLine($"{name} now in scene {sw.SceneEntry} at {x:F1},{z:F1}");
        }
        catch (Exception e) when (e is TimeoutException or InvalidOperationException)
        {
            if (e is InvalidOperationException) return;
        }
    }
});

// Take the server's own moves of this bot (31081: a camp spawn, a revive point) as the next walk's start, like the client.
_ = Task.Run(async () =>
{
    while (true)
    {
        try
        {
            var move = Romsg.MovementChangeNtf.Parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(31081)
                && Romsg.MovementChangeNtf.Parser.ParseFrom(e.Body).Uid == roleId, TimeSpan.FromDays(1))).Body);
            x = move.MovementInfo.Pos.X; y = move.MovementInfo.Pos.Y; z = move.MovementInfo.Pos.Z;
            Console.WriteLine($"{name} moved to {x:F1},{z:F1}");
        }
        catch (Exception e) when (e is TimeoutException or InvalidOperationException)
        {
            if (e is InvalidOperationException) return;
        }
    }
});

string? line;
while ((line = Console.ReadLine()) != null)
{
    var p = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
    if (p.Length == 0) continue;
    try
    {
        switch (p[0])
        {
            case "walk":
                await WalkTo(F(p[1]), F(p[2]));
                break;
            case "circle":
                float r = F(p[1]), secs = F(p[2]), cx = x, cz = z;
                for (float t = 0; t < secs; t += 0.1f)
                {
                    float a = t / secs * MathF.Tau;
                    await Step(cx + r * MathF.Cos(a), cz + r * MathF.Sin(a), stop: false);
                }
                await Step(cx, cz, stop: true);
                break;
            case "join":
                await bot.JoinChatAsync(p[1]);
                break;
            case "say":
                // Channel index from the group id prefix: "4_x" map, "@TGS#_1_x" world, ...
                int channel = int.TryParse(p[1].Replace("@TGS#_", "").Split('_')[0], out var c) ? c : 4;
                await bot.SendChatAsync(p[1], 2, p.Length > 2 ? p[2] : "", channel);
                break;
            case "whisper":
                await bot.SendChatAsync(p[1], 1, p.Length > 2 ? p[2] : "", channelIndex: 5);
                break;
            case "face":
                await bot.SendGameAsync(31020, new Romsg.ChangeFaceReq { Angle = F(p[1]) });
                break;
            case "wait":
                await Task.Delay(TimeSpan.FromSeconds(F(p[1])));
                break;
            case "party":
                await bot.SendGameAsync(42000, new Romsg.TeamCreateReq { TeamType = Romsg.TeamType.Five, TargetId = 1001 });
                break;
            case "invite":
                await bot.SendGameAsync(42004, new Romsg.InviteIntoTeamReq { Guid = long.Parse(p[1], CultureInfo.InvariantCulture) });
                break;
            case "approve":
                await bot.SendGameAsync(42018, new Romsg.ApplyListOperateReq
                { OperType = Romsg.ApplyListOperateType.Agree, TargetGuid = long.Parse(p[1], CultureInfo.InvariantCulture) });
                break;
            case "teamaccept":
                var teamInvite = Romsg.NotifyInvitee.Parser.ParseFrom(bot.Inbox.Snapshot().Last(e => e.IsGame(42005)).Body);
                await bot.SendGameAsync(42006, new Romsg.InviteeResp
                { TeamId = teamInvite.TeamTotalSimpleInfo.Info.TeamId, IsAgree = true, InviteGuid = teamInvite.InviteGuid });
                break;
            case "ready":
                var readyCheck = Romsg.MatchReadyNotify.Parser.ParseFrom(bot.Inbox.Snapshot().Last(e => e.IsGame(82636)).Body);
                await bot.SendGameAsync(82637, new Romsg.MatchReadyReq { MatchEntryId = readyCheck.MatchEntryId, PlayerId = roleId, IsReady = true });
                break;
            case "confirm":
                await bot.SendGameAsync(30406, new Romsg.ConfirmTeamDungeonReq { IsAgree = true });
                break;
            case "match":
                await bot.SendGameAsync(82681, new Romsg.StartMatchReq { MatchEntryId = int.Parse(p[1], CultureInfo.InvariantCulture) });
                break;
            case "enter":
                await bot.SendGameAsync(30400, new Romsg.EnterDungeonReq { SceneEntry = long.Parse(p[1], CultureInfo.InvariantCulture) });
                break;
            case "guildhome":
                await bot.SendGameAsync(32256, new Romsg.GuildEnterHomeReq());
                break;
            case "casual":
                await bot.SendGameAsync(43504, new Romsg.CommonDailyActivityReq { ActivityId = 3304, CasualBattleSignUp = new Romsg.CasualBattleSignUpReq() });
                break;
            case "like":
                await bot.SendGameAsync(82554, new Romsg.UpvotePersonalReq { RoleId = long.Parse(p[1], CultureInfo.InvariantCulture) });
                break;
            case "teamleave":
                await bot.SendGameAsync(42001, new Romsg.LeaveTeamReq());
                break;
            case "gmop":
                // The client's GM panel button: GmOpEnum and its comma-separated parameters (e.g. 35 = GmAddBuff).
                await bot.SendGameAsync(30300, new Romsg.GeneralGMProcReq
                { GmType = (Romsg.GmOpEnum)int.Parse(p[1], CultureInfo.InvariantCulture), ParamString = p.Length > 2 ? p[2] : "" });
                var gmReply = await bot.Inbox.WaitAsync(e => e.IsGame(30300), TimeSpan.FromSeconds(5));
                Console.WriteLine($"gm: {Romsg.GeneralGMProcRsp.Parser.ParseFrom(gmReply.Body).ErrInfo}");
                break;
            case "send":
                // Any game message: send <msgId> <romsg type> <protobuf JSON>, e.g. send 30451 DungeonTeamTransferReq {"targetGuid":"100012"}
                var rest = p[2].Split(' ', 2);
                var messageType = typeof(Romsg.Ping).Assembly.GetType("Romsg." + rest[0])
                    ?? throw new ArgumentException($"no message type Romsg.{rest[0]}");
                var descriptor = (Google.Protobuf.Reflection.MessageDescriptor)messageType.GetProperty("Descriptor")!.GetValue(null)!;
                await bot.SendGameAsync(uint.Parse(p[1], CultureInfo.InvariantCulture),
                    Google.Protobuf.JsonParser.Default.Parse(rest.Length > 1 ? rest[1] : "{}", descriptor));
                break;
            case "revive":
                await bot.SendGameAsync(31013, new Romsg.EntityReliveReq());
                break;
            case "leader":
                await bot.SendGameAsync(42007, new Romsg.TeamOperateReq
                { Oper = Romsg.TeamOperationType.TeamOperTypeChangeLeader, TargetGuid = long.Parse(p[1], CultureInfo.InvariantCulture) });
                break;
            case "friend":
                await bot.MicroAsync("FriendService", "apply", new() { ["Uid"] = roleId.ToString(), ["Target"] = p[1] });
                break;
            case "guildapply":
                await bot.SendGameAsync(32223, new Romsg.GuildEnterApplyReq { GuildIds = { long.Parse(p[1], CultureInfo.InvariantCulture) } });
                break;
            case "guildhelp":
                await bot.SendGameAsync(32220, new Romsg.GuildCreateHelperOpeReq { GuildIds = { long.Parse(p[1], CultureInfo.InvariantCulture) } });
                break;
            case "guildagree":
                await bot.SendGameAsync(32224, new Romsg.GuildEnterApplyOpeReq { Agree = true, PlayerIds = { long.Parse(p[1], CultureInfo.InvariantCulture) } });
                break;
            case "guildleave":
                await bot.SendGameAsync(32234, new Romsg.GuildMemberLeaveReq());
                break;
            case "tavernnew":
                Console.WriteLine($"Tavern room {await BotTavern.CreateAsync(bot, http, name.ToLowerInvariant(), p.Length > 1 ? string.Join(' ', p[1..]) : name, Console.WriteLine, CancellationToken.None)}");
                break;
            case "tavern":
                await BotTavern.EnterAsync(bot, http, name.ToLowerInvariant(), long.Parse(p[1], CultureInfo.InvariantCulture), Console.WriteLine, CancellationToken.None);
                break;
            case "tavernleave":
                await bot.SendGameAsync(Ro3Emu.Server.Game.TavernService.ExitMsg, new Romsg.ExitBarRoomReq());
                break;
            case "tavernmic":
                await bot.SendGameAsync(Ro3Emu.Server.Game.TavernService.ApplyMicMsg, new Romsg.ApplyOpenMicReq { Reason = p.Length > 1 ? string.Join(' ', p[1..]) : "" });
                break;
            case "tavernaccept":
                var invite = Romsg.InviteEnterBarRoomNotify.Parser.ParseFrom(bot.Inbox.Snapshot().Last(e => e.IsGame(Ro3Emu.Server.Game.TavernService.InviteNotify)).Body);
                await bot.SendGameAsync(Ro3Emu.Server.Game.TavernService.InviteResponseMsg,
                    new Romsg.ResponseInviteEnterBarRoomReq { BarRoomGuid = invite.BarRoomGuid, InviterGuid = invite.InviterGuid, IsAccept = true });
                break;
            case "taverngift":
                var gift = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                await bot.SendGameAsync(Ro3Emu.Server.Game.TavernService.GiftMsg, new Romsg.PresentGiftReq
                {
                    TargetGuid = long.Parse(gift[1], CultureInfo.InvariantCulture), GiftId = int.Parse(gift[2], CultureInfo.InvariantCulture),
                    GiftNum = gift.Length > 3 ? int.Parse(gift[3], CultureInfo.InvariantCulture) : 1,
                });
                break;
            case "quit":
                return;
        }
        Console.WriteLine($"ok: {line}");
    }
    catch (Exception e)
    {
        Console.WriteLine($"error: {line}: {e.Message}");
    }
}

async Task WalkTo(float tx, float tz)
{
    const float speed = 5.5f, tick = 0.1f;
    while (true)
    {
        float dx = tx - x, dz = tz - z, d = MathF.Sqrt(dx * dx + dz * dz);
        if (d <= speed * tick)
        {
            await Step(tx, tz, stop: true);
            return;
        }
        await Step(x + dx / d * speed * tick, z + dz / d * speed * tick, stop: false);
    }
}

async Task Step(float nx, float nz, bool stop)
{
    float dir = (MathF.Atan2(nx - x, nz - z) * 180f / MathF.PI + 360f) % 360f;
    x = nx;
    z = nz;
    await bot.SendGameAsync(31082, new Romsg.WalkReq { Pos = new Romsg.Vec3 { X = x, Y = y, Z = z }, Dir = dir, Face = dir, BStop = stop, SpeedRate = 1 });
    if (!stop) await Task.Delay(100);
}

static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

static async Task<int> VoiceEchoAsync(string[] args)
{
    var positional = new List<string>();
    int delayMs = 1000;
    string? dataDir = null;
    long guildId = 0;
    int channel = 0;
    bool askMic = false;
    string? tavern = null;
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--delay-ms" && i + 1 < args.Length) delayMs = int.Parse(args[++i], CultureInfo.InvariantCulture);
        else if (args[i] == "--data-dir" && i + 1 < args.Length) dataDir = args[++i];
        else if (args[i] == "--guild" && i + 1 < args.Length) guildId = long.Parse(args[++i], CultureInfo.InvariantCulture);
        else if (args[i] == "--channel" && i + 1 < args.Length) channel = int.Parse(args[++i], CultureInfo.InvariantCulture);
        else if (args[i] == "--ask-mic") askMic = true;
        else if (args[i] == "--tavern" && i + 1 < args.Length) tavern = args[++i];
        else positional.Add(args[i]);
    }
    if (positional.Count < 3 || delayMs < 0 || guildId < 0 || channel < 0)
    {
        Console.WriteLine("usage: Ro3Emu.BotCli voice-echo <server http base> <account> <password> [roleName] [--delay-ms 1000] [--data-dir <dir>] [--guild <guildId> [--channel 0] [--ask-mic]] [--tavern <guid>|new [--ask-mic]]");
        return 2;
    }
    string http = positional[0], account = positional[1], password = positional[2];
    string? wantedName = positional.Count > 3 ? positional[3] : null;
    dataDir ??= FindDataDir();

    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    static void Log(string line) => Console.WriteLine(line);

    await using var bot = new BotClient(http, File.ReadAllText(Path.Combine(dataDir, "gate_rsa_public.pem")))
    { OperatorToken = Ro3Emu.Server.Http.OperatorToken.Read(dataDir) };
    await bot.LoginAsync(account, password);
    await bot.EnterGateAsync();
    // An existing role: the one named, else the account's first; none yet: create one.
    var existing = wantedName != null ? bot.Roles.FirstOrDefault(r => r.Name == wantedName) : bot.Roles.FirstOrDefault();
    string roleName = existing?.Name ?? wantedName ?? "EchoBot";
    long roleId = existing?.Id ?? (await bot.CreateRoleAsync(roleName)).RoleBaseInfo.Id;
    await bot.EnterWorldAsync(roleId);
    Log($"{roleName} ({roleId}) in world");

    // Heartbeat like the real client, so the session looks alive.
    _ = Task.Run(async () =>
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(5000, stop.Token);
                await bot.SendGameAsync(1, new Romsg.Ping());
            }
            catch { return; }
        }
    });

    try
    {
        if (tavern != null) return await TavernEchoAsync(bot, http, account, roleId, roleName, tavern, askMic, delayMs, Log, stop.Token);
        if (guildId > 0)
        {
            await BotGuildVoice.JoinGuildAsync(bot, http, account, guildId, Log, stop.Token);
            using var sitting = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            var sit = BotGuildVoice.SitAsync(bot, roleId, channel, askMic, Log, sitting.Token);
            var room = $"2_{guildId}_{channel}";
            // The echo starts once the panel seat is taken (the relay admits only who sits in the channel) and ends
            // with it: a kick from the channel drops the voice connection too.
            var echo = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(1), sitting.Token);
                return await BotVoiceEcho.RunAsync(bot, roleId, room, TimeSpan.FromMilliseconds(delayMs), Log, sitting.Token);
            });
            var first = await Task.WhenAny(sit, echo);
            if (first == echo && echo.IsCompletedSuccessfully && echo.Result.Kind != BotVoiceEchoEndKind.Cancelled)
                Log("voice echo ended; still sitting in the channel");
            int joinCode = await sit;
            sitting.Cancel();
            // The seat decides the exit code; an echo that broke (network, a refused ticket) is only reported.
            try { await echo; }
            catch (OperationCanceledException) { }
            catch (Exception e) { Log($"voice echo failed: {e.GetType().Name}: {e.Message}"); }
            Log(joinCode == 0 ? "stopped" : "exiting");
            return joinCode == 0 ? 0 : 1;
        }
        long teamId = await BotVoiceEcho.AcceptPartyInviteAsync(bot, Log, stop.Token);
        var end = await BotVoiceEcho.RunAsync(bot, roleId, teamId, TimeSpan.FromMilliseconds(delayMs), Log, stop.Token);
        switch (end.Kind)
        {
            case BotVoiceEchoEndKind.Kicked when end.KickCode == Ro3Emu.Server.Voice.VoiceKickCode.RemovedFromRoom:
                Log("no longer in the team, exiting");
                return 0;
            case BotVoiceEchoEndKind.Kicked:
                Log($"kicked with code {end.KickCode}, exiting");
                return 1;
            case BotVoiceEchoEndKind.JoinRefused:
                Log($"voice join refused with code {end.KickCode}, exiting");
                return 1;
            case BotVoiceEchoEndKind.Closed:
                Log("voice connection closed, exiting");
                return 1;
            default:
                Log("stopped");
                return 0;
        }
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested)
    {
        Log("stopped");
        return 0;
    }
    catch (InvalidOperationException e)
    {
        Log($"error: {e.Message}");
        return 1;
    }
}

// The Tavern variant of voice-echo: in the room while the echo runs on its voice (the room's guid in decimal).
static async Task<int> TavernEchoAsync(BotClient bot, string http, string account, long roleId, string roleName, string tavern,
    bool askMic, int delayMs, Action<string> log, CancellationToken stop)
{
    long room = tavern == "new"
        ? await BotTavern.CreateAsync(bot, http, account, roleName + "'s room", log, stop)
        : long.Parse(tavern, CultureInfo.InvariantCulture);
    if (tavern != "new") await BotTavern.EnterAsync(bot, http, account, room, log, stop);
    using var staying = CancellationTokenSource.CreateLinkedTokenSource(stop);
    var stay = BotTavern.StayAsync(bot, roleId, room, askMic, log, staying.Token);
    var echo = BotVoiceEcho.RunAsync(bot, roleId, room.ToString(CultureInfo.InvariantCulture), TimeSpan.FromMilliseconds(delayMs), log, staying.Token);
    await Task.WhenAny(stay, echo);
    staying.Cancel();
    try { await Task.WhenAll(stay, echo); }
    catch (OperationCanceledException) { }
    catch (Exception e) { log($"tavern echo ended: {e.GetType().Name}: {e.Message}"); }
    log("stopped");
    return 0;
}

static string FindDataDir()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return Path.Combine(dir.FullName, "data");
    }
    return "data";
}
