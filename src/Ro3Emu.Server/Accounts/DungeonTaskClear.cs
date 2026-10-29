using System.Text.Json;

namespace Ro3Emu.Server.Accounts;

// 此資料獨立於既有副本獎勵 JSON，避免改變已凍結獎勵的指紋。
internal sealed record DungeonTaskClear(string SourceKey, long RoleId, long RoomId, long SceneEntry,
    IReadOnlyList<DungeonTaskClearTarget> Targets, int Version = 1)
{
    internal byte[] Encode()
    {
        Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this);
        if (bytes.Length > 262144) throw new InvalidDataException("副本任務快照過大。");
        return bytes;
    }

    internal static DungeonTaskClear Decode(byte[] bytes)
    {
        var value = JsonSerializer.Deserialize<DungeonTaskClear>(bytes)
            ?? throw new InvalidDataException("副本任務快照為空。");
        value.Validate();
        return value;
    }

    private void Validate()
    {
        if (Version != 1 || string.IsNullOrWhiteSpace(SourceKey) || RoleId <= 0 || RoomId <= 0 || SceneEntry <= 0
            || Targets == null || Targets.Count > 1024
            || Targets.Select(t => (t.TaskId, t.Goal)).Distinct().Count() != Targets.Count
            || Targets.Any(t => t.TaskId <= 0 || t.StartTime < 0 || t.CompleteCount < 0 || t.Goal < 0
                || t.Required <= 0 || t.Target is not [101018, var scene, var required]
                || scene != SceneEntry || required != t.Required))
            throw new InvalidDataException("副本任務快照版本或目標不符。");
    }
}

internal sealed record DungeonTaskClearTarget(long TaskId, long StartTime, long CompleteCount,
    int Goal, long[] Target, long Required);
