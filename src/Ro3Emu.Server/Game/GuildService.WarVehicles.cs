using System.Collections.Concurrent;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// War vehicles from the guild warehouse (docs/systems/territory-wars.md "War vehicles"). A vehicle item of the siege
/// battle bag (ItemConfig _iSubType 1402) answers its "use" button with 32281 (UI_Com_ItemTipsWidgetCtrl_Operation
/// _gameplayUseVehicle -> LC_WartimeItemHelper.TryUseVehicle); its ItemConfig _iUseEffect [606, skill] names the skill
/// whose script GVG_S0_BuildMecha raises the NPC of its _kLogicData[0] (Tank 4903501 -> NPC 4900301, Madogear 4903201..401 ->
/// 4900401..403, Jet Backpack 4903601 -> 4900501). The answer only clears the bag's busy state
/// (LC_DAL_Guild.ReceiveGuildVehicleUseRsp); the vehicle itself arrives through the AOI and is boarded by its interactions
/// (TerritoryWarsService.WarVehicles).
/// </summary>
public sealed partial class GuildService
{
    public const uint VehicleUse = 32281;
    private const long UseSkillEffect = 606;
    private const string BuildMechaScript = "GVG_S0_BuildMecha", RepairAreaScript = "GVG_S0_RepairArea";

    /// <summary>Each member's standing vehicle (the roleUsingToolId of its Vehicle slot); runtime only, like cannons.</summary>
    private readonly ConcurrentDictionary<long, long> _vehicleOf = new();

    /// <summary>The NPC a vehicle item raises (_iUseEffect 606 -> GVG_S0_BuildMecha's _kLogicData[0]), or 0.</summary>
    internal int VehicleNpc(int item) =>
        UseSkill(item) is { ScriptLogic: BuildMechaScript, LogicData: [var npc, ..] } && NPCConfig.Get(data, npc) != null ? (int)npc : 0;

    /// <summary>The GVG_S0_RepairArea skill of a repair item (1006010 Madogear Pilot Repair: _iUseEffect 606 -> 4901901), or 0.</summary>
    internal long RepairSkill(int item) => UseSkill(item) is { ScriptLogic: RepairAreaScript } skill ? skill.Id : 0;

    /// <summary>The skill of an item's _iUseEffect 606 (use a skill).</summary>
    private SkillConfig? UseSkill(int item)
    {
        if (item <= 0 || ItemConfig.Get(data, item)?.UseEffect is not { ValueKind: System.Text.Json.JsonValueKind.Array } effects)
            return null;
        foreach (var effect in effects.EnumerateArray())
            if (effect.ValueKind == System.Text.Json.JsonValueKind.Array && effect.GetArrayLength() >= 2
                && GameData.Number(effect[0]) == UseSkillEffect && GameData.Number(effect[1]) is { } skill
                && SkillConfig.Get(data, (long)skill) is { } row)
                return row;
        return null;
    }

    public Task VehicleUseAsync(GameSession s, GuildVehicleUseReq req) => RunGuildAsync(s, [], [], () => VehicleUseCoreAsync(s, req));

    /// <summary>
    /// 32281: one vehicle leaves the warehouse and stands beside the member. (inferred) As for the cannons the member
    /// needs the Vehicle permission of the Tactics panel (the battle bag only lists that type, 32285 and
    /// CheckGVGPermission 2202 on the seats), the item a vehicle, stock left (535) and a running siege; one vehicle per
    /// member, a second one replaces the first (as UIWords 61001 says of cannons).
    /// </summary>
    private async Task VehicleUseCoreAsync(GameSession s, GuildVehicleUseReq req)
    {
        var g = Own(s);
        int item = req.VehicleItem?.ItemId ?? 0;
        int npc = VehicleNpc(item);
        long repair = npc == 0 ? RepairSkill(item) : 0;
        var site = WarMachines;
        int code = g == null ? ErrorCodes.TerritoryGuild
            : WarSideOf?.Invoke(s) is not > 0 || site == null ? ErrorCodes.FeatureUnavailable
            : npc == 0 && repair == 0 ? ErrorCodes.Parameter
            : ToolOf(g, s.Role!.Id) != WartimeToolType.Vehicle ? ErrorCodes.GuildPermission : 0;
        if (code == 0)
        {
            Stock(g!);
            if (g!.WartimeItems.GetValueOrDefault(item) <= 0) code = ErrorCodes.GuildSupplies;
        }
        if (code != 0)
        {
            await s.SendGameAsync(VehicleUse, new GuildVehicleUseRsp { Result = code });
            return;
        }
        g!.WartimeItems[item]--;
        await SaveGuildAsync(g);
        if (repair != 0)
        {
            await RepairUseAsync(s, g, item, repair, site!);
            return;
        }
        long uid = await site!.RaiseVehicleAsync(s, npc);
        if (uid == 0)
        {
            g.WartimeItems[item]++;
            await SaveGuildAsync(g);
            await s.SendGameAsync(VehicleUse, new GuildVehicleUseRsp { Result = ErrorCodes.FeatureUnavailable });
            return;
        }
        long role = s.Role!.Id;
        if (_vehicleOf.TryGetValue(role, out long old) && old != uid) await site.RemoveAsync(old);
        _vehicleOf[role] = uid;
        await s.SendGameAsync(VehicleUse, new GuildVehicleUseRsp());
        await PushStoreAsync(g);
        await PushToolPermissionAsync(g, role);
        await PushToolChangesAsync(g, [.. SlotsOf(g, role)]);
    }

    /// <summary>
    /// A repair item of the vehicle bag (1006010, 32281 like a vehicle: LC_WartimeItemHelper.TryUseVehicle): its repair
    /// zone stands at the member's feet; the item goes back to the warehouse if it cannot.
    /// </summary>
    private async Task RepairUseAsync(GameSession s, GuildStore.State g, int item, long skill, IWarMachineSite site)
    {
        if (!await site.RaiseRepairZoneAsync(s, item, skill))
        {
            g.WartimeItems[item]++;
            await SaveGuildAsync(g);
            await s.SendGameAsync(VehicleUse, new GuildVehicleUseRsp { Result = ErrorCodes.FeatureUnavailable });
            return;
        }
        await s.SendGameAsync(VehicleUse, new GuildVehicleUseRsp());
        await PushStoreAsync(g);
    }

    /// <summary>The Tactics machine type of the player (SiegePermissionSelfData eToolType), None outside a guild slot.</summary>
    internal WartimeToolType ToolOfRole(GameSession s) => Own(s) is { } g ? ToolOf(g, s.Role!.Id) : WartimeToolType.None;
}
