using System.Globalization;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class GmCommands
{
    /// <summary>GmOpEnum values of the character, gear, quest, dungeon, guild, mail, position and server ops.</summary>
    private const int OpStrengthen = 4, OpRefine = 5, OpResetAttrPnt = 9, OpDropPool = 26, OpGvgReset = 51, OpCreateMultiRole = 54,
        OpSetServerTime = 62, OpSetServerOpenDay = 63, OpDumpServerLevelInfo = 64, OpSendMailPerson = 70, OpSendMailSystemByCfgId = 71,
        OpSendMailSystemByCustom = 72, OpRevokeSystemMail = 73, OpShowLocation = 79, OpFlyToTarget = 83, OpDungeonCmd = 93, OpGuild = 94,
        OpTask = 99, OpEnterDungeon = 108, OpAllSkillsMax = 110, OpPrintBarroom = 134, OpSetBarroomInfo = 136,
        OpAddBarroomToDayStayTimes = 141, OpSetMoveVerify = 186, OpSetRoleAttr = 301;

    /// <summary>GmTask's state option (GMConfig 1021 _kOptionName): accept, abandon, reset, submit, complete, delete.</summary>
    private const int TaskAccept = 0, TaskAbandon = 1, TaskReset = 2, TaskSubmit = 3, TaskComplete = 4, TaskDelete = 5;

    /// <summary>The guild function (FunctionConfig 13) GuildService checks before a creation.</summary>
    private const int GuildFunction = 13;

    /// <summary>GM panel character, gear, quests, dungeons, guild, mail and position ops; null when the op is not one of them.</summary>
    private async Task<string?> CharacterOpAsync(GameSession s, int op, string[] args)
    {
        switch (op)
        {
            case OpStrengthen or OpRefine:
                return await EquipLevelAsync(s, op == OpRefine, args);
            case OpResetAttrPnt:
                // (inferred) The current stat plan, as the client's own reset (31501 without a plan index).
                await character.ResetStatsAsync(s);
                return "Stat points of the current plan reset.";
            case OpAllSkillsMax:
                // GMConfig 3007 options: 0 clears, 1 learns all; the button's default is to learn.
                return await services.GetRequiredService<SkillService>().GmLearnAllAsync(s, Arg(args, 0) is not "0");
            case OpSetRoleAttr:
                return SetRoleAttr(args);
            case OpTask:
                return await TaskAsync(s, I(args[0]), L(args[1]));
            case OpEnterDungeon:
                return await EnterDungeonAsync(s, L(args[0]));
            case OpDungeonCmd:
                return await DungeonCmdAsync(s, args);
            case OpGuild:
                return await GuildAsync(s, args);
            case OpSendMailPerson:
                return await MailPersonAsync(s, args);
            case OpSendMailSystemByCfgId:
                return await MailAllByTemplateAsync(I(args[0]), MailExpiry(Arg(args, 1)));
            case OpSendMailSystemByCustom:
                return await MailAllCustomAsync(s, args);
            case OpRevokeSystemMail:
                return $"GM op {OpLabel(op)}: the emulator keeps no record of server-wide mail to revoke (each letter lives only in its mailbox).";
            case OpDropPool:
                return await DropPoolAsync(s, L(args[0]));
            case OpShowLocation:
                string instance = s.InstanceId != 0 ? $" instance {s.InstanceId}" : "";
                return string.Create(CultureInfo.InvariantCulture, $"Scene {s.SceneEntry} {data.MapName(s.SceneEntry)} line {s.LineNumber}{instance} "
                    + $"at {s.Position.X:F1} {s.Position.Y:F1} {s.Position.Z:F1} face {s.Face:F0}");
            case OpFlyToTarget:
                return await FlyToAsync(s, L(args[0]));
            case OpGvgReset:
                return await GvgAsync(s, Arg(args, 0));
            case OpDumpServerLevelInfo:
                int? day = character.ServerDay?.Invoke();
                return $"Server day {day?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}, server level {character.ServerLevel()}, "
                    + $"level cap {character.MaxLevel} (no day-based cap: owner decision, all content open).";
            case OpSetServerTime:
                return $"GM op {OpLabel(op)}: the emulator runs on the host clock; one shared server time for every session cannot be set from the panel.";
            case OpSetServerOpenDay:
                return $"GM op {OpLabel(op)}: the open day counts from the season start (SeasonService), which the panel does not set; see !seasonnext.";
            case OpCreateMultiRole:
                return $"GM op {OpLabel(op)}: the emulator creates roles only through an account's own login, not in batches.";
            case OpSetMoveVerify:
                return $"GM op {OpLabel(op)}: movement verification is the host's ServerOptions.WalkCheck ('{options.Value.WalkCheck}'), not a per-role switch.";
            case OpPrintBarroom or >= OpSetBarroomInfo and <= OpAddBarroomToDayStayTimes:
                return $"GM op {OpLabel(op)}: the emulator has no tavern (Barroom) system.";
            default:
                return null;
        }
    }

    /// <summary>The panel's parameter <paramref name="index"/>, or "" when it was left empty or not sent.</summary>
    private static string Arg(string[] args, int index) => index < args.Length ? args[index] : "";

    /// <summary>
    /// GmOpStrengthen / GmOpRefine [slot, level] (GMConfig 1006/1007): slot 1 weapon (main and off hand), 2 armor, 3 cloak,
    /// 4 shoes, 5 left and 6 right accessory, i.e. EquipGridType 1+2, 3..7; an empty level means 1. Levels belong to the
    /// grid, so the gear worn there (or none) takes them, as after the client's own upgrade.
    /// </summary>
    private async Task<string> EquipLevelAsync(GameSession s, bool refine, string[] args)
    {
        int slot = I(args[0]), level = Arg(args, 1) is { Length: > 0 } text ? I(text) : 1;
        if (slot is < 1 or > 6) throw new ArgumentException("slot 1 weapon, 2 armor, 3 cloak, 4 shoes, 5 left / 6 right accessory");
        int[] grids = slot == 1 ? [1, 2] : [slot + 1];
        return await services.GetRequiredService<EquipmentService>().GmSetLevelAsync(s, grids, level, refine)
            ?? $"No {(refine ? "refine" : "strengthen")} level {level} for slot {slot}.";
    }

    /// <summary>
    /// GmSetRoleAttr [school, level, paid level, has BD] (GMConfig 1033) applies a GMRoleAttrSet row (genre * 1000 + level),
    /// and the client export holds those rows' keys only: their columns are server-side, so there is nothing to apply.
    /// </summary>
    private string SetRoleAttr(string[] args)
    {
        long key = L(args[0]) * 1000 + L(args[1]);
        return data.Row("GMRoleAttrSet", key) is null
            ? $"No GMRoleAttrSet row {key} (school {args[0]}, level {args[1]})."
            : $"GM op {OpLabel(OpSetRoleAttr)}: GMRoleAttrSet {key} has no columns in the client export (a server-side table), so the attribute set is unknown.";
    }

    /// <summary>
    /// GmTask [state, task id] (GMConfig 1021; presets: "4" opens a system by completing its quest) through the !quest
    /// paths. (inferred) Abandon and delete both drop the quest, reset drops and accepts it again, submit hands in only a
    /// running one, complete accepts it when needed and hands it in.
    /// </summary>
    private async Task<string> TaskAsync(GameSession s, int state, long id)
    {
        if (data.Row("TaskConfig", id) is null) return $"No quest {id}.";
        switch (state)
        {
            case TaskAccept:
                return await tasks.GmAsync(s, "accept", id);
            case TaskAbandon or TaskDelete:
                return await tasks.GmAsync(s, "drop", id);
            case TaskReset:
                await tasks.GmAsync(s, "drop", id);
                return await tasks.GmAsync(s, "accept", id);
            case TaskSubmit when s.Tasks?.Infos.GetValueOrDefault(id)?.Status is not (TaskState.Running or TaskState.Finish):
                return $"Quest {id} is not running.";
            case TaskSubmit or TaskComplete:
                return await tasks.GmAsync(s, "done", id);
            default:
                throw new ArgumentException("state 0 accept, 1 abandon, 2 reset, 3 submit, 4 complete, 5 delete");
        }
    }

    /// <summary>GmEnterDungeon [dungeon id]: the client's own 30400 Enter (as !dungeon), whose reply carries the result.</summary>
    private async Task<string> EnterDungeonAsync(GameSession s, long dungeon)
    {
        if (data.Row("DungeonConfig", dungeon) is null) return $"No dungeon {dungeon}.";
        await services.GetRequiredService<DungeonService>().EnterAsync(s, new EnterDungeonReq { SceneEntry = dungeon });
        return $"Dungeon {dungeon}: entry requested; the 30400 reply carries the result.";
    }

    /// <summary>
    /// GmDungeonCmd [enter|leave] (GMConfig 3005 "退出副本"): leave is the client's own 30401 Leave. (inferred) Enter names
    /// no dungeon, so it takes an optional second parameter and otherwise points to GmEnterDungeon.
    /// </summary>
    private async Task<string> DungeonCmdAsync(GameSession s, string[] args)
    {
        switch (args[0].ToLowerInvariant())
        {
            case "enter":
                return Arg(args, 1) is { Length: > 0 } id ? await EnterDungeonAsync(s, L(id)) : "Enter needs a dungeon id: use GmEnterDungeon.";
            case "leave":
                if (s.InstanceId == 0) return "Not in a dungeon.";
                await services.GetRequiredService<DungeonService>().LeaveAsync(s);
                return "Dungeon left.";
            default:
                throw new ArgumentException("enter|leave");
        }
    }

    /// <summary>
    /// GmOpGuild: the button sends "create,{name}" (GMConfig 3002 _kParameterField). The presets' league subcommands
    /// (leaguematch, guildleague, loadleaguedebugjson) are original debug hooks with no counterpart; !league holds the
    /// emulator's league test aids. The creation is the client's own 32210 with a default frame and design.
    /// </summary>
    private async Task<string> GuildAsync(GameSession s, string[] args)
    {
        string sub = args[0].ToLowerInvariant();
        if (sub is "leaguematch" or "guildleague" or "loadleaguedebugjson")
            return $"GmOpGuild {sub}: an original league debug hook the emulator does not have; see !league register|fill|status.";
        if (sub != "create" || args.Length < 2 || args[1].Length == 0) throw new ArgumentException("create,<guild name>");
        string name = string.Join(',', args[1..]);
        var guilds = services.GetRequiredService<Accounts.GuildStore>();
        if (guilds.ForRole(s.Role!.Id) is { } own) return $"Already in guild {own.Name} ({own.Id}).";
        if (!services.GetRequiredService<FunctionService>().IsOpen(s, GuildFunction)) return "The guild function is not open for this role yet.";
        // (inferred) The first frame (type 1) and design (type 2) a new guild may pick: shown at creation, unlocked by default.
        var icons = GuildIconConfig.All(data).Where(i => i.CreateShow != 0 && i.DefaultUnlock == 1).ToList();
        var icon = new GuildHeadIcon
        {
            BackgroundType = icons.Where(i => i.Type == 1).Select(i => i.Id).FirstOrDefault(),
            IconType = icons.Where(i => i.Type == 2).Select(i => i.Id).FirstOrDefault(),
        };
        await services.GetRequiredService<GuildService>().CreateAsync(s, new GuildCreateReq { Name = name, Icon = icon });
        return guilds.ForRole(s.Role.Id) is { } created
            ? $"Guild {created.Name} ({created.Id}) created."
            : $"Guild {name} not created; the 32210 reply carries the reason.";
    }

    /// <summary>
    /// GmSendMailPerson [title, content, type, NPC, expiry, reward kind, reward value] (GMConfig 1017): type 1 system or
    /// 3 guild mail; reward kind 0 drop pool, 1 item, 2 award ("奖励只生效一个配置": one reward). (inferred) The recipient
    /// is the GM, the NPC sender is not resolved (the system sender), and an empty expiry keeps MailService's default.
    /// </summary>
    private async Task<string> MailPersonAsync(GameSession s, string[] args)
    {
        int category = Arg(args, 2) is { Length: > 0 } type ? I(type) : 1;
        if (category is not (1 or 3)) throw new ArgumentException("mail type 1 system or 3 guild");
        var goods = MailGoods(s, Arg(args, 5), Arg(args, 6));
        string id = await services.GetRequiredService<MailService>().SendRawAsync(s.Role!.Id, args[0], Arg(args, 1), goods,
            expiresAt: MailExpiry(Arg(args, 4)), category: category);
        return $"Mail {id} delivered" + (goods.Count > 0 ? $" with {Goods(goods.Select(g => (g.Entry, g.Count)))}." : ".");
    }

    /// <summary>
    /// GmSendMailSystemByCfgId [mail id, expiry] (GMConfig 1018): a MailConfig template to every role. (inferred) The
    /// emulator has no server-wide mail record, so "every role" is every role online now.
    /// </summary>
    private async Task<string> MailAllByTemplateAsync(int template, DateTimeOffset? expiresAt)
    {
        if (data.Row("MailConfig", template) is null) return $"No MailConfig {template}.";
        var mail = services.GetRequiredService<MailService>();
        var roles = OnlineRoles();
        try
        {
            foreach (var role in roles) await mail.SendTemplateAsync(role.Role!.Id, template, expiresAt);
        }
        catch (AwardResolutionException e)
        {
            return $"MailConfig {template}: {e.Message}.";
        }
        return $"MailConfig {template} delivered to {roles.Count} online roles.";
    }

    /// <summary>
    /// GmSendMailSystemByCustom [title, content, zone, NPC, expiry, reward kind, reward value] (GMConfig 1019): a custom
    /// system mail to every role. (inferred) Every role online now (no server-wide mail record); the zone is this server;
    /// a drop pool or award is rolled for each recipient.
    /// </summary>
    private async Task<string> MailAllCustomAsync(GameSession s, string[] args)
    {
        var mail = services.GetRequiredService<MailService>();
        var expiresAt = MailExpiry(Arg(args, 4));
        MailGoods(s, Arg(args, 5), Arg(args, 6)); // refuse a bad reward before anyone gets mail
        var roles = OnlineRoles();
        foreach (var role in roles)
            await mail.SendRawAsync(role.Role!.Id, args[0], Arg(args, 1), MailGoods(role, Arg(args, 5), Arg(args, 6)), expiresAt: expiresAt);
        return $"Mail delivered to {roles.Count} online roles.";
    }

    private List<GameSession> OnlineRoles() => [.. sessions.All().Where(o => o.Role != null && o.Progress != null)];

    /// <summary>
    /// The panel's mail reward: kind 0 a DropPoolConfig id, 1 an item id ("id" or "id:count", (inferred) the count
    /// separator), 2 an AwardConfig id; pools and awards are rolled for <paramref name="recipient"/>, binding kept.
    /// </summary>
    private List<MailService.Attachment> MailGoods(GameSession recipient, string kind, string value)
    {
        if (kind.Length == 0 || value.Length == 0) return [];
        var rewards = services.GetRequiredService<RewardService>();
        switch (I(kind))
        {
            case 0:
                if (data.Row("DropPoolConfig", L(value)) is null) throw new ArgumentException($"no drop pool {value}");
                return [.. rewards.RollPoolBound(L(value), recipient.Progress!).Select(g => new MailService.Attachment(g.Entry, g.Count, g.Bind))];
            case 1:
                var parts = value.Split(':', ';', '|', '*');
                int entry = I(parts[0]), count = parts.Length > 1 ? I(parts[1]) : 1;
                if (data.Row("ItemConfig", entry) is null || count <= 0) throw new ArgumentException($"no item {value}");
                return [new MailService.Attachment(entry, count)];
            case 2:
                if (data.Row("AwardConfig", L(value)) is null) throw new ArgumentException($"no award {value}");
                return [.. rewards.RollAwardBound(L(value), recipient).Select(g => new MailService.Attachment(g.Entry, g.Count, g.Bind))];
            default:
                throw new ArgumentException("reward kind 0 drop pool, 1 item, 2 award");
        }
    }

    /// <summary>
    /// A mail expiry from the panel: empty or 0 = the mail's default, (inferred) a bare number = days from now, otherwise
    /// a host-local date time as GmSetServerTime writes it ("年-月-日 时:分:秒").
    /// </summary>
    private static DateTimeOffset? MailExpiry(string text)
    {
        if (text.Length == 0 || text == "0") return null;
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int days)) return DateTimeOffset.UtcNow.AddDays(days);
        return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal);
    }

    /// <summary>GmDropPool [pool id] (GMConfig 1015 "触发掉落"): one DropPoolConfig draw for the GM, into the bag.</summary>
    private async Task<string> DropPoolAsync(GameSession s, long pool)
    {
        if (data.Row("DropPoolConfig", pool) is null) return $"No drop pool {pool}.";
        var rolled = services.GetRequiredService<RewardService>().RollPoolBound(pool, s.Progress!);
        if (rolled.Count == 0) return $"Drop pool {pool} dropped nothing.";
        await character.GrantBoundAsync(s, rolled);
        return $"Drop pool {pool}: {Goods(rolled.Select(g => (g.Entry, g.Count)))}.";
    }

    private string Goods(IEnumerable<(int Entry, int Count)> goods) =>
        string.Join(", ", goods.Select(g => $"{g.Count} {data.ItemName(g.Entry)} ({g.Entry})"));

    /// <summary>
    /// GmFlyToTarget [target id]: next to a player (role id), a monster or other world unit, or an NPC. (inferred) A
    /// target on the GM's own map is a teleport, one on another public map a warp; a private instance is not entered.
    /// </summary>
    private async Task<string> FlyToAsync(GameSession s, long uid)
    {
        if (uid == s.Role!.Id) return "That is you.";
        (RuntimeMapKey Map, long Scene, float X, float Y, float Z)? target = null;
        if (sessions.All().FirstOrDefault(o => o.Role?.Id == uid) is { } player)
            target = (player.RuntimeMap, player.SceneEntry, player.Position.X, player.Position.Y, player.Position.Z);
        else if (services.GetRequiredService<World>().AllUnits().FirstOrDefault(u => u.Uid == uid) is { } unit)
            target = (unit.RuntimeMap, unit.SceneEntry, unit.X, unit.Y, unit.Z);
        else if (services.GetRequiredService<WorldData>().Npc(uid) is { } npc)
            target = (RuntimeMapKey.Public(npc.SceneEntry, s.LineNumber), npc.SceneEntry, npc.X, npc.Y, npc.Z);
        if (target is not { } t) return $"No entity {uid}.";
        if (t.Map == s.RuntimeMap) await scene.TeleportInMapAsync(s, new MapPoint(t.X, t.Y, t.Z, s.Face));
        else if (t.Map.Kind == RuntimeMapKind.PublicLine) await WarpAsync(s, t.Scene, t.X, t.Z);
        else return $"Entity {uid} is in a private instance of {t.Scene}.";
        return string.Create(CultureInfo.InvariantCulture, $"Flew to {uid}: scene {t.Scene} at {t.X:F1} {t.Z:F1}.");
    }

    /// <summary>
    /// GmGVGReset ["123" = start in 1 s, 2 s preparation, 3 s battle | open | next] (GMConfig 3004). The emulator's siege
    /// test aid (!siege) opens and closes the war now; it has no stage skip or custom timings.
    /// </summary>
    private async Task<string> GvgAsync(GameSession s, string arg)
    {
        string sub = arg.ToLowerInvariant();
        if (sub is not ("open" or "close" or "state"))
            return $"GmGVGReset {arg}: the siege runs on its own timeline; the panel maps only open (and !siege close|state).";
        // !siege takes the event owner (IsEventOwnerCommand), so it runs as its own GM line and answers with a notice.
        await RunAsync(s, "!siege " + sub);
        return $"Siege {sub} requested (test aid !siege {sub}); the result follows as a notice.";
    }
}
