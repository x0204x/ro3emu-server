using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// GM commands, two ways in: "!command" lines typed in any chat channel (replies go to the typist only, as the
/// client's marquee, BROADCAST_NOTIFY 83270 with a raw string) and the client's own GM panel (GENERAL_PROC_GM 30300,
/// ). ServerOptions.GmAccounts limits who may use them; empty means everyone unless
/// ServerOptions.GmOpenWhenEmpty is false (a Production host, docs/guide/gm-access.md). The loopback HTTP /ro3emu/gm keeps
/// "empty = everyone" (<see cref="Allowed(ServerOptions, string?, bool)"/>).
/// </summary>
public sealed class GmCommands(IOptions<ServerOptions> options, GameData data, SessionRegistry sessions, CharacterService character, SceneService scene, TaskService tasks, IServiceProvider services, ILogger<GmCommands> log)
{
    public const uint BroadcastNotify = 83270;
    public const uint GeneralProcGm = 30300;

    /// <summary>The HUD asks with this prefix + a counter whether to show the GM button; "&lt;request&gt;:1" = yes.</summary>
    private const string PanelProbe = "ro3-gm-panel-capability-v1:";

    /// <summary>GmOpEnum values the emulator implements.</summary>
    private const int OpNone = 0, OpAddItem = 1, OpRemoveItem = 2, OpAddAttrPnt = 8, OpSetLevel = 24, OpAddExp = 25, OpJumpMap = 31,
        OpClearPack = 66, OpFullHpMp = 78, OpSetJob = 101;

    private delegate Task Command(GameSession session, string[] args);

    private Dictionary<string, (string Usage, Command Run)>? _commands;

    /// <summary>Hook for systems that come later to add their commands.</summary>
    public void Register(string name, string usage, Func<GameSession, string[], Task> run)
    {
        Commands()[name] = (usage, (s, a) => run(s, a));
    }

    public bool IsCommand(string text) => text.StartsWith('!') && text.Length > 1;

    /// <summary>Shared account allowlist for every GM wire entry point.</summary>
    public bool Allowed(GameSession session) => Allowed(options.Value, session.Account, loopbackTool: false);

    /// <summary>
    /// A listed account always may. An empty list means everyone for the loopback HTTP tool (unchanged), and in game
    /// whatever <see cref="ServerOptions.GmOpenWhenEmpty"/> says (unset = everyone, as for tests and local play).
    /// </summary>
    internal static bool Allowed(ServerOptions options, string? account, bool loopbackTool)
    {
        var allowed = options.GmAccounts;
        if (allowed.Length == 0) return loopbackTool || options.GmOpenWhenEmpty != false;
        return account != null && allowed.Contains(account, StringComparer.Ordinal);
    }

