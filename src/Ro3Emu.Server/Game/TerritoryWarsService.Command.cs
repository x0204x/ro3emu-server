using System.Globalization;
using Romsg;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The commander's tools of a siege or territory battle (LC_SceneCom_Command, added only by the Siege and Territory
/// Wars dungeon scenes), from the client's own handlers:
/// <list type="bullet">
/// <item>30407/30408 map marks on the siege big map: up to GVEMapMarkMaxNum (10) per mark type. Commander marks need
/// guild right 12 BattleFieldCommand and are shared by the side (the guild); team-leader marks need the party
/// leader and are shared by that party (LC_DunGeon_Helper.CheckManageMarkPermission). The client adds without an id
/// and deletes by a global id ((markType - 1) * 10 + slot, 0 = all of that type); 30408 is a full per-type snapshot
/// with slots 1..10 (LC_DAL_Dungeon:332-427).</item>
/// <item>30431 gather at a mark: the client drops the response and has no 30438 handler; what it shows is system
/// chat 4110041 (DungeonCollect) with a CHAT_PARAM_GATHER parameter [sender, markType, slot], which raises the 3D
/// guide on that mark (LC_SystemMessageDecodeFunc.PushDungeonCollect).</item>
/// <item>30436/30437 command marks (the A-F letters on targets): right 12; the client drops the response and reads
/// 30437 as positional slots (LC_SceneCom_Command.GVGWAR_COMMAND_NOTIFY). The ids are dungeon point ids in a siege,
/// NPC config ids in a territory battle. Six letters exist (UIWords 61142-61147).</item>
/// </list>
/// The state lives on the battle and ends with it. A full mark slot list answers ErrorCodes.Parameter (inferred: the
/// client stops at 10 itself, UIWord 44102).
/// </summary>
public sealed partial class TerritoryWarsService
{
    public const uint SetMark = 30407, MarkNotify = 30408, GatherMark = 30431, CommandMark = 30436, CommandMarkNotify = 30437;
    private const uint SystemBroadcast = 1022;
    private const int BattlefieldCommandRight = 12, MarkSlots = 10, CommandMarkSlots = 6, GatherChat = 4110041;

    /// <summary>One battle's marks, by side (guild) or by party.</summary>
    internal sealed class CommandBoard
    {
        public Dictionary<long, Vec3?[]> CommanderMarks { get; } = [];
        public Dictionary<long, Vec3?[]> TeamMarks { get; } = [];
        public Dictionary<long, long[]> CommandMarks { get; } = [];
        public Dictionary<long, CommandSkillState> Skills { get; } = [];
    }

    private readonly record struct CommandSide(CommandBoard Board, long Guild, GameSession[] Members);

    /// <summary>The battle the session fights in (siege or territory), its board, its guild and the side's present members.</summary>
    private CommandSide? CommandSideOf(GameSession s)
    {
        long guild = OwnId(s);
        if (guild == 0) return null;
        if (s.SiegeBattle is { } siege && SiegeMember(s, siege))
            return new(siege.Command, guild, [.. siege.Room.ActiveMembers.Select(m => m.Session).Where(p => SiegeMember(p, siege) && OwnId(p) == guild)]);
        if (s.TerritoryBattle is { } battle && Member(s, battle))
            return new(battle.Command, guild, [.. battle.Room.ActiveMembers.Select(m => m.Session).Where(p => Member(p, battle) && OwnId(p) == guild)]);
        return null;
    }

    private bool Commands(GameSession s, long guild) => _guilds.HasRight(guild, s.Role!.Id, BattlefieldCommandRight);

    private long LeadsTeam(GameSession s) => _teams.TeamOf(s.Role!.Id) is { TeamId: > 0 } team && team.Leader == s.Role.Id ? team.TeamId : 0;

