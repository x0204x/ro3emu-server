using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Where the war machines stand (docs/systems/territory-wars.md "War machines in territory battles"): the running siege
/// and the territory battles of DungeonConfig _iGamePlayType 18 (TerritoryWars). Type 18 mounts the UI template
/// Battle_TerritoryWars (LC_MainUIDefine), whose UI_Sys_Dungeon_TerritoryWars_ButtonWidget has the siege's Manage, Command
/// and Instrument buttons (the battle bag for the Firearm permission), and LC_DAL_TerritoryWars keeps the 32285
/// permission too. Of the 36 GVGWarZoneConfig battles only 3030401 (zone 2), 3030101 (3), 3030201 (6) and 3030301 (7) are
/// type 18; the others are type 6 (Single) and have no such buttons, so machines are refused there (inferred).
/// In a siege the two sides are camps (attack 1, defense 2); in a territory battle every player is camp 1 and the client
/// tells friend from enemy by guild (LC_CampRelationStrategy_TerritoryWars: the head info Guild, else custom data 104
/// BelongGuild), so a machine there belongs to its builder's guild.
/// </summary>
public sealed partial class TerritoryWarsService
{
    /// <summary>enum_DungeonGameplayType TerritoryWars (Lua_GameDefine).</summary>
    private const long TerritoryWarsGameplay = 18;
    /// <summary>enum_CustomData BelongGuild (Lua_CustomDataDefine): a unit's guild when its head info has none.</summary>
    private const long BelongGuild = 104;

    internal enum WarKind { Siege, Territory }

    /// <summary>
    /// The battle room a member fights in with war machines. <paramref name="Side"/> is GuildService.WarSideOf's: 1 attack
    /// and 2 defense in a siege, 3 in a territory battle (the territory camp of a battle participant, RoleState.Camp).
    /// <paramref name="Guild"/> is the guild the member fights for.
    /// </summary>
    private sealed record WarRoom(InstanceService.Room Room, WarKind Kind, int Side, long Guild);

    /// <summary>
    /// The running siege the member is loaded into and fights for, or the active type-18 territory battle they take part in,
    /// else null. Read outside the territory gate: a battle record only changes its settled flag and end, and a retired
    /// epoch reads as no battle.
    /// </summary>
    private WarRoom? WarRoomOf(GameSession s)
    {
        try
        {
            if (s.SiegeBattle is { } siege && !siege.Room.Closed && SiegeActive(siege) && SiegeMember(s, siege))
            {
                long guild = siege.State.Players[s.Role!.Id].Guild;
                return new WarRoom(siege.Room, WarKind.Siege, guild == siege.State.Attack ? 1 : 2, guild);
            }
            if (s.TerritoryBattle is { } battle && !battle.Room.Closed && Active(battle) && Member(s, battle) && MachineBattle(battle))
                return new WarRoom(battle.Room, WarKind.Territory, 3, battle.State.Players[s.Role!.Id].Guild);
        }
        catch (InvalidOperationException) { }
        catch (KeyNotFoundException) { }
        return null;
    }

    /// <summary>A territory battle whose dungeon is DungeonConfig _iGamePlayType 18 (the war machine UI).</summary>
    private bool MachineBattle(Battle battle) => DungeonConfig.Get(_data, battle.State.Scene)?.GamePlayType == TerritoryWarsGameplay;

    /// <summary>
    /// A machine of <paramref name="camp"/> and <paramref name="guild"/> is on the member's side: the same camp in a siege
    /// (LC_CommonCondition.CheckCamp compares EntityCamp), the same guild in a territory battle, where every camp is 1 and
    /// the client's relation is the guild's.
    /// </summary>
    private bool OwnSide(GameSession s, long camp, long guild) =>
        WarRoomOf(s) is { Kind: WarKind.Territory } war ? war.Guild == guild : camp == CampOf(s);

    /// <summary>The guild of a cannon or vehicle, or null for any other unit.</summary>
    private static long? MachineGuild(MonsterUnit m) => m switch
    {
        CannonUnit cannon => cannon.Guild,
        VehicleUnit vehicle => vehicle.Guild,
        _ => null,
    };

    /// <summary>A machine's guild on its entity: guildId (EntityBase 59) and custom data 104 BelongGuild.</summary>
    private static void AddMachineGuild(EntityBase entity, long guild)
    {
        if (guild == 0) return;
        entity.GuildId = guild;
        entity.CustomDatas.Add(CannonLong(BelongGuild, guild));
    }
}
