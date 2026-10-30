using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's ItemConfig (data/config/ItemConfig.json).</summary>
[ConfigTable("ItemConfig")]
public readonly partial record struct ItemConfig(JsonElement Row)
{
    [ConfigField("_iAuction")] public partial long Auction { get; }
    [ConfigField("_iAward")] public partial long Award { get; }
    [ConfigField("_iBagTab")] public partial long BagTab { get; }
    [ConfigField("_iCD")] public partial long Cd { get; }
    [ConfigField("_iCardGroup")] public partial long CardGroup { get; }
    [ConfigField("_iCardPart")] public partial long CardPart { get; }
    [ConfigField("_iCanAuto")] public partial long[] CanAuto { get; }
    /// <summary>[text id, @{1}, @{2}, ...]: the description and the values its placeholders show.</summary>
    [ConfigField("_iDescription")] public partial JsonElement? Description { get; }
    [ConfigField("_iEnchantSlot")] public partial long EnchantSlot { get; }
    [ConfigField("_iEquipId")] public partial long EquipId { get; }
    [ConfigField("_iEquipPart")] public partial long EquipPart { get; }
    [ConfigField("_iGroupUse")] public partial long GroupUse { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iIfSameCard")] public partial long IfSameCard { get; }
    [ConfigField("_iIsEleCard")] public partial long IsEleCard { get; }
    [ConfigField("_iIsUse")] public partial long IsUse { get; }
    [ConfigField("_iLevelNeed")] public partial long LevelNeed { get; }
    [ConfigField("_iName")] public partial long[] Name { get; }
    [ConfigField("_iQuality")] public partial long Quality { get; }
    [ConfigField("_iSeasonResetType")] public partial long SeasonResetType { get; }
    [ConfigField("_iSexNeed")] public partial long SexNeed { get; }
    [ConfigField("_iSort")] public partial long Sort { get; }
    [ConfigField("_iStackLimit")] public partial long StackLimit { get; }
    [ConfigField("_iSubType")] public partial long SubType { get; }
    [ConfigField("_iTrade")] public partial long Trade { get; }
    [ConfigField("_iType")] public partial long Type { get; }
    [ConfigField("_iUseEffect")] public partial JsonElement? UseEffect { get; }
    [ConfigField("_kCDGroup")] public partial long[] CDGroup { get; }
    [ConfigField("_kCanCart")] public partial long CanCart { get; }
    [ConfigField("_kCombine")] public partial long[][] Combine { get; }
    [ConfigField("_kDecompose")] public partial long[][] Decompose { get; }
    [ConfigField("_kIcon")] public partial string Icon { get; }
    [ConfigField("_kJobNeed")] public partial long[] JobNeed { get; }
    [ConfigField("_kMultiverseArray")] public partial long[] MultiverseArray { get; }
    [ConfigField("_kParam1")] public partial string Param1 { get; }
    /// <summary>The item's large picture (a card's art); "item_null_*" when it has none.</summary>
    [ConfigField("_kPic")] public partial string Pic { get; }
    [ConfigField("_kUseTimes")] public partial long[] UseTimes { get; }
}
