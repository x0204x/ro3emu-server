using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Server.Maps;

/// <summary>
/// What a monster kind is: NPCConfig + AttriMonsterConfig × _kAttriScale + its normal-attack
/// skill + AIConfig, resolved once per NPCConfig id.
/// </summary>
public sealed record MonsterTemplate(
    int NpcId, int Level, long MaxHp, long MaxSp, long Atk, long Matk, long Def, long Mdef, long Hit, long Flee, long Cri,
    int SpeedCm, int Element, int Race, int Size, int Camp,
    bool Aggressive, float RoamDistance, int RoamTimeMs, float ViewRange, float LeashDistance,
    int NormalSkill, int SkillMs, float HitPointSeconds, int SwingIntervalMs, float AttackRange, int BodyRemainMs)
{
    /// <summary>The boss's special skills (BossSkillSets); null: only the normal attack.</summary>
    public BossSkillBook? Skills { get; init; }
    /// <summary>AttriMonsterConfig cast speed (116), retained for live chant modifiers.</summary>
    public long CastSpeed { get; init; }
}

/// <summary>A live monster on a map (entityType 1): HP, home point, current walk segment and AI state.</summary>
public sealed class MonsterUnit : WorldUnit
{
    public required MonsterTemplate Template { get; init; }
    /// <summary>Instance-specific final outgoing damage rate; 10000 leaves field and dungeon monsters unchanged.</summary>
    public int DamageRatio { get; init; } = 10000;
    public required SpawnGroup Group { get; init; }
    public required SpawnPoint Home { get; init; }
    public long Hp { get; set; }
    public bool Dead { get; set; }
    /// <summary>Scene script presentation camp; null uses the NPCConfig template camp.</summary>
    public int? CampOverride { get; set; }
    public long BornTimeMs { get; init; }
    internal string SpawnGeneration { get; } = Guid.NewGuid().ToString("N");
    public RewardSourceId? RewardSource { get; internal set; }
    internal RewardSettlement? RewardSettlement { get; set; }
    internal Task? RewardCommit { get; set; }
    internal long RewardRecipient { get; set; }
    internal bool DeathDurable { get; set; }
    internal long StagedLethalDamage { get; set; }
    internal bool KillObserversStarted { get; set; }

    /// <summary>Temporary strongest movement slow (basis points), with an absolute server-millisecond expiry.</summary>
    public int SlowMovementBp { get; set; }
    public long SlowMovementExpireMs { get; set; }
    public int? BuffSpeedCm { get; set; }
    public int SpeedCm(long nowMs) => Math.Min(BuffSpeedCm ?? Template.SpeedCm, SlowMovementExpireMs > nowMs
        ? (int)Math.Max(1, Template.SpeedCm * (10000L - SlowMovementBp) / 10000L) : int.MaxValue);

    // Walk segment in progress: from (FromX, FromZ) to (ToX, ToZ) over [StartMs, EndMs].
    public bool Moving { get; set; }
    public float FromX, FromY, FromZ, ToX, ToY, ToZ;
    public long StartMs, EndMs;
    public float SegmentSpeed;

    // AI
    public long NextRoamMs { get; set; }
    public GameSession? Target { get; set; }
    /// <summary>Tower pet currently drawing this monster's attacks; owner remains the fallback target (inferred).</summary>
    public TowerPetUnit? PetTarget { get; set; }
    public long NextSwingMs { get; set; }
    public long NextChaseMs { get; set; }
    public bool Returning { get; set; }
    public long LastCombatMs { get; set; }
    /// <summary>Boss special-skill scheduler state, created when the boss first engages (MonsterService.BossSkills).</summary>
    public BossSkillRuntime? BossSkills { get; set; }
    /// <summary>A special skill's cast and action run until then: the boss neither walks nor swings.</summary>
    public long BusyUntilMs { get; set; }
    /// <summary>No other special before then (the gap after the previous one); shared with MvpService's own plans.</summary>
    public long NextSpecialMs { get; set; }
    /// <summary>MvpService paces this boss's specials from its own tick (its plans first, then the generic ones).</summary>
    public bool SpecialsOwnedByMvp { get; set; }
    /// <summary>Live boss skill cooldowns for 31125/31127, keyed by actual cast row.</summary>
    public Dictionary<int, long> BossSkillCds { get; } = new();

