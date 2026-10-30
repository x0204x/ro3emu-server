using System.Text.Json;
using Microsoft.Data.Sqlite;
using Romsg;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class CharacterService
{
    /// <summary>使用結果與回覆後的畫面同步；不寫入持久資料或交易收據。</summary>
    internal sealed record ItemUseOutcome(int Result, Func<Task>? AfterReply = null);

    /// <summary>
    /// Activity 106's map action 304 must be settled by NewActivityService before the item is removed.
    /// The common item-use path holds ActivityGate then StateLock, validates the live item and owns
    /// replies/cooldown publication. The callback commits 5064, the cost and the supplied cooldown write.
    /// ItemConfig 1112001/1112002 use variants [304,1]/[304,2] (client export).
    /// </summary>
    internal Func<GameSession, ItemUseReq, Action<RoleItemPlanBuilder>, Action, Task<ItemUseOutcome>>? StartTreasureMap { get; set; }

    /// <summary>既有尋寶只恢復同一游標；成功 ACK 後再同步，不進入扣圖、冷卻或 ItemUsed 路徑。</summary>
    internal Func<GameSession, ItemUseReq, Func<Task>?>? ResumeTreasureMap { get; set; }

    /// <summary>Preserve ActivityGate -> StateLock ordering while all effects use the common item-use validation.</summary>
    private async Task<IDisposable?> LockTreasureUseAsync(GameSession session, ItemUseReq req)
    {
        var item = session.Items.FirstOrDefault(i => i.Guid == req.ItemGuid && i.Grid == 0);
        if (item == null || data.Row("ItemConfig", item.Entry) is not { } row || !IsTreasureMapUse(row)) return null;
        await session.ActivityGate.WaitAsync();
        return new Releaser(session.ActivityGate);
    }

    internal static bool IsTreasureMapUse(JsonElement row)
    {
        if (!row.TryGetProperty("_iUseEffect", out var effects) || effects.ValueKind != JsonValueKind.Array
            || effects.GetArrayLength() != 1 || effects[0].ValueKind != JsonValueKind.Array
            || effects[0].GetArrayLength() != 2) return false;
        var action = effects[0];
        return GameData.Number(action[0]) == 304 && GameData.Number(action[1]) is 1 or 2;
    }
}
