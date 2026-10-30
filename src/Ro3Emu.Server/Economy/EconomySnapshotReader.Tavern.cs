using System.Text.Json;
using Ro3Emu.Server.Game;
namespace Ro3Emu.Server.Economy;

public static partial class EconomySnapshotReader
{
    private sealed partial class State
    {
        internal void ReadTavern() => ScanJsonEvidence("tavern", "id", "state", (key, state) =>
        {
            // 全欄位均為房間／成員／租約控制資料，不含貨幣、道具、待領獎勵。
            try { _ = state.Deserialize<TavernService.WorldState>(StrictJson) ?? throw new JsonException("酒館狀態不可為 null。"); }
            catch (JsonException e) when (OnlyUnmappedMembers(state, typeof(TavernService.WorldState)))
            { throw new UnknownPersistedFieldException(e.Message); }
        });
    }
}