    /// <summary>Status bits (property 2701); bit 15 = fighting.</summary>
    public long StatusBits { get; set; }

    public MonsterBrain? Brain { get; init; }

    /// <summary>Unit custom data a gameplay attaches (31018 on change; carried in the AOI entity), e.g. Guild League IsFlag.</summary>
    public List<CustomData> CustomData { get; } = new();

    /// <summary>Extra snapshot state an owner adds to the 31037 base (MVP bosses and minions: their live buffs).</summary>
    public Action<EntityBase>? EntityExtras { get; set; }

    public override void Tick(MapInstance map, GameTime time) => Brain?.Tick(this, map);

    /// <summary>Position along the current segment at <paramref name="nowMs"/> (updates X/Y/Z).</summary>
    public void Advance(long nowMs)
    {
        if (!Moving) return;
        float t = EndMs <= StartMs ? 1f : Math.Clamp((nowMs - StartMs) / (float)(EndMs - StartMs), 0f, 1f);
        X = FromX + (ToX - FromX) * t;
        Y = FromY + (ToY - FromY) * t;
        Z = FromZ + (ToZ - FromZ) * t;
    }

    /// <summary>The 31081 Walk of the current segment from where the monster is now (for a viewer that just got it).</summary>
    public MovementInfo CurrentMovement(long nowMs)
    {
        var info = new MovementInfo { Pos = Pos, Face = Face, ServerTime = nowMs };
        if (Moving && EndMs > nowMs)
        {
            info.MoveType = MovementType.MoveTypeWalk;
            info.MoveDuration = (int)(EndMs - nowMs);
            info.MovementWalk = new MovementWalk { Dir = Face, Speed = SegmentSpeed, Acc = 0, EndHeight = ToY };
        }
        return info;
    }

    /// <summary>empty name (the client shows NPCConfig._kName), camp and element, HP for the bar.</summary>
    public override EntityBase Entity()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var entity = new EntityBase
        {
            Uuid = (ulong)Uid,
            EntityTemplateID = (ulong)Template.NpcId,
            EntityType = 1, // UT_Monster
            Name = "",
            EntityCamp = CampOverride ?? Template.Camp,
            ElementType = Template.Element,
            Face = Face,
            State = Dead ? EntityState.Dead : EntityState.Alive,
            BornTimeMs = BornTimeMs,
            MovementInfo = CurrentMovement(now),
            NotifyType = EntityBase.Types.NotifyType.All,
        };
        foreach (var (idx, value) in new (long, long)[]
                 {
                     (101, Template.MaxHp), (2000, Dead ? 0 : Hp), (2002, Template.Level), (113, SpeedCm(now)),
                     (102, Template.MaxSp), (2001, Template.MaxSp),
                 })
            entity.Property.Add(new AoIProperty { Idx = idx, Data = value, BaseData = value });
        if (CustomData.Count > 0) entity.CustomDatas.Add(CustomData.Select(c => c.Clone()));
        EntityExtras?.Invoke(entity);
        return entity;
    }

    /// <summary>31049 Base change: a full overwrite of state, template, camp and element.</summary>
    public EntityBase StateChange() => new()
    {
        Uuid = (ulong)Uid,
        NotifyType = EntityBase.Types.NotifyType.Base,
        State = Dead ? EntityState.Dead : EntityState.Alive,
        EntityTemplateID = (ulong)Template.NpcId,
        EntityCamp = CampOverride ?? Template.Camp,
        ElementType = Template.Element,
    };
}

/// <summary>Runs a monster's AI each map tick (MonsterService).</summary>
public interface MonsterBrain
{
    void Tick(MonsterUnit monster, MapInstance map);
}