    internal static bool IsEventOwnerCommand(string line)
    {
        if (!line.StartsWith('!')) return false;
        var parts = line[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 && (parts[0].Equals("siege", StringComparison.OrdinalIgnoreCase) ||
            parts[0].Equals("twpoint", StringComparison.OrdinalIgnoreCase) ||
            parts[0].Equals("mvp", StringComparison.OrdinalIgnoreCase) &&
            (parts.Length < 2 || !parts[1].Equals("goto", StringComparison.OrdinalIgnoreCase)));
    }

    /// <param name="loopbackTool">The loopback HTTP /ro3emu/gm: an empty GmAccounts still means everyone.</param>
    public Task RunAsync(GameSession session, string line, bool loopbackTool = false)
    {
        // A GM line typed in chat arrives inside the chat message's role scope (C/D 16). GM admits its own scope (Realm
        // or an economic request), so it runs as its own operation right after instead of expanding the chat's.
        if (session.Owners is { Current: { RuntimeOnly: false } })
        {
            services.GetRequiredService<GameLoop>().TrackRuntimeDetached("GM", () => RunAsync(session, line, loopbackTool));
            return Task.CompletedTask;
        }
        return RunAdmittedAsync(session, line, loopbackTool);
    }

    private Task RunAdmittedAsync(GameSession session, string line, bool loopbackTool)
        => session.Owners is { } owners
            ? IsEventOwnerCommand(line)
                ? owners.RunEconomicRequestAsync(session, () => RunCoreAsync(session, line, loopbackTool))
                : owners.RunSessionAsync("GM", [Ownership.ReservationKey.Realm], session, () => RunCoreAsync(session, line, loopbackTool))
            : RunCoreAsync(session, line, loopbackTool);

    private async Task RunCoreAsync(GameSession session, string line, bool loopbackTool)
    {
        if (!Allowed(options.Value, session.Account, loopbackTool))
        {
            await ReplyAsync(session, "GM commands are not enabled for this account.");
            return;
        }
        var parts = line[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        log.LogInformation("GM {Name}: {Line}", session.Role?.Name, line);
        if (parts.Length == 0 || !Commands().TryGetValue(parts[0].ToLowerInvariant(), out var command))
        {
            await ReplyAsync(session, $"Unknown command. {string.Join(" ", Commands().Keys.Select(k => "!" + k))}");
            return;
        }
        try
        {
            await command.Run(session, parts[1..]);
        }
        catch (Exception e) when (e is FormatException or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            await ReplyAsync(session, $"Usage: !{parts[0]} {command.Usage}");
        }
    }

    /// <summary>30300 from the client's GM panel: capability probe, preset buttons (GMCombinationConfig) or one op.</summary>
    public async Task PanelAsync(GameSession session, GeneralGMProcReq req)
    {
        string param = req.ParamString ?? "";
        if (param.StartsWith(PanelProbe, StringComparison.Ordinal))
        {
            await session.SendGameAsync(GeneralProcGm, new GeneralGMProcRsp { Result = 0, ErrInfo = param + (Allowed(session) ? ":1" : ":0") });
            return;
        }
        string reply;
        if (!Allowed(session))
        {
            reply = "GM commands are not enabled for this account.";
        }
        else
        {
            log.LogInformation("GM panel {Name}: op {Op} '{Param}' list {List}", session.Role?.Name, (int)req.GmType, param, req.GmListId);
            try
            {
                reply = (int)req.GmType == OpNone && req.GmListId > 0
                    ? await CombinationAsync(session, req.GmListId)
                    : await OpAsync(session, (int)req.GmType, param.Split(',', StringSplitOptions.TrimEntries));
            }
            catch (Exception e) when (e is FormatException or IndexOutOfRangeException or ArgumentException or OverflowException)
            {
                reply = $"Bad parameters for op {(int)req.GmType}: {e.Message}";
            }
        }
        await session.SendGameAsync(GeneralProcGm, new GeneralGMProcRsp { Result = 0, ErrInfo = reply });
    }

    /// <summary>A preset button: GMCombinationConfig._kCombinationScript = [[opName, p1, ...], ...].</summary>
    private async Task<string> CombinationAsync(GameSession session, int listId)
    {
        if (data.Row("GMCombinationConfig", listId) is not { } row || !row.TryGetProperty("_kCombinationScript", out var script)
            || script.ValueKind != JsonValueKind.Array)
            return $"No preset {listId}.";
        var results = new List<string>();
        foreach (var step in script.EnumerateArray())
        {
            if (step.ValueKind != JsonValueKind.Array || step.GetArrayLength() == 0) continue;
            string name = step[0].GetString() ?? "";
            var args = step.EnumerateArray().Skip(1).Select(a => a.ValueKind == JsonValueKind.String ? a.GetString()! : a.ToString()).ToArray();
            try
            {
                results.Add(OpNumber(name) is { } op ? await OpAsync(session, op, args) : $"{name}: unknown op");
            }
            catch (ArgumentException e)
            {
                results.Add($"{name} {string.Join(',', args)}: {e.Message}");
            }
        }
        return string.Join("\n", results);
    }

    /// <summary>GmOpEnum value by its proto name, case-insensitively ("GmOpAddItem", "GmSetLevel"...).</summary>
    private static int? OpNumber(string name) =>
        GeneralGMProcReq.Descriptor.FindFieldByName("gm_type").EnumType.Values
            .FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))?.Number;

