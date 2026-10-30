using Romsg;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class GmCommands
{
    /// <summary>GmOpEnum values only the GMCombinationConfig presets use (GMConfig buttons 1030-1032, 3008-3011).</summary>
    private const int OpSkillQueue = 38, OpJobProfessSkill = 170, OpJobProfessAttr = 171, OpSetSkillWheel = 172, OpEquipWeapon = 173,
        OpCreateLvEmblem = 174, OpEquipMount = 175, OpEquipPendant = 176, OpWarRobe = 251, OpSeasonTalent = 261, OpPlayerAsset = 999;

    private const int ItemTypeEquip = 5; // ItemConfig._iType

    /// <summary>GM panel ops that only GMCombinationConfig presets use (builds: weapon, mount, pendant, emblem, skills, talents); null when the op is not one of them.</summary>
    private async Task<string?> PresetOpAsync(GameSession s, int op, string[] args) => op switch
    {
        // ["GmPlayerAsset","openallfunc"]: the only subcommand the client data names.
        OpPlayerAsset => await PlayerAssetAsync(s, args),
        // ["GmjobProfessSkill","101"] / ["GmjobProfessAttr","101"]: a JobProfessConfig build (school) id.
        OpJobProfessSkill => await services.GetRequiredService<SkillService>().GmProfessionSkillsAsync(s, Profession(args[0])),
        OpJobProfessAttr => await character.SetProfessionStatsAsync(s, Profession(args[0])),
        // ["GmsetSkillWheel", six SkillConfig row ids] and ["GmskillQueue", skill group ids...].
        OpSetSkillWheel => await services.GetRequiredService<SkillService>().GmSkillBarAsync(s, [.. args.Select(L)]),
        OpSkillQueue => await services.GetRequiredService<SkillService>().GmSkillQueueAsync(s, [.. args.Select(I)]),
        OpEquipWeapon => await EquipAsync(s, I(args[0])),
        // ["GmcreateLvEmblem", item, level].
        OpCreateLvEmblem => await services.GetRequiredService<EmblemService>().GmCreateAsync(s, I(args[0]), args.Length > 1 ? I(args[1]) : 0),
        // ["GmequipMount", CartRemouldTable id, "1" (ride)?] and ["GmequipPendant", CartRemouldTable id].
        OpEquipMount => await services.GetRequiredService<CareerSpecialService>().GmLeaseAsync(s, L(args[0]), mount: true,
            ride: args.Length > 1 && args[1] == "1"),
        OpEquipPendant => await services.GetRequiredService<CareerSpecialService>().GmLeaseAsync(s, L(args[0]), mount: false, ride: false),
        // ["GmseasonTalent","upgradetree", tree].
        OpSeasonTalent => args.Length > 1 && args[0].Equals("upgradetree", StringComparison.OrdinalIgnoreCase)
            ? await services.GetRequiredService<SeasonTalentService>().GmUpgradeTreeAsync(s, L(args[1]))
            : $"GmSeasonTalent {string.Join(',', args)}: only upgradetree <tree> is known (the presets' one subcommand).",
        // ["251","lightupallappearance"].
        OpWarRobe => args.Length > 0 && args[0].Equals("lightupallappearance", StringComparison.OrdinalIgnoreCase)
            ? await services.GetRequiredService<WardrobeService>().GmLightUpAllAsync(s)
            : $"GMWarRobe {string.Join(',', args)}: only lightupallappearance is known (the presets' one subcommand).",
        _ => null,
    };

    /// <summary>
    /// A JobProfessConfig id as the client reads it: its _iProfessionID keys the recommended stats and skills
    /// (Lua_CFG_JobProfessConfig.GetProfessionId before JobProfessPointConfig / JobProfessSkillConfig).
    /// </summary>
    private long Profession(string arg) =>
        JobProfessConfig.Get(data, L(arg)) is { } build ? build.ProfessionId : throw new ArgumentException($"no JobProfessConfig {arg}");

    /// <summary>
    /// "openallfunc": every FunctionConfig function counts as open (FunctionService.IsOpen), then 5056 and the other
    /// function-gated datasets go out as after a level change. (inferred) For this role session only: the emulator
    /// computes openings from their conditions and keeps no opened set to persist.
    /// </summary>
    private async Task<string> PlayerAssetAsync(GameSession s, string[] args)
    {
        if (args.Length == 0 || !args[0].Equals("openallfunc", StringComparison.OrdinalIgnoreCase))
            return $"GmPlayerAsset {string.Join(',', args)}: only openallfunc is known (the presets' one subcommand).";
        s.GmAllFunctionsOpen = true;
        foreach (var handler in character.ProgressHandlers) await handler(s);
        return "Every function is open for this role session.";
    }

    /// <summary>
    /// GMEquipWeapon 173: wear an instance of the item from the bag (one is granted when the role has none), through
    /// 31400's own path, so 5009, 5007, the reply and the model change go out as for the client's equip; the grid is the
    /// item's own (EquipPosType). (inferred) A second step for the same item (the dual-dagger presets 5402, 6511 add two)
    /// wears the second copy; where 31400 gives the item one grid it replaces the first, and the reply says so.
    /// </summary>
    private async Task<string> EquipAsync(GameSession s, int entry)
    {
        if (ItemConfig.Get(data, entry) is not { Type: ItemTypeEquip }) return $"{entry} is not equipment.";
        string name = $"{data.ItemName(entry)} ({entry})";
        int worn = s.Items.Count(i => i.Entry == entry && i.Grid > 0);
        if (!s.Items.Any(i => i.Entry == entry && i.Grid == 0))
        {
            if (worn > 0) return $"{name} is already worn.";
            await character.AddItemAsync(s, entry, 1);
        }
        if (s.Items.Where(i => i.Entry == entry && i.Grid == 0).MaxBy(i => i.Guid) is not { } item) return $"{name} could not be granted.";
        await character.EquipAsync(s, new EquipItemOprReq { ItemGUID = item.Guid, Opr = EquipItemOprType.EquipOprTypeEquip });
        if (s.Items.FirstOrDefault(i => i.Guid == item.Guid) is not { Grid: > 0 } equipped)
            return $"{name} could not be equipped (not appraised, or no grid for it).";
        return $"Equipped {name} in grid {equipped.Grid}"
            + (worn > 0 && s.Items.Count(i => i.Entry == entry && i.Grid > 0) == worn ? ", replacing the copy worn there." : ".");
    }
}
