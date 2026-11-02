using Romsg;

namespace Ro3Emu.Server.Maps;

/// <summary>A live RefreshInfo-backed subtype-12 collection actor.</summary>
public sealed class GatheringUnit : WorldUnit
{
    public required GatheringSpawnPoint Source { get; init; }
    /// <summary>Fresh at each spawn, including refresh and process restart (inferred lifecycle).</summary>
    public required string GenerationId { get; init; }
    public required int NpcId { get; init; }
    public required long CollectionId { get; init; }
    public required int InteractionId { get; init; }
    public required string Name { get; init; }
    public required int Camp { get; init; }
    public required float InteractDistance { get; init; }
    public required int SharedLimit { get; init; }
    public required int PersonalLimit { get; init; }
    public required int CooldownSeconds { get; init; }
    public required long BornTimeMs { get; init; }

    public int TotalCollectCount { get; set; }
    public Dictionary<long, int> PersonalCounts { get; } = [];
    public Dictionary<long, long> Cooldowns { get; } = [];
    public bool Depleted => SharedLimit > 0 && TotalCollectCount >= SharedLimit;

    /// <summary>
    /// The client directly indexes both CollectionInfo maps even when a player's key is absent
    /// (LC_Unit_Collection.lua:509-523). Key zero keeps both protobuf maps present on Lua decode;
    /// real actor IDs remain authoritative keys. The sentinel is an inferred wire compatibility measure.
    /// </summary>
    public CollectionInfo Collection()
    {
        var info = new CollectionInfo { PointConfigId = Source.Id, TotalCollectCount = TotalCollectCount };
        info.CollectCd[0] = 0;
        info.CollectCountByPlayer[0] = 0;
        foreach (var (player, end) in Cooldowns) info.CollectCd[player] = end;
        foreach (var (player, count) in PersonalCounts) info.CollectCountByPlayer[player] = count;
        return info;
    }

    public override EntityBase Entity() => new()
    {
        Uuid = (ulong)Uid,
        EntityTemplateID = (ulong)NpcId,
        EntityType = 2, // UT_Npc; NPCConfig type 2/subtype 12 owns LC_Unit_Collection.
        State = EntityState.Alive,
        Name = Name,
        EntityCamp = Camp,
        Face = Face,
        BornTimeMs = BornTimeMs,
        MovementInfo = new MovementInfo { Pos = Pos, Face = Face, ServerTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
        CollectionInfo = Collection(),
        NotifyType = EntityBase.Types.NotifyType.All,
    };

    public EntityBase CollectionChange() => new()
    {
        Uuid = (ulong)Uid,
        CollectionInfo = Collection(),
        NotifyType = EntityBase.Types.NotifyType.CollectionInfo,
    };
}