    /// <summary>One GM operation with the panel's comma-separated parameters.</summary>
    private async Task<string> OpAsync(GameSession s, int op, string[] args)
    {
        switch (op)
        {
            case OpAddItem:
                int count = args.Length > 1 ? I(args[1]) : 1;
                return await character.AddItemAsync(s, I(args[0]), count <= 0 ? 1 : count);
            case OpRemoveItem:
                return await character.RemoveItemAsync(s, I(args[0]), args.Length > 1 && I(args[1]) > 0 ? I(args[1]) : int.MaxValue);
            case OpAddAttrPnt:
                return await character.AddStatPointsAsync(s, I(args[0]));
            case OpSetLevel:
                return await character.SetLevelAsync(s, I(args[0]), args.Length > 1 && I(args[1]) > 0 ? I(args[1]) : null);
            case OpAddExp:
                return await character.AddExpAsync(s, L(args[0]), args.Length > 1 ? L(args[1]) : 0);
            case OpJumpMap:
                long scene = L(args[0]);
                bool hasPoint = args.Length >= 3 && (F(args[1]) != 0 || F(args[2]) != 0);
                await WarpAsync(s, scene, hasPoint ? F(args[1]) : null, hasPoint ? F(args[2]) : null);
                return $"Warp to {scene} {data.MapName(scene)}";
            case OpClearPack:
                return await character.ClearBagAsync(s);
            case OpFullHpMp:
                return "HP and SP are always full (no combat yet).";
            case OpSetJob:
                return await character.SetJobAsync(s, I(args[0]));
            default:
                return $"GM op {op} is not implemented by the emulator.";
        }
    }

    public static Task ReplyAsync(GameSession session, string text) =>
        session.SendGameAsync(BroadcastNotify, new BroadCastNotify { Content = text });

