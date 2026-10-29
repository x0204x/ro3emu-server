using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The commander's skills of a siege or territory battle, from the scenes' DungeonBase.CommandModule (DungeonLua_2230101
/// for the siege; PublicParamConfig PointWarCommandSkillConfigDungeonId names DungeonLua_3030101's for territory
/// battles) and the client's handlers:
/// <list type="bullet">
/// <item>The Command button shows for guild right 12 BattleFieldCommand (UI_Sys_Dungeon_Siege_ButtonWidgetCtrl); its
/// panel casts with the ordinary 31102 at the caster's own position (UI_Sys_Siege_BattlefieldSkillWidgetCtrl._useSkill),
/// so the skills come through CombatService's cast path as its OwnSkill* hooks: no job tree, no SkillConfig cooldowns.</item>
/// <item>30439 DungeonCommandSkillNotify replaces the panel's whole state (LC_SceneCom_Command): per skill its cooldown,
/// used and maximum charges and the recovery of a used charge, and the common cooldown, all in epoch ms. The state is the
/// side's (inferred: the panel is the guild's, and any commander of it casts from the same charges).</item>
/// <item>4909001 Blessing of Light and 4909101 Battle Command put their _kBuff1 on the side within the skill's 20 m
/// (_kRange 2000); 4909201 Defend the Glory revives up to _iTargetMax (20) fallen allies there with 70% HP and SP
/// (_kDamageParamExtra [[2,7000,101,3]], "with 70% HP and MP"); 4909301 Emergency Call invites the side to the caster.
/// Their selector templates are hostile (skill_4909x m_kSelectType 1), so the generic path would only buff the caster.</item>
/// <item>Emergency Call (teleportSkill: 20 s, 20 players, radius 1000): 30118 invites with the commander's id, the
/// invite tip accepts with 30119, 30122 counts the accepted, 30120 clears the tips (LC_DAL_Dungeon). The client words
/// only two refusals, 828 dead and 5203 mounted; 292 expired and 293 full are ErrorCodeConfig's "instance teleport
/// skill" rows (inferred).</item>
/// </list>
/// Inferred: a charge recovers skillRecoverCd after it was used, one at a time; each use starts skillCd and the common
/// cooldown; an arrival lands at the commander's position at the cast.
/// </summary>
public sealed partial class TerritoryWarsService
{
    public const uint CommandSkillNotify = 30439, TeleportInvite = 30118, TeleportResponse = 30119, TeleportClear = 30120,
        TeleportCount = 30122;
    internal const int BlessingOfLight = 4909001, BattleCommand = 4909101, DefendTheGlory = 4909201, EmergencyCall = 4909301;
    private const long CommandCommonCdMs = 5000, CallMs = 20000;
    private const int CallMaxPlayers = 20, CommandReviveRate = 7000;

    private readonly record struct CommandSkillRule(int MaxUses, long CdMs, long RecoverMs);

    // DungeonLua_2230101 CommandModule.skills: Blessing of Light's skillCd is 20000 in the siege and 180000 elsewhere.
    private static readonly Dictionary<int, CommandSkillRule> SiegeCommandSkills = new()
    {
        [BlessingOfLight] = new(2, 20000, 180000), [BattleCommand] = new(1, 360000, 360000),
        [DefendTheGlory] = new(1, 600000, 600000), [EmergencyCall] = new(1, 900000, 900000),
    };

    // DungeonLua_3030101 CommandModule.skills (PointWarCommandSkillConfigDungeonId).
    private static readonly Dictionary<int, CommandSkillRule> TerritoryCommandSkills = new()
    {
        [BlessingOfLight] = new(2, 180000, 180000), [BattleCommand] = new(1, 360000, 360000),
        [DefendTheGlory] = new(1, 600000, 600000), [EmergencyCall] = new(1, 900000, 900000),
    };

    /// <summary>One side's command skills: the charges and cooldowns, and its open Emergency Call.</summary>
    internal sealed class CommandSkillState
    {
        public Dictionary<int, DungeonCommandSkillInfo> Skills { get; } = [];
        public long CommonCdStart { get; set; }
        public long CommonCdEnd { get; set; }
        public EmergencyCallState? Call { get; set; }
    }

    internal sealed class EmergencyCallState
    {
        public long Inviter { get; init; }
        public Vec3 Origin { get; init; } = new();
        public long Until { get; init; }
        public HashSet<long> Invited { get; } = [];
        public HashSet<long> Accepted { get; } = [];
    }

    private static long CommandNow => MonsterService.NowMs;

    private static Dictionary<int, CommandSkillRule> CommandRules(GameSession s) => s.SiegeBattle != null ? SiegeCommandSkills : TerritoryCommandSkills;

    private static CommandSkillState CommandSkillsOf(CommandSide side) =>
        side.Board.Skills.TryGetValue(side.Guild, out var state) ? state : side.Board.Skills[side.Guild] = new();

    /// <summary>The skill's info with its recovered charges counted back in.</summary>
    private static DungeonCommandSkillInfo SkillInfo(CommandSkillState state, int skill, CommandSkillRule rule, long now)
    {
        if (!state.Skills.TryGetValue(skill, out var info))
            state.Skills[skill] = info = new DungeonCommandSkillInfo { SkillId = skill, MaxUseTimes = rule.MaxUses };
        while (info.UseTimes > 0 && info.RecoverEnd > 0 && info.RecoverEnd <= now)
        {
            info.UseTimes--;
            if (info.UseTimes > 0) { info.RecoverStart = info.RecoverEnd; info.RecoverEnd += rule.RecoverMs; }
        }
        return info;
    }

    /// <summary>CombatService.OwnSkillAdmit: null unless a command skill, else 0 or the refusal.</summary>
    private int? CommandSkillAdmit(GameSession s, int skill)
    {
        if (!SiegeCommandSkills.ContainsKey(skill)) return null;
        if (CommandSideOf(s) is not { } side || !CommandRules(s).TryGetValue(skill, out var rule)) return ErrorCodes.SkillRequirements;
        if (!Commands(s, side.Guild)) return ErrorCodes.NoPermission;
        var state = CommandSkillsOf(side);
        long now = CommandNow;
        var info = SkillInfo(state, skill, rule, now);
        return state.CommonCdEnd > now || info.SkillCdEnd > now || info.UseTimes >= rule.MaxUses ? ErrorCodes.SkillCooldown : 0;
    }

    /// <summary>CombatService.OwnSkillCast: the accepted cast spends a charge and starts the cooldowns.</summary>
    private async Task CommandSkillCastAsync(GameSession s, int skill)
    {
        if (CommandSideOf(s) is not { } side || !CommandRules(s).TryGetValue(skill, out var rule)) return;
        var state = CommandSkillsOf(side);
        long now = CommandNow;
        var info = SkillInfo(state, skill, rule, now);
        if (info.UseTimes == 0 || info.RecoverEnd <= now) { info.RecoverStart = now; info.RecoverEnd = now + rule.RecoverMs; }
        info.UseTimes++;
        info.SkillCdStart = now; info.SkillCdEnd = now + rule.CdMs;
        state.CommonCdStart = now; state.CommonCdEnd = now + CommandCommonCdMs;
        await RoleFanout.EachAsync(side.Members, p => p.SendGameAsync(CommandSkillNotify, CommandSkillSnapshot(state, CommandRules(s), now)));
        // The panel counts the charge back only from a fresh notify (inferred: one when it recovers).
        var commander = s;
        _loop.ScheduleRuntime(TimeSpan.FromMilliseconds(Math.Max(0, info.RecoverEnd - now)), async () =>
        {
            if (CommandSideOf(commander) is { } current) await SendCommandSkillsAsync(current, commander);
        });
    }

    private static DungeonCommandSkillNotify CommandSkillSnapshot(CommandSkillState state, Dictionary<int, CommandSkillRule> rules, long now)
    {
        var notify = new DungeonCommandSkillNotify { CommonCdStartMs = state.CommonCdStart, CommonCdEndMs = state.CommonCdEnd };
        foreach (var (skill, rule) in rules) notify.SkillInfos[skill] = SkillInfo(state, skill, rule, now).Clone();
        return notify;
    }

    private static Task SendCommandSkillsAsync(CommandSide side, GameSession anyMember) =>
        RoleFanout.EachAsync(side.Members, p => p.SendGameAsync(CommandSkillNotify,
            CommandSkillSnapshot(CommandSkillsOf(side), CommandRules(anyMember), CommandNow)));

    /// <summary>CombatService.OwnSkillLanded: the landed command skill's effect on the caster's side.</summary>
    private async Task CommandSkillLandedAsync(GameSession s, JsonElement row)
    {
        if (CommandSideOf(s) is not { } side || s.Role is not { } role) return;
        var config = new SkillConfig(row);
        float radius = config.Range.FirstOrDefault() / 100f;
        var near = side.Members.Where(p => p.Role != null && !p.SceneLoading && p.WorldScene == s.WorldScene
                && MonsterService.Distance(p.Position.X, p.Position.Z, s.Position.X, s.Position.Z) <= radius)
            .OrderBy(p => MonsterService.Distance(p.Position.X, p.Position.Z, s.Position.X, s.Position.Z)).ToArray();
        switch ((int)config.Id)
        {
            case BlessingOfLight or BattleCommand:
                foreach (var ally in near.Where(p => !p.Dead).Take((int)Math.Max(1, config.TargetMax)))
                    foreach (var buff in config.Buff1 ?? [])
                        if (buff.Length >= 3) await _buffs.AddAsync(ally, (int)buff[0], (int)buff[2], role.Id);
                break;
            case DefendTheGlory:
                foreach (var fallen in near.Where(p => p.Dead).Take((int)Math.Max(1, config.TargetMax)))
                    if (await ReviveByCommandAsync(fallen))
                        foreach (var buff in config.Buff1 ?? [])
                            if (buff.Length >= 3) await _buffs.AddAsync(fallen, (int)buff[0], (int)buff[2], role.Id);
                break;
            case EmergencyCall:
                await StartEmergencyCallAsync(s, side);
                break;
        }
    }

    /// <summary>A fallen ally back to life where it lies, through the shared revive claim.</summary>
    private async Task<bool> ReviveByCommandAsync(GameSession fallen)
    {
        if (_buffs.HasForbidden(fallen, ForbiddenState.Revive) || !_combat.TryClaimRevive(fallen)) return false;
        try
        {
            if (!_combat.CommitReviveClaim(fallen, CommandReviveRate, CommandReviveRate)) return false;
            await _combat.PublishReviveAsync(fallen, null);
            return true;
        }
        finally { _combat.ReleaseReviveClaim(fallen); }
    }

    private async Task StartEmergencyCallAsync(GameSession s, CommandSide side)
    {
        var state = CommandSkillsOf(side);
        if (state.Call is { } old) await ClearCallAsync(side, old);
        var call = new EmergencyCallState { Inviter = s.Role!.Id, Origin = s.Position.Clone(), Until = CommandNow + CallMs };
        foreach (var p in side.Members.Where(p => p != s && p.Role != null)) call.Invited.Add(p.Role!.Id);
        state.Call = call;
        await RoleFanout.EachAsync(side.Members.Where(p => p != s), p => p.SendGameAsync(TeleportInvite,
            new DungeonTeleportInviteNotify { InviteGuid = call.Inviter }));
        await RoleFanout.EachAsync(side.Members, p => p.SendGameAsync(TeleportCount, CallCount(call)));
        var commander = s;
        _loop.ScheduleRuntime(TimeSpan.FromMilliseconds(CallMs), async () =>
        {
            if (CommandSideOf(commander) is { } current && CommandSkillsOf(current).Call == call) await ClearCallAsync(current, call);
        });
    }

    private static DungeonTeleportAcceptCountNotify CallCount(EmergencyCallState call) =>
        new() { InviterGuid = call.Inviter, AcceptCount = call.Accepted.Count, MaxCount = CallMaxPlayers };

    private static async Task ClearCallAsync(CommandSide side, EmergencyCallState call)
    {
        var state = CommandSkillsOf(side);
        if (state.Call == call) state.Call = null;
        await RoleFanout.EachAsync(side.Members, p => p.SendGameAsync(TeleportClear, new DungeonTeleportInviteClearNotify()));
    }

    /// <summary>30119: an invited ally accepts the call and lands at the commander.</summary>
    private async Task TeleportResponseAsync(GameSession s, DungeonTeleportResponseReq req)
    {
        CommandSide? side = CommandSideOf(s);
        var call = side is { } open ? CommandSkillsOf(open).Call : null;
        int result = call == null || !call.Invited.Contains(s.Role!.Id) || call.Until <= CommandNow ? ErrorCodes.TeleportExpired
            : call.Accepted.Count >= CallMaxPlayers ? ErrorCodes.TeleportFull
            : s.Dead ? ErrorCodes.TeleportDead
            : MountService.Rider(s) != 0 ? ErrorCodes.TeleportRiding : 0;
        if (result == 0 && req.IsAgree)
        {
            call!.Accepted.Add(s.Role!.Id);
            call.Invited.Remove(s.Role.Id);
        }
        await Send(s, TeleportResponse, new DungeonTeleportResponseRsp { Result = result });
        if (result != 0 || !req.IsAgree) return;
        await _scene.TeleportInMapAsync(s, new MapPoint(call!.Origin.X, call.Origin.Y, call.Origin.Z, s.Face));
        await _buffs.AddAsync(s, EmergencyCall, 500, call.Inviter);
        await RoleFanout.EachAsync(side!.Value.Members, p => p.SendGameAsync(TeleportCount, CallCount(call)));
        if (call.Accepted.Count >= CallMaxPlayers) await ClearCallAsync(side.Value, call);
    }
}