    /// <summary>The slots a mark of this type goes to for the sender, or null when it may not mark that type.</summary>
    private Vec3?[]? MarkSlotsFor(GameSession s, CommandSide side, DungeonMarkType type)
    {
        if (type == DungeonMarkType.DungeonMarkCommander)
            return Commands(s, side.Guild) ? Slots(side.Board.CommanderMarks, side.Guild) : null;
        if (type == DungeonMarkType.DungeonMarkTeamLeader)
            return LeadsTeam(s) is var team and > 0 ? Slots(side.Board.TeamMarks, team) : null;
        return null;

        static Vec3?[] Slots(Dictionary<long, Vec3?[]> marks, long key) =>
            marks.TryGetValue(key, out var slots) ? slots : marks[key] = new Vec3?[MarkSlots];
    }

    /// <summary>Who sees a mark type: the whole side for commander marks, the sender's party on the side for its own.</summary>
    private IEnumerable<GameSession> MarkAudience(GameSession s, CommandSide side, DungeonMarkType type)
    {
        if (type != DungeonMarkType.DungeonMarkTeamLeader) return side.Members;
        long team = _teams.TeamOf(s.Role!.Id).TeamId;
        return side.Members.Where(p => _teams.TeamOf(p.Role!.Id).TeamId == team);
    }

    private static DungeonSetMarkPosNtf MarkSnapshot(Vec3?[]? slots, DungeonMarkType type)
    {
        var notify = new DungeonSetMarkPosNtf { MarkType = type };
        if (slots != null)
            for (int i = 0; i < slots.Length; i++)
                if (slots[i] is { } at) notify.Pos.Add(new DungeonMarkPos { Id = i + 1, Pos = at.Clone(), MarkType = type });
        return notify;
    }

    /// <summary>A global mark id (1-10 commander, 11-20 team leader) as its type and 0-based slot.</summary>
    private static (DungeonMarkType Type, int Slot) FromGlobalMarkId(int id) =>
        ((DungeonMarkType)((id - 1) / MarkSlots + 1), (id - 1) % MarkSlots);

    private async Task SetMarkAsync(GameSession s, DungeonSetMarkPosReq req)
    {
        var response = new DungeonSetMarkPosRsp { BDel = req.BDel, Pos = req.Pos };
        var type = req.Pos?.MarkType ?? DungeonMarkType.DungeonMarkNone;
        int slot = -1;
        if (req.BDel && req.Pos is { Id: > 0 } byId) (type, slot) = FromGlobalMarkId(byId.Id);
        Vec3?[]? slots = null;
        CommandSide? side = CommandSideOf(s);
        if (side == null) response.Result = ErrorCodes.SceneMissing;
        else if (req.Pos == null || type is not (DungeonMarkType.DungeonMarkCommander or DungeonMarkType.DungeonMarkTeamLeader)
            || req.BDel && req.Pos.Id > MarkSlots * 2 || !req.BDel && req.Pos.Pos == null) response.Result = ErrorCodes.Parameter;
        else if ((slots = MarkSlotsFor(s, side.Value, type)) == null) response.Result = ErrorCodes.NoPermission;
        else if (req.BDel)
        {
            if (slot < 0) Array.Clear(slots);
            else slots[slot] = null;
        }
        else if (Array.IndexOf(slots, null) is var free and >= 0) slots[free] = req.Pos.Pos.Clone();
        else response.Result = ErrorCodes.Parameter;
        await Send(s, SetMark, response);
        if (response.Result == 0)
            await RoleFanout.EachAsync(MarkAudience(s, side!.Value, type), p => p.SendGameAsync(MarkNotify, MarkSnapshot(slots, type)));
    }

