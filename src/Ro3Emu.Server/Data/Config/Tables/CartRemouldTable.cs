using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's CartRemouldTable (data/config/CartRemouldTable.json).</summary>
[ConfigTable("CartRemouldTable")]
public readonly partial record struct CartRemouldTable(JsonElement Row)
{
    [ConfigField("_iPendantId")] public partial long PendantId { get; }
    [ConfigField("_iSubType")] public partial long SubType { get; }
    [ConfigField("_iTaskId")] public partial long TaskId { get; }
    [ConfigField("_iType")] public partial long Type { get; }
    [ConfigField("_iUnlockBaseLv")] public partial long UnlockBaseLv { get; }
    [ConfigField("_kBindBuff")] public partial long[] BindBuff { get; }
    [ConfigField("_kJob")] public partial long[] Job { get; }
    [ConfigField("_kProfessionID")] public partial long[] ProfessionId { get; }
    [ConfigField("_kUnlockSkillID")] public partial long UnlockSkillId { get; }
}
