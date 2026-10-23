using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's TenGameplayBossConfig (data/config/TenGameplayBossConfig.json).</summary>
[ConfigTable("TenGameplayBossConfig")]
public readonly partial record struct TenGameplayBossConfig(JsonElement Row)
{
    [ConfigField("_iBlessingBuffUnlockType")] public partial long BlessingBuffUnlockType { get; }
    [ConfigField("_iBoss")] public partial long Boss { get; }
    [ConfigField("_iDungeon")] public partial long Dungeon { get; }
    [ConfigField("_iOriginalReviveNum")] public partial long OriginalReviveNum { get; }
    [ConfigField("_iPersonAward")] public partial long PersonAward { get; }
    [ConfigField("_kBlessingBuffUnlock")] public partial long BlessingBuffUnlock { get; }
    [ConfigField("_kBlessingID")] public partial long BlessingId { get; }
    [ConfigField("_kBondsID")] public partial long[] BondsId { get; }
    [ConfigField("_kFinalTrialID")] public partial long[] FinalTrialId { get; }
    [ConfigField("_kUnlock")] public partial long Unlock { get; }
}