    private Dictionary<string, (string Usage, Command Run)> Commands() => _commands ??= new()
    {
        ["help"] = ("", (s, _) => ReplyAsync(s, "Commands: " + string.Join("  ", Commands().Select(c => $"!{c.Key} {c.Value.Usage}".Trim())))),
        ["where"] = ("", Where),
        ["dungeonsolo"] = ("on|off (test aid, this role session only)", async (s, a) =>
        {
            if (a.Length != 1 || a[0].ToLowerInvariant() is not ("on" or "off")) throw new ArgumentException("on|off");
            // Changing a test policy cancels any outstanding queue/vote before the next Enter Challenge.
            await services.GetRequiredService<DungeonMatchService>().LeaveRoleAsync(s, notifySelf: true);
            s.DungeonSoloMatch = a[0].Equals("on", StringComparison.OrdinalIgnoreCase);
            await ReplyAsync(s, $"Dungeon solo TEST AID {(s.DungeonSoloMatch ? "ON" : "OFF")} for this role session. "
                + "Click Enter Challenge again. This is not original matchmaking behavior.");
        }
        ),
        ["dungeon"] = ("<sceneEntry> (test aid: the client's 30400 Enter for a solo story dungeon)", async (s, a) =>
        {
            if (a.Length != 1 || !long.TryParse(a[0], out long scene)) throw new ArgumentException("<sceneEntry>");
            // The same entry the client's story step sends; admission, loading and settlement stay the server's own.
            await services.GetRequiredService<DungeonService>().EnterAsync(s, new Romsg.EnterDungeonReq { SceneEntry = scene });
            await ReplyAsync(s, $"Dungeon {scene}: entry requested (test aid).");
        }
        ),
        ["chicken"] = ("shape <players> <team>|shape reset|skip|rank <n> (Survivor Battle test aids)", async (s, a) =>
        {
            // Test aids, not original behaviour: a small Survivor Battle lobby, and skipping a pre-battle stage.
            var battles = services.GetRequiredService<TimedBattleService>();
            if (a.Length == 3 && a[0] == "shape" && int.TryParse(a[1], out int players) && int.TryParse(a[2], out int team)
                && team > 0 && players > team && players % team == 0)
            {
                battles.ChickenTestShape = (players, team);
                await ReplyAsync(s, $"Survivor Battle TEST AID: {players} entrants in teams of {team} (in memory).");
            }
            else if (a.Length == 2 && a[0] == "shape" && a[1] == "reset")
            {
                battles.ChickenTestShape = null;
                await ReplyAsync(s, "Survivor Battle shape from the config again.");
            }
            else if (a.Length == 1 && a[0] == "skip") await ReplyAsync(s, await battles.SkipStageAsync(s));
            else if (a.Length == 2 && a[0] == "rank" && int.TryParse(a[1], out int rank))
                await ReplyAsync(s, await battles.SetChickenRankAsync(s, rank));
            else throw new ArgumentException("shape <players> <team> (players a multiple of team, more than one team) | shape reset | skip | rank <n>");
        }
        ),
        ["online"] = ("", Online),
        ["warp"] = ("<sceneEntry> [x z]", Warp),
        ["jump"] = ("<x> <z>", Jump),
        ["finditem"] = ("<name>", FindItem),
        ["item"] = ("<id|name> [count]", Item),
        ["mail"] = ("<title> [item count]", Mail),
        ["delitem"] = ("<id> [count]", (s, a) => Op(s, OpRemoveItem, a)),
        ["zeny"] = ("<amount>", Zeny),
        ["lvl"] = ("<base> [job]", (s, a) => Op(s, OpSetLevel, a)),
        ["exp"] = ("<base> [job]", (s, a) => Op(s, OpAddExp, a)),
        ["points"] = ("<n>", (s, a) => Op(s, OpAddAttrPnt, a)),
        ["job"] = ("<jobId>", (s, a) => Op(s, OpSetJob, a)),
        ["clearbag"] = ("", (s, a) => Op(s, OpClearPack, a)),
        ["client"] = ("<find name>", ClientDiagnostics),
        ["speed"] = ("<speed, 0 = normal>", async (s, a) => await ReplyAsync(s, await character.SetSpeedAsync(s, L(a[0])))),
        ["quest"] = ("[accept|finish|done <taskId> | reset]", async (s, a) => await ReplyAsync(s, a.Length == 0
            ? "Quests: " + tasks.Describe(s)
            : await tasks.GmAsync(s, a[0].ToLowerInvariant(), a.Length > 1 ? L(a[1]) : 0))),
        ["mvp"] = ("[spawn|where|goto|weaken <cfg>]", async (s, a) =>
        {
            var mvps = services.GetRequiredService<MvpService>();
            string reply = await mvps.GmAsync(a);
            if (a.Length >= 2 && a[0] == "goto" && int.TryParse(a[1], out _))
            {
                var where = (await mvps.GmAsync(["where", a[1]])).Split(' ');
                if (where.Length == 3 && long.TryParse(where[0], out long map))
                {
                    await WarpAsync(s, map, F(where[1]) + 3, F(where[2]));
                    reply = "Warped next to MVP " + a[1];
                }
            }
            await ReplyAsync(s, reply);
        }
        ),
        ["boss"] = ("spawn <npcId> | skills [npcId] | phase <n> | cast <skillId> | kill (boss skill test aids: the nearest boss within 40 m)", async (s, a) =>
        {
            string reply = await services.GetRequiredService<MonsterService>().BossGmAsync(s, a);
            foreach (string line in reply.Split('\n')) await ReplyAsync(s, line);
        }
        ),
        ["season"] = ("[playbook]", async (s, a) =>
        {
            var seasons = services.GetRequiredService<SeasonService>();
            await ReplyAsync(s, a.Length == 0 ? seasons.Describe() : seasons.OpenContent.Enabled
                ? "OpenContent keeps playbook 101 (the only playbook with its own content); switching would hide content"
                : await seasons.SetPlayBookAsync(I(a[0])));
        }
        ),
        ["siege"] = ("open|close|state|seed <city> <attackGuild> <defenseGuild> (test aid)", async (s, a) =>
            await ReplyAsync(s, await services.GetRequiredService<TerritoryWarsService>().SiegeGmAsync(a))),
        ["twpoint"] = ("<point> unready|ready|complete [readySeconds] [owner|self] (test aid: 30129 stronghold gameplay point)", async (s, a) =>
            await ReplyAsync(s, await services.GetRequiredService<TerritoryWarsService>().ScenePointGmAsync(s, a))),
        // Test aids, not original behaviour. Window override lives only in memory and affects activity 125.
        ["guildchallenge"] = ("open|close (test aid)", async (s, a) =>
        {
            if (a.Length != 1 || a[0] is not ("open" or "close")) throw new ArgumentException("Use !guildchallenge open|close");
            await services.GetRequiredService<GuildChallengeService>().TestWindowAsync(a[0] == "open");
            await ReplyAsync(s, a[0] == "open" ? "Guild Challenge test window open for 90 minutes; close or restart restores the real schedule."
                : "Guild Challenge test window closed; real schedule restored.");
        }
        ),
        // Test aid: activity 3303's occurrence starts now (in memory), so the Hunting Challenge runs its 1074 phases.
        ["guildhunt"] = ("open|close (test aid)", async (s, a) =>
        {
            if (a.Length != 1 || a[0] is not ("open" or "close")) throw new ArgumentException("Use !guildhunt open|close");
            services.GetRequiredService<GuildHuntingService>().TestWindow(a[0] == "open");
            await ReplyAsync(s, a[0] == "open" ? "Hunting Challenge test window open: candidates now, combat in 2 minutes."
                : "Hunting Challenge test window removed; real schedule restored.");
        }
        ),
        // stage 0 = full-clear shortcut to the final stage's real drop pool, not the preview-only base award.
        ["guildauction"] = ("[challengeId=1001] [stage=0]", async (s, a) =>
        {
            var guild = services.GetRequiredService<Accounts.GuildStore>().ForRole(s.Role!.Id) ?? throw new ArgumentException("Join a guild first");
            int challenge = a.Length > 0 ? I(a[0]) : 1001, stage = a.Length > 1 ? I(a[1]) : 0;
            int code = await services.GetRequiredService<AuctionService>().PublishGuildChallengeAsync(
                "gm:" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), guild.Id, challenge, stage, [s.Role.Id]);
            await ReplyAsync(s, code == 0 ? $"guild auction published (challenge {challenge}, stage {stage})" : $"guild auction failed: {code}");
        }
        ),
        // Test aids, not original behaviour: system World lots, and Event lots with the GM as the dividend participant.
        ["worldauction"] = ("open [itemId...] | close (test aid)", async (s, a) =>
            await ReplyAsync(s, await services.GetRequiredService<AuctionService>().WorldGmAsync(a))),
        ["actauction"] = ("<3001|3002|3003> open [itemId...] | close (test aid)", async (s, a) =>
            await ReplyAsync(s, await services.GetRequiredService<AuctionService>().EventGmAsync(s.Role!.Id, a))),
        ["seasonnext"] = ("<playbook> <yyyy-MM-dd>", async (s, a) =>
        {
            if (a.Length != 2 || !DateTime.TryParseExact(a[1], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var start)) throw new ArgumentException("Use !seasonnext <playbook> <yyyy-MM-dd>");
            var seasons = services.GetRequiredService<SeasonService>();
            // A rollover archives and resets season-keyed state; under OpenContent there are no seasons to roll.
            await ReplyAsync(s, seasons.OpenContent.Enabled ? "OpenContent: there is no season rollover"
                : await seasons.AdvanceCycleAsync(I(a[0]), start));
        }
        ),
        ["hp"] = ("<hp> [sp]", async (s, a) => { await character.SetHpSpAsync(s, L(a[0]), a.Length > 1 ? L(a[1]) : null); await ReplyAsync(s, $"HP {a[0]}"); }
        ),
    };

    /// <summary>Asks the client hook (Ro3Emu.ClientHooks Diagnostics, msg 77012) to inspect itself into Player.log.</summary>
    private async Task ClientDiagnostics(GameSession s, string[] args)
    {
        if (args.Length == 0) throw new ArgumentException("command");
        await s.SendGameRawAsync(ClientDiagnosticsMsg, System.Text.Encoding.UTF8.GetBytes(string.Join(' ', args)));
        await ReplyAsync(s, "Sent to the client; see Player.log [Ro3Emu.Diag].");
    }

    private const uint ClientDiagnosticsMsg = 77012;

    private async Task Op(GameSession s, int op, string[] args)
    {
        if (op != OpClearPack && args.Length == 0) throw new ArgumentException("parameters");
        await ReplyAsync(s, await OpAsync(s, op, args));
    }

    /// <summary>"!item 2010121", "!item 1000000 20", "!item beginner potion 20".</summary>
    private Task Item(GameSession s, string[] args)
    {
        if (args.Length == 0) throw new ArgumentException("item");
        if (int.TryParse(args[0], out _)) return Op(s, OpAddItem, args);
        bool counted = args.Length > 1 && int.TryParse(args[^1], out _);
        string name = string.Join(' ', counted ? args[..^1] : args);
        var hit = data.FindItems(name).FirstOrDefault();
        if (hit.Id == 0) return ReplyAsync(s, $"No item called '{name}'.");
        return Op(s, OpAddItem, [hit.Id.ToString(CultureInfo.InvariantCulture), counted ? args[^1] : "1"]);
    }

    /// <summary>Local GM system-mail smoke path; trailing numeric pair is an optional plain attachment.</summary>
    private async Task Mail(GameSession s, string[] args)
    {
        if (args.Length == 0) throw new ArgumentException("mail title [item count]");
        bool attached = args.Length >= 3 && int.TryParse(args[^2], out _) && int.TryParse(args[^1], out _);
        string title = string.Join(' ', attached ? args[..^2] : args);
        var goods = attached ? new[] { new MailService.Attachment(I(args[^2]), I(args[^1])) } : [];
        var mail = services.GetRequiredService<MailService>();
        string id = await mail.SendRawAsync(s.Role!.Id, title, title, goods);
        await ReplyAsync(s, $"Mail {id} delivered.");
    }

    private Task Zeny(GameSession s, string[] args)
    {
        long amount = L(args[0]);
        return amount >= 0
            ? Op(s, OpAddItem, ["1", amount.ToString(CultureInfo.InvariantCulture)])
            : Op(s, OpRemoveItem, ["1", (-amount).ToString(CultureInfo.InvariantCulture)]);
    }

    private Task Where(GameSession s, string[] _) =>
        ReplyAsync(s, string.Create(CultureInfo.InvariantCulture,
            $"Scene {s.SceneEntry} {data.MapName(s.SceneEntry)} at {s.Position.X:F1} {s.Position.Y:F1} {s.Position.Z:F1}"));

    private Task Online(GameSession s, string[] _) =>
        ReplyAsync(s, "Online: " + string.Join(", ", sessions.All().Select(o => $"{o.Role?.Name ?? o.Account} ({o.SceneEntry})")));

    private Task FindItem(GameSession s, string[] args)
    {
        var hits = data.FindItems(string.Join(' ', args)).Take(8).ToList();
        return ReplyAsync(s, hits.Count == 0 ? "No such item." : string.Join("  ", hits.Select(h => $"{h.Id} {h.Name}")));
    }

    /// <summary>Same-map teleport to x,z (y from the nav grid).</summary>
    private Task Jump(GameSession s, string[] args) =>
        scene.TeleportInMapAsync(s, new MapPoint(F(args[0]), s.Position.Y, F(args[1]), s.Face));

    private Task Warp(GameSession s, string[] args) =>
        WarpAsync(s, L(args[0]), args.Length >= 3 ? F(args[1]) : null, args.Length >= 3 ? F(args[2]) : null);

    /// <summary>Map change to x,z, or to the map's born point.</summary>
    private async Task WarpAsync(GameSession s, long target, float? x, float? z)
    {
        var at = x is { } px && z is { } pz ? new MapPoint(px, s.Position.Y, pz, s.Face) : (MapPoint?)null;
        if (!await scene.EnterSceneAsync(s, target, at)) throw new ArgumentException("unknown scene");
    }

    private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
    private static int I(string s) => int.Parse(s, CultureInfo.InvariantCulture);
    private static long L(string s) => long.Parse(s, CultureInfo.InvariantCulture);
}
