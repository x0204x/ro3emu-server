using System.Text.Json;
namespace Ro3Emu.Server.Data.Config;
/// <summary>G 酒館分類；0 為房間類型，1 為語言，兩者不可互換。</summary>
[ConfigTable("BarroomTypeConfig")]
public readonly partial record struct BarroomTypeConfig(JsonElement Row)
{
    [ConfigField("_iType")] public partial long Type { get; }
}