    private async Task GatherAsync(GameSession s, DungeonGatherMarkPosReq req)
    {
        var response = new DungeonGatherMarkPosRsp { GatherType = req.GatherType, Pos = req.Pos, GatherGuid = req.GatherGuid };
        CommandSide? side = CommandSideOf(s);
        var (type, slot) = req.Pos is { Id: > 0 and <= MarkSlots * 2 } at ? FromGlobalMarkId(at.Id) : (DungeonMarkType.DungeonMarkNone, -1);
        if (side == null) response.Result = ErrorCodes.SceneMissing;
        else if (req.GatherType != DungeonGatherType.DungeonGatherMark || slot < 0) response.Result = ErrorCodes.Parameter;
        else if (MarkSlotsFor(s, side.Value, type) is not { } slots) response.Result = ErrorCodes.NoPermission;
        else if (slots[slot] == null) response.Result = ErrorCodes.Parameter;
        await Send(s, GatherMark, response);
        if (response.Result != 0) return;
        var chat = new SystemBoardCastMsgNtf { Source = MsgSource.System, ChatId = GatherChat };
        chat.ParamList[1] = new ChatParameter
        {
            Params = { s.Role!.Name, ((int)type).ToString(CultureInfo.InvariantCulture), (slot + 1).ToString(CultureInfo.InvariantCulture) },
            Type = ChatParamType.ChatParamGather,
        };
        await RoleFanout.EachAsync(MarkAudience(s, side!.Value, type), p => p.SendGameAsync(SystemBroadcast, chat));
    }

    private async Task CommandMarkAsync(GameSession s, GvgWarUpdateCommandMarkReq req)
    {
        CommandSide? side = CommandSideOf(s);
        int result = 0;
        long[]? marks = null;
        if (side == null) result = ErrorCodes.SceneMissing;
        else if (!Commands(s, side.Value.Guild)) result = ErrorCodes.NoPermission;
        else
        {
            marks = side.Value.Board.CommandMarks.TryGetValue(side.Value.Guild, out var held) ? held
                : side.Value.Board.CommandMarks[side.Value.Guild] = new long[CommandMarkSlots];
            switch (req.UpdateType)
            {
                case CommandMarkUpdateType.Add when req.MarkId > 0:
                    if (Array.IndexOf(marks, req.MarkId) >= 0) break;
                    if (Array.IndexOf(marks, 0L) is var free and >= 0) marks[free] = req.MarkId;
                    else result = ErrorCodes.Parameter;
                    break;
                case CommandMarkUpdateType.Remove when Array.IndexOf(marks, req.MarkId) is var at and >= 0 && req.MarkId > 0:
                    marks[at] = 0;
                    break;
                case CommandMarkUpdateType.Clear:
                    Array.Clear(marks);
                    break;
                default:
                    result = ErrorCodes.Parameter;
                    break;
            }
        }
        await Send(s, CommandMark, new GvgWarUpdateCommandMarkResp { Result = result });
        if (result == 0)
            await RoleFanout.EachAsync(side!.Value.Members, p => p.SendGameAsync(CommandMarkNotify, CommandMarkSnapshot(marks)));
    }

    private static GvgWarUpdateCommandMarkNotify CommandMarkSnapshot(long[]? marks)
    {
        var notify = new GvgWarUpdateCommandMarkNotify();
        notify.MarkIds.Add(marks ?? new long[CommandMarkSlots]);
        return notify;
    }

    /// <summary>On loading into the battle: the side's commander marks, the party's own marks, the command letters and the
    /// command skills.</summary>
    private async Task SendCommandBoardAsync(GameSession s)
    {
        if (CommandSideOf(s) is not { } side) return;
        await s.SendGameAsync(MarkNotify, MarkSnapshot(side.Board.CommanderMarks.GetValueOrDefault(side.Guild), DungeonMarkType.DungeonMarkCommander));
        long team = _teams.TeamOf(s.Role!.Id).TeamId;
        await s.SendGameAsync(MarkNotify, MarkSnapshot(team > 0 ? side.Board.TeamMarks.GetValueOrDefault(team) : null,
            DungeonMarkType.DungeonMarkTeamLeader));
        await s.SendGameAsync(CommandMarkNotify, CommandMarkSnapshot(side.Board.CommandMarks.GetValueOrDefault(side.Guild)));
        await s.SendGameAsync(CommandSkillNotify, CommandSkillSnapshot(CommandSkillsOf(side), CommandRules(s), CommandNow));
    }
}
