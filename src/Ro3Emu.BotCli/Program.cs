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
//   guildapply <guildId>  apply to a guild;  guildhelp <guildId>  respond to a guild being founded;  guildleave
//   quit
// usage: Ro3Emu.BotCli voice-echo <server http base> <account> <password> [roleName] [--delay-ms 1000] [--data-dir <dir>] [--guild <guildId> [--channel 0] [--ask-mic]]
//   --guild: instead of a party, apply to that guild, wait to be accepted, sit in its voice channel and echo there
//   (when the server's voice relay is on); --ask-mic asks for the mic each time the channel turns to Order mode.
//   waits for a party invite, accepts it and echoes the party's voice back (see BotVoiceEcho)
if (args.Length > 0 && args[0] == "voice-echo")
{
    Environment.ExitCode = await VoiceEchoAsync(args[1..]);
    return;
}

string name = args.Length > 0 ? args[0] : "Botty";
string http = args.Length > 1 ? args[1] : "http://127.0.0.1:5380";
string dataDir = args.Length > 2 ? args[2] : FindDataDir();

await using var bot = new BotClient(http, File.ReadAllText(Path.Combine(dataDir, "gate_rsa_public.pem")));
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
            case "friend":
                await bot.MicroAsync("FriendService", "apply", new() { ["Uid"] = roleId.ToString(), ["Target"] = p[1] });
                break;
            case "guildapply":
                await bot.SendGameAsync(32223, new Romsg.GuildEnterApplyReq { GuildIds = { long.Parse(p[1], CultureInfo.InvariantCulture) } });
                break;
            case "guildhelp":
                await bot.SendGameAsync(32220, new Romsg.GuildCreateHelperOpeReq { GuildIds = { long.Parse(p[1], CultureInfo.InvariantCulture) } });
                break;
            case "guildleave":
                await bot.SendGameAsync(32234, new Romsg.GuildMemberLeaveReq());
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
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--delay-ms" && i + 1 < args.Length) delayMs = int.Parse(args[++i], CultureInfo.InvariantCulture);
        else if (args[i] == "--data-dir" && i + 1 < args.Length) dataDir = args[++i];
        else if (args[i] == "--guild" && i + 1 < args.Length) guildId = long.Parse(args[++i], CultureInfo.InvariantCulture);
        else if (args[i] == "--channel" && i + 1 < args.Length) channel = int.Parse(args[++i], CultureInfo.InvariantCulture);
        else if (args[i] == "--ask-mic") askMic = true;
        else positional.Add(args[i]);
    }
    if (positional.Count < 3 || delayMs < 0 || guildId < 0 || channel < 0)
    {
        Console.WriteLine("usage: Ro3Emu.BotCli voice-echo <server http base> <account> <password> [roleName] [--delay-ms 1000] [--data-dir <dir>] [--guild <guildId> [--channel 0] [--ask-mic]]");
        return 2;
    }
    string http = positional[0], account = positional[1], password = positional[2];
    string? wantedName = positional.Count > 3 ? positional[3] : null;
    dataDir ??= FindDataDir();

    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    static void Log(string line) => Console.WriteLine(line);

    await using var bot = new BotClient(http, File.ReadAllText(Path.Combine(dataDir, "gate_rsa_public.pem")));
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

static string FindDataDir()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return Path.Combine(dir.FullName, "data");
    }
    return "data";
}
