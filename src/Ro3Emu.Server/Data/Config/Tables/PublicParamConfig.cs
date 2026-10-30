using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>
/// PublicParamConfig 是以參數名稱索引的異質表，不是產生器的一般設定列；在此集中具名參數的型別讀取。
/// </summary>
public static class PublicParamConfig
{
    private const string TableName = "PublicParamConfig";
    private const int PartySlots = 5;

    // G PublicParam 的原名保留大小寫；缺資料時僅採原件已證實的預設值。
    public static int BarroomTaskId(GameData data) => Scalar(data, "BarroomTaskID", 2000);
    public static int BarRoomNameLen(GameData data) => Scalar(data, "BarRoomNameLen", 18);
    public static int BarroomPersonNum(GameData data) => Scalar(data, "BarroomPersonNum", 1000);
    public static int BarRoomSetInfoCd(GameData data) => Scalar(data, "BarRoomSetInfoCd", 5);
    public static int TransferRoomCd(GameData data) => Scalar(data, "BarRoomTransferRoomCd", 30);
    private static int Scalar(GameData data, string key, int fallback) =>
        data.Table(TableName).TryGetValue(key, out var value) && value.TryGetInt32(out int n) && n > 0 ? n : fallback;

    /// <summary>LC_TaskHelper.GetQuestBranchId 使用的主線分支 ID；保留上游的順序、數值轉換與非正值排除規則。</summary>
    public static long[] GuideMainQuestBranchChoice(GameData data) =>
        data.Table(TableName).TryGetValue(nameof(GuideMainQuestBranchChoice), out var value)
        && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Select(v => (long)(ConfigValue.Number(v) ?? 0)).Where(id => id > 0)] : [];

    /// <summary>G PublicParamConfig.lua 的五人組隊 EXP 級距；倍率用途仍由 TeamService 的推定政策決定。</summary>
    public static int[] TeamExpBuffNum(GameData data)
    {
        // 不完整的手工資料可省略參數；呼叫端使用中性倍率為 (inferred) fallback，不補造客戶端級距。
        if (!data.Table(TableName).TryGetValue(nameof(TeamExpBuffNum), out var value)) return [];
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != PartySlots)
            throw InvalidTeamExperience();
        var result = new int[PartySlots];
        for (int i = 0; i < result.Length; i++)
        {
            if (value[i].ValueKind != JsonValueKind.Number
                || !value[i].TryGetInt32(out int multiplier) || multiplier < 0) throw InvalidTeamExperience();
            result[i] = multiplier;
        }
        return result;
    }

    private static InvalidDataException InvalidTeamExperience() =>
        new("PublicParamConfig.TeamExpBuffNum 必須是五個非負的 int32 整數。");
}
