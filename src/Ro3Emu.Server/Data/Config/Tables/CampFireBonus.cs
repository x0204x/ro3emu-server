using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>
/// The client's CampFireBonus (data/config/CampFireBonus.json, read by no client Lua): the Bonfire Party prize of a
/// transformation. The key _iBuffid is the transform buff (7020101-7020503: the monster _iTypeid, Poring 5000001 to
/// Baphomet Jr. 5000073, at size 1-3), _iSizeBuffId its size buff; _iBonusLevelFirst/Second/Third are the
/// MultiverseGroupConfig awards of the First/Second/Third Prize (fuel, currency 100 and the Primordial Transmutation
/// Manual) and _iBonus*Show the matching GuildCampFireLuckyPlayerInfo.bonusShow.
/// </summary>
[ConfigTable("CampFireBonus")]
public readonly partial record struct CampFireBonus(JsonElement Row)
{
    [ConfigField("_iBuffid")] public partial long BuffId { get; }
    [ConfigField("_iTypeid")] public partial long TypeId { get; }
    [ConfigField("_iSizeBuffId")] public partial long SizeBuffId { get; }
    [ConfigField("_iBonusLevelFirst")] public partial long BonusLevelFirst { get; }
    [ConfigField("_iBonusLevelSecond")] public partial long BonusLevelSecond { get; }
    [ConfigField("_iBonusLevelThird")] public partial long BonusLevelThird { get; }
    [ConfigField("_iBonusFirstShow")] public partial long BonusFirstShow { get; }
    [ConfigField("_iBonusSecondShow")] public partial long BonusSecondShow { get; }
    [ConfigField("_iBonusThirdShow")] public partial long BonusThirdShow { get; }

    /// <summary>The award group and the shown value of a prize level (1 First, 2 Second, 3 Third).</summary>
    public (long Group, long Show) Prize(long level) => level switch
    {
        1 => (BonusLevelFirst, BonusFirstShow),
        2 => (BonusLevelSecond, BonusSecondShow),
        3 => (BonusLevelThird, BonusThirdShow),
        _ => (0, 0),
    };
}
