using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>A player's running auto-attack: the server repeats the normal attack.</summary>
public sealed class AttackLoop
{
    public required MonsterUnit Target { get; init; }
    public required int SkillId { get; init; }
    public long NextSwingMs { get; set; }
    public float StartX { get; set; }
    public float StartZ { get; set; }
    /// <summary>Started while the player still walked: the stop that follows at once is where the loop really starts.</summary>
    public bool StartedWalking { get; set; }
    public long StartMs { get; init; }
    /// <summary>The walk speed (m/s) and the last walk message when the loop started (CombatService.PlayerMoved).</summary>
    public float ArrivalSpeed { get; init; }
    public DateTime WalkFromUtc { get; init; }
}

/// <summary>
/// Combat: lock (31029), casts (31102) with the server-driven auto-attack loop, swings (31100) and
/// hits at the template's hit moment (31101), HP through 31048 / 31008, the fight stance bit, monster attacks on players,
/// player death (31049 state) and revive (31013), regeneration. The damage formula is reconstructed from the operand
/// names and constants the client carries (§3.6): everything there is marked inferred.
/// </summary>
public sealed partial class CombatService : IGameTicker
{
    public const uint LockEnemy = 31029;
    public const uint UseSkill = 31102;
    public const uint UseInSkill = 31100;
    public const uint SkillHit = 31101;
    public const uint SkillEnd = 31103;
    public const uint SkillInterrupt = 31124;
    public const uint AoiAttrChange = 31048;
    public const uint EntityBaseChange = 31049;
    public const uint Relive = 31013;
    public const uint ReviveInfo = 30112;

    private const int AttrStatus = 2701;
    private const long FightBit = 1L << 15;           // eObjectStateBattling (16)
    private const long OutOfCombatMs = 10000;          // PublicParamConfig.OutOfCombatTime
    private const long RegenIntervalMs = 5000;         // PublicParamConfig.AutoRecoverInterval
    public const uint SkillCdUpdate = 31113;

    private readonly World _world;
    private readonly MonsterService _monsters;
    private readonly CharacterService _character;
    private readonly SkillService _skills;
    private readonly SceneService _scene;
    private readonly BuffService _buffs;
    private readonly WorldData _worldData;
    private readonly SessionRegistry _sessions;
    private readonly GameData _data;
    private readonly GameLoop _loop;
    private readonly ILogger<CombatService> _log;
    /// <summary>The combat dice (hit, damage float, crit, effect procs). Tests whose subject is not chance swap in
    /// fixed rolls, so a prepared lethal basic attack cannot randomly miss (the "first swing missed" flaky class).</summary>
    internal CombatRolls Rolls { get; set; } = new();
    private long _sessionIds;
    private long _regenSlot = -1;
    /// <summary>Monsters whose fight bit is set: the out-of-combat check walks these instead of every unit of every
    /// map each tick. Entries leave on the clear or when the unit leaves the world.</summary>
    private readonly HashSet<MonsterUnit> _fightingMonsters = new();

    /// <summary>A monster died by this player: rewards (RewardService), quest kill targets (TaskService).</summary>
    public List<Func<GameSession, MonsterUnit, Task>> Killed { get; } = new();
    /// <summary>E04: freeze and commit the lethal source/entitlement before publishing a lethal hit or corpse.</summary>
    public List<Func<GameSession, MonsterUnit, Task>> BeforeKilled { get; } = new();
    private sealed class InstanceDeathGate
    {
        internal readonly SemaphoreSlim Semaphore = new(1, 1);
        internal int Users;
    }
    private readonly Dictionary<long, InstanceDeathGate> _instanceDeathGates = new();
    private sealed class LethalFinalization { internal int Started; }
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<MonsterUnit, LethalFinalization>
        _finalizedLethalPublications = new();

    private async Task FinalizeLethalPublicationAsync(MonsterUnit monster)
    {
        if (!monster.Dead) return;
        var state = _finalizedLethalPublications.GetValue(monster, _ => new LethalFinalization());
        if (Interlocked.Exchange(ref state.Started, 1) != 0) return;
        foreach (var handler in AfterLethalPublication)
            try { await handler(monster); }
            catch (Exception error) { _log.LogError(error, "Postcommit death finalizer failed for {Uid}", monster.Uid); }
    }

    private async Task CommitMonsterDeathAsync(GameSession player, MonsterUnit monster, long previousHp, long damage,
        Role? recipient, Ownership.AttachmentLease? attachment, long sceneGeneration, long worldScene)
    {
        bool OriginalRecipient() => recipient != null && ReferenceEquals(player.Role, recipient)
            && ReferenceEquals(player.Attachment, attachment) && attachment?.Cancelled != true
            && !player.SceneLoading && player.SceneGeneration == sceneGeneration
            && player.WorldScene == worldScene && player.WorldScene == monster.WorldScene
            && _sessions.All().Contains(player);
        InstanceDeathGate? gate = null;
        if (monster.InstanceId != 0)
        {
            if (!_instanceDeathGates.TryGetValue(monster.InstanceId, out gate))
                _instanceDeathGates[monster.InstanceId] = gate = new();
            gate.Users++;
            await gate.Semaphore.WaitAsync();
        }
        monster.StagedLethalDamage = damage; // final damage is part of frozen dungeon history before Dealt runs
        try
        {
            if (!OriginalRecipient()) throw new OperationCanceledException("Kill recipient changed before reward admission.");
            // Reward presentation is bound to its recipient lease. Keep that binding on an awaited
            // child permit so cancellation cannot suppress an already committed cast's hit/corpse.
            await _loop.TrackAsync("durable kill reward", async () =>
            {
                foreach (var handler in BeforeKilled)
                {
                    if (!OriginalRecipient()) throw new OperationCanceledException("Kill recipient changed during reward admission.");
                    await handler(player, monster);
                }
            });
            monster.DeathDurable = true;
            // Keep a room's concurrent lethal admissions ordered through the damage-stat update.
            // The next final-actor prehook sees all earlier durable actors and their report counters.
            if (OriginalRecipient())
                foreach (var handler in Dealt) handler(player, monster, damage);
        }
        catch
        {
            if (monster.RewardSettlement?.Committed == true || monster.DeathDurable)
            {
                monster.DeathDurable = true;
                monster.Hp = 0; // a retained retry may have entered at the restored 1 HP
                await _monsters.KillAsync(monster);
            }
            else if (monster.RewardSettlement?.OutcomeUnknown != true)
            {
                monster.Hp = previousHp;
                monster.RewardCommit = null;
                monster.RewardSettlement = null;
                monster.RewardRecipient = 0;
            }
            throw;
        }
        finally
        {
            if (gate != null)
            {
                gate.Semaphore.Release();
                if (--gate.Users == 0) { _instanceDeathGates.Remove(monster.InstanceId); gate.Semaphore.Dispose(); }
            }
        }
    }

    private async Task SettleMonsterHitAsync(GameSession player, MonsterUnit monster, long previousHp, long damage)
    {
        var role = player.Role;
        var attachment = player.Attachment;
        long sceneGeneration = player.SceneGeneration, worldScene = player.WorldScene;
        bool OriginalRecipient() => role != null && ReferenceEquals(player.Role, role)
            && ReferenceEquals(player.Attachment, attachment) && attachment?.Cancelled != true
            && !player.SceneLoading && player.SceneGeneration == sceneGeneration
            && player.WorldScene == worldScene && player.WorldScene == monster.WorldScene
            && _sessions.All().Contains(player);
        try
        {
            if (monster.Hp == 0) monster.StagedLethalDamage = damage;
            // MVP's final-hit score and event damage precede the event's frozen result.
            // A training dummy can restore its HP here, so recheck lethality afterward.
            foreach (var handler in EventDamageRecorded) await handler(player, monster, damage);
            if (!OriginalRecipient())
                throw new OperationCanceledException("Monster hit lost its original recipient during damage bookkeeping.");
            if (monster.Hp > 0)
            {
                foreach (var handler in Dealt) handler(player, monster, damage);
                return;
            }
            foreach (var handler in BeforeLethalPublication) await handler(player, monster, previousHp);
            // Event settlement may await a writer. It cannot transfer the separate E04
            // source to a later role or attachment that did not land this hit.
            if (!OriginalRecipient())
                throw new OperationCanceledException("Kill recipient changed before the ordinary reward barrier.");
            await CommitMonsterDeathAsync(player, monster, previousHp, damage,
                role, attachment, sceneGeneration, worldScene);
        }
        catch (EventSettlementOutcomeUnknownException) { throw; } // retain the frozen event source and unpublished actor
        catch
        {
            // A proven E04 refusal restores HP inside CommitMonsterDeathAsync. An already
            // committed event source remains frozen and is retried without another event hit.
            if (monster.Hp == 0 && !monster.DeathDurable && monster.RewardSettlement?.OutcomeUnknown != true)
                RestoreUnpublishedLethalHit(monster, previousHp);
            throw;
        }
    }

    private async Task PublishDurableKillAsync(GameSession player, MonsterUnit monster, Func<bool> current)
    {
        // Cancellation stops further effects; the original healthy owner still observes paid kills.
        bool CanPublish() => current() && player.Owner?.State != Ownership.RoleOwnerState.RecoveryRequired;
        if (!monster.DeathDurable || monster.KillObserversStarted || !CanPublish()) return;
        monster.KillObserversStarted = true; // reserve the one callback pass across awaited observers
        foreach (var handler in Killed)
        {
            if (!CanPublish()) break;
            try { await handler(player, monster); }
            catch (Exception e) { _log.LogError(e, "Postcommit kill observer failed for {Source}", monster.RewardSource?.Key); }
        }
    }

    /// <summary>Dungeon owners settle a loss after authoritative player death (inferred).</summary>
    public List<Func<GameSession, Task>> PlayerDied { get; } = new();
    /// <summary>A death some owner publishes its own 30112 for (a dungeon run's revive rule, a territory battle's
    /// countdown): the generic 30-second notice is not sent then. Two notices with different times reached the
    /// client before (seen in the traffic of a dungeon death).</summary>
    public List<Func<GameSession, bool>> ReviveInfoOwners { get; } = new();

    /// <summary>A tower-owned type-7 actor died; TowerService sends 32809 and replaces it from the saved party.</summary>
    public List<Func<TowerPetUnit, Task>> TowerPetDied { get; } = new();

    /// <summary>A player damaged a monster (MVP statistics).</summary>
    public List<Action<GameSession, MonsterUnit, long>> Dealt { get; } = new();

    /// <summary>Territory objective/cart owner observes committed monster HP before kill settlement.</summary>
    public List<Func<GameSession, MonsterUnit, long, Task>> MonsterDamageApplied { get; } = new();

    /// <summary>Territory and escort owners observe effective hostile-player damage and its lethal transition.</summary>
    public List<Action<GameSession, GameSession, long, bool>> PvpDealt { get; } = new();
    public List<Func<GameSession, GameSession, Task>> PlayerKilled { get; } = new();
    public Func<GameSession, GameSession, bool>? PlayerHostile { get; set; }
    public Func<GameSession, MonsterUnit, bool>? MonsterTargetAllowed { get; set; }
    public Func<GameSession, MonsterUnit, Vec3, Vec3, bool>? BlockedMonsterEndpointAllowed { get; set; }
    /// <summary>
    /// Whether something stands between the caster and the target of a skill that walls stop (_iPassBlock 2): the
    /// monster aimed at (null for a ground or player target), the caster's and the target's positions. Unset, nothing
    /// does (see <see cref="ValidateTarget"/>).
    /// </summary>
    public Func<GameSession, MonsterUnit?, Vec3, Vec3, bool>? SkillLineBlocked { get; set; }

    /// <summary>A monster damaged a player; the flag says the hit was lethal (MVP statistics).</summary>
    public List<Action<MonsterUnit, GameSession, long, bool>> Taken { get; } = new();

    /// <summary>Effective self-skill healing for authoritative dungeon statistics (MG_Common.RoleStatisticsInfo).</summary>
    public List<Action<GameSession, long>> Healed { get; } = new();

    // Totem passives need the row and final target before the existing post-hit event loses that context.
    public List<Func<GameSession, JsonElement, Task>> CastLanded { get; } = new();
    /// <summary>After a player's own cast applied its effects, with the monsters it processed and its published hit (each
    /// target's result, the allies it healed) (TalentProcs).</summary>
    internal List<Func<GameSession, ActiveSkillCast, IReadOnlyList<MonsterUnit>, SkillHitInfo, Task>> CastResolved { get; } = [];
    /// <summary>The row a cast deals its damage with once its monster targets are chosen ("For every enemy hit by [Hammer
    /// Fall], ... increases"), by their count (TalentProcs).</summary>
    internal Func<GameSession, JsonElement, int, JsonElement>? TargetCountRow { get; set; }
    /// <summary>May the caster's skill put this _kBuff1 buff on the recipient (TalentProcs: talent-gated buffs).</summary>
    internal Func<GameSession, GameSession, int, bool>? SkillBuffAllowed { get; set; }
    public List<Func<GameSession, MonsterUnit, JsonElement, DamageHitResult, long, long>> DamageAdjusters { get; } = new();
    public List<Func<GameSession, MonsterUnit, JsonElement, long, long>> ExtraHitDamage { get; } = new();
    public List<Func<GameSession, MonsterUnit, JsonElement, long, Task>> DealtAsync { get; } = new();
    public List<Func<GameSession, long, long>> ExtraHeal { get; } = new();
    public List<Func<MonsterUnit, GameSession, DamageHitResult, long, Task<long>>> IncomingDamageAdjusters { get; } = new();
    public List<Func<MonsterUnit, GameSession, long, Task>> TakenAsync { get; } = new();
    public List<Func<GameSession, Task>> PlayerRevived { get; } = new();
    /// <summary>A match owner decides which skills a participant may cast (true/false), or defers (null).</summary>
    public Func<GameSession, int, bool?>? MatchSkillRule { get; set; }
    /// <summary>Feature-scoped skill grants that retain the ordinary target, cooldown, cost and effect pipeline.</summary>
    internal List<Func<GameSession, CastSkillReq, bool>> SkillGrants { get; } = [];
    /// <summary>Feature-scoped exceptions to a generic target gate; all unlisted target errors remain closed.</summary>
    internal List<Func<GameSession, CastSkillReq, int, bool>> SkillTargetErrorOverrides { get; } = [];
    /// <summary>
    /// A skill a feature owns outright (the war commander's): null for any other skill, else 0 to cast it or the refusal.
    /// Such a cast skips the job-tree check and the SkillConfig cooldowns; <see cref="OwnSkillCast"/> runs in their place
    /// once the cast is accepted, and <see cref="OwnSkillLanded"/> resolves it instead of the generic effects.
    /// </summary>
    public Func<GameSession, int, int?>? OwnSkillAdmit { get; set; }
    public Func<GameSession, int, Task>? OwnSkillCast { get; set; }
    public Func<GameSession, JsonElement, Task>? OwnSkillLanded { get; set; }

    /// <summary>
    /// Runs once a player's own cast is accepted, before its timeline starts, and returns the milliseconds that cast must
    /// wait. MountService gets a system-mount rider off first (the client holds a skill until the unit is off one,
    /// LC_State_Unit_Skill.InitSkillState), so the dismount plays and the hit follows. The cast is never refused.
    /// </summary>
    public Func<GameSession, Task<int>>? BeforeCast { get; set; }

    /// <summary>
    /// Skills a player may cast without learning them, resolved like any other (a war vehicle seat's skills,
    /// VehicleSeatConfig _kMainSkills / _kSkills); their cooldowns are the ordinary SkillConfig ones.
    /// </summary>
    public List<Func<GameSession, int, bool>> GrantedSkill { get; } = [];
    /// <summary>World-core owns the dead player's explicit accept/decline invitation.</summary>
    public Func<GameSession, GameSession, JsonElement, Task>? ReviveOffer { get; set; }
    public Func<GameSession, int>? ReviveCheck { get; set; }
    /// <summary>World-core applies a paid World Tree skill through the same claim and dungeon quota owner.</summary>
    public Func<GameSession, GameSession, JsonElement, Task<bool>>? ReviveItemSkill { get; set; }

    public CombatService(World world, MonsterService monsters, CharacterService character, SkillService skills, SceneService scene, BuffService buffs,
        WorldData worldData, SessionRegistry sessions, GameData data, GameLoop loop, ILogger<CombatService> log, PetService pets,
        SeasonTalentService talents)
    {
        this._world = world;
        this._monsters = monsters;
        this._character = character;
        this._skills = skills;
        this._scene = scene;
        this._buffs = buffs;
        this._worldData = worldData;
        this._sessions = sessions;
        this._data = data;
        this._loop = loop;
        this._log = log;
        _pets = pets;
        WireGuardians();
        monsters.Attack = MonsterAttackAsync;
        buffs.ImmuneDeathDeclined = PlayerDiesAsync;
        scene.Leaving.Add(ClearSkillsAsync);
        scene.Leaving.Add(buffs.ClearOnSceneAsync);
        character.ExtraAttributes.Add(PassiveAttributes);
        character.ExtraAttributes.Add(ResonanceAttributes); // Job Resonance stats (CombatService.Restraint)
        world.UnitRemoved.Add(unit => { if (unit is MonsterUnit m) _fightingMonsters.Remove(m); return Task.CompletedTask; });
        skills.Learned.Add(RefreshSkillParametersAsync);
        skills.Learned.Add(character.RefreshStatsAsync);
        buffs.Changed.Add(RefreshSkillParametersAsync);
        talents.EffectsChanged.Add(RefreshSkillParametersAsync);
        scene.Loaded.Add(RefreshSkillParametersAsync);
        scene.Leaving.Add(ClearSceneSkillProtocolAsync);
        skills.QueueEligibility = CanQueueSkill;
        character.ProgressHandlers.Add(buffs.ReconcileEnergyAsync);
        monsters.AttackPet = MonsterAttackPetAsync;
        buffs.UnitChanged.Add(PublishTowerPetStatsAsync);
        WireBuffRules();
        WireBossSkills();
        WireCompanions();
    }

    private static long Now => MonsterService.NowMs;

    // ---- requests ----

    public void Lock(GameSession s, LockEnemyReq req)
    {
        s.LockTarget = req.EnemyId;
        _log.LogDebug("{Name} locks {Target}", s.Role?.Name, req.EnemyId);
    }

    /// <summary>
    /// The first authored Action damage entry, which LC_DamageHelper can turn into a target hit animation. A named
    /// Bullet entry is only the fallback for timelines without an Action hit; it must not hide a later Action entry.
    /// </summary>
    private int HitEffect(long scriptShow)
    {
        var effects = _data.SkillTemplateTable("Lua_Config_EffectData");
        int firstDamage = 0;
        int firstBullet = 0;
        for (long id = scriptShow * 100; id < scriptShow * 100 + 100; id++)
        {
            if (!effects.TryGetValue(id.ToString(), out var e)
                || new SkillEffectData(e).Damage is not { } damage) continue;
            firstDamage = firstDamage == 0 ? (int)id : firstDamage;
            if (damage is { DamageType: 3, HasActionDamage: true }) return (int)id;
            if (firstBullet == 0 && damage is { DamageType: 2, Bullet: { } bullet }
                && !string.IsNullOrWhiteSpace(bullet.Name)) firstBullet = (int)id;
        }
        return firstBullet != 0 ? firstBullet : firstDamage != 0 ? firstDamage : 1000002;
    }

    /// <summary>The first visible authored Bullet entry for a timeline that has no client bullet points.</summary>
    private int BulletEffect(long scriptShow)
    {
        var effects = _data.SkillTemplateTable("Lua_Config_EffectData");
        for (long id = scriptShow * 100; id < scriptShow * 100 + 100; id++)
        {
            if (!effects.TryGetValue(id.ToString(), out var e)
                || new SkillEffectData(e).Damage is not { DamageType: 2, Bullet: { } bullet }
                || string.IsNullOrWhiteSpace(bullet.Name)) continue;
            return (int)id;
        }
        return 0;
    }

    /// <summary>
    /// The authored damage-display count is also the client presentation split. LC_DamageHelper divides one
    /// authoritative total damage value across this many jump words, while LC_Bullet_Mgr emits this many bullets.
    /// An empty _kShowDmg means one hit/projectile (for example Fire Ball); Fire Bolt authors one through ten.
    /// </summary>
    private static int PresentationSplit(JsonElement row) => HitSplit(row);

    private int BulletTravelMs(int effectId, Vec3 origin, Vec3 target)
    {
        if (!_data.SkillTemplateTable("Lua_Config_EffectData").TryGetValue(effectId.ToString(), out var effect)
            || new SkillEffectData(effect).Damage?.Bullet is not { } bullet) return 0;
        double duration;
        if (bullet.ControlType == 0)
            duration = bullet.LifeTime;
        else
        {
            double unitsPerSecond = bullet.Speed;
            duration = unitsPerSecond > 0 ? Math.Max(1, Distance(origin, target) / unitsPerSecond * 1000) : 0;
        }
        return duration > 0 ? (int)Math.Clamp(Math.Round(duration), 1, 5000) : 0;
    }
    /// <summary>
    /// SkillHitInfo.split: how many damage words the client shows for one hit. SkillConfig._kShowDmg holds one count
    /// per show attack point and only the server reads it. LC_DamageHelper.ProcessDamageWord divides the damage into
    /// that many words and, for the caster, adds a running total (Dmg_Accumulate) for skills with _iAccumDmgType, so
    /// with split 1 the total repeats the hit. A cast resolves at its first attack point, so the first count applies
    /// (inferred for skills with several points).
    /// </summary>
    private static int HitSplit(JsonElement row) => (int)Math.Max(1, new SkillConfig(row).ShowDmg.FirstOrDefault(1));

    // ---- monsters hitting players ----

    /// <summary>
    /// Tower pet skills use configured damaging PetStarConfig._kActiveSkill entries and the shared
    /// damage formula. Target choice and skill rotation are (inferred); 31100 source is the actor UUID,
    /// while petConfigId and 32809 keys remain the owned PetBaseConfig ID.
    /// </summary>
    public async Task TowerPetAttackAsync(GameSession owner, TowerPetUnit pet, MonsterUnit target, int skillId)
    {
        if (!ReferenceEquals(owner, pet.Owner) || pet.Dead || pet.Hp <= 0 || target.Dead || target.Hp <= 0 || owner.Role == null || owner.Dead || owner.SceneLoading
            || owner.TowerAttempt is not { Loaded: true, Settled: false } attempt
            || attempt.InstanceId != pet.InstanceId || target.InstanceId != pet.InstanceId
            || !attempt.Pets.Contains(pet) || !pet.AttackSkills.Any(s => s.Id == skillId)
            || MonsterService.Distance(pet.X, pet.Z, target.X, target.Z) > pet.AttackSkills.First(s => s.Id == skillId).Range + 1f
            || _data.Row("SkillConfig", skillId) is not { } row
            || owner.WorldScene != pet.WorldScene || target.WorldScene != pet.WorldScene
            || !pet.TryBeginCast(pet.AttackSkills.First(s => s.Id == skillId), Now)) return;
        var castingRole = owner.Role;
        var castingAttachment = owner.Attachment;
        long castingSceneGeneration = owner.SceneGeneration;
        long castId = ++_sessionIds;
        long now = Now;
        int duration = pet.ActionMsFor(pet.AttackSkills.First(s => s.Id == skillId));
        var use = new UseInSkillInfo
        {
            SrcID = pet.Uid, SkillID = skillId, TargetId = target.Uid, TargetPos = target.Pos,
            Face = pet.Face, SessionId = castId, SkillMs = duration, StartTime = now, PetConfigId = pet.PetId,
            PlayAct = true,
        };
        await _world.ToUnitViewersAsync(pet, UseInSkill, use);
        if (!pet.Viewers.Contains(owner)) await owner.SendGameAsync(UseInSkill, use);
        // The exact server pet hit point is unavailable. Schedule at 40% of the configured action (inferred).
        ScheduleRuntime(pet.WorldScene, TimeSpan.FromMilliseconds(duration * 0.4), async () =>
        {
            if (pet.Dead || pet.Hp <= 0 || owner.Dead || owner.SceneLoading || !ReferenceEquals(owner.Role, castingRole)
                || owner.TowerAttempt is not { Loaded: true, Settled: false } a
                || a.InstanceId != pet.InstanceId || !a.Pets.Contains(pet)) return;
            float radius = row.TryGetProperty("_kRange", out var area) && area.ValueKind == JsonValueKind.Array
                && area.GetArrayLength() > 0 ? (float)(GameData.Number(area[0]) ?? 0) / 100f : 0f;
            int limit = (int)Math.Max(1, GameData.Int(row, "_iTargetMax"));
            var targets = GameData.Int(row, "_iRangeType") == 2 && radius > 0
                ? _world.Map(pet.WorldScene).Near(pet.X, pet.Z, radius + 1).OfType<MonsterUnit>()
                    .Where(m => !m.Dead && m.Hp > 0 && m.InstanceId == pet.InstanceId
                        && MonsterService.Distance(pet.X, pet.Z, m.X, m.Z) <= radius + 0.5f)
                    .OrderBy(m => MonsterService.Distance(pet.X, pet.Z, m.X, m.Z)).Take(limit).ToList()
                : !target.Dead && target.Hp > 0 && target.InstanceId == pet.InstanceId
                    && MonsterService.Distance(pet.X, pet.Z, target.X, target.Z) <= pet.AttackSkills.First(s => s.Id == skillId).Range + 2f
                    ? new List<MonsterUnit> { target } : [];
            if (targets.Count == 0) return;
            var stats = pet.CombatStats;
            var attacker = new Fighter(stats[103], stats[104], stats[109], stats[111], pet.Level, pet.Element);
            var hit = new SkillHitInfo
            {
                SrcID = pet.Uid, SkillId = skillId, ShowPoint = HitEffect(GameData.Int(row, "_iScriptShow")),
                SessionId = castId, SrcElement = pet.Element,
            };
            var processed = new List<MonsterUnit>();
            var killed = new List<MonsterUnit>();
            try
            {
                foreach (var monster in targets)
                {
                    if (monster.Dead || monster.Hp <= 0) continue; // another cast may have reserved this actor while we awaited
                    processed.Add(monster);
                    var defender = MonsterFighter(monster);
                    var (damage, result) = Damage(attacker, defender, row);
                    foreach (var adjust in MonsterDamageAdjusters) damage = adjust(monster, damage, false);
                    long previousHp = monster.Hp;
                    monster.Hp = Math.Max(0, monster.Hp - damage);
                    if (monster.Hp == 0) killed.Add(monster); // reserve before awaited callbacks
                    await SettleMonsterHitAsync(owner, monster, previousHp, damage);
                    hit.Dsts.Add(new DamageInfo
                    {
                        DstID = monster.Uid, Damage = damage, HitResult = result,
                        DstElement = monster.Template.Element, IsDead = monster.Hp == 0
                    });
                }
                foreach (var viewer in processed.SelectMany(m => m.Viewers).Append(owner).Distinct())
                    await viewer.SendGameAsync(SkillHit, hit);
                foreach (var monster in processed)
                {
                    if (a.Settled || owner.Dead) return;
                    if (monster.Hp == 0 && !monster.DeathDurable) continue; // another cast's uncommitted death
                    await _world.ToUnitViewersAsync(monster, AoiAttrChange, MonsterService.Hp(monster));
                    if (monster.Hp > 0)
                    {
                        _monsters.Engage(monster, pet);
                        await FightingAsync(monster);
                        continue;
                    }
                    if (!killed.Contains(monster)) continue; // a concurrent player/pet owns that lethal resolution
                    await _monsters.KillAsync(monster);
                    foreach (var other in _sessions.All().Where(o => o.Attack?.Target == monster))
                    {
                        other.Attack = null;
                        await other.SendGameAsync(SkillEnd, new SkillEndNotify
                        {
                            SrcID = other.Role!.Id, SessionId = castId, Type = SkillEndNotify.Types.SkillEndType.Interrupted,
                            InterruptedInfo = new SkillInterruptedInfo { Type = SkillInterruptedInfo.Types.SkillInterruptType.LockerInvalid },
                        });
                    }
                    await PublishDurableKillAsync(owner, monster, () => ReferenceEquals(owner.Role, castingRole)
                        && ReferenceEquals(owner.Attachment, castingAttachment) && !owner.SceneLoading
                        && owner.SceneGeneration == castingSceneGeneration && owner.WorldScene == monster.WorldScene);
                }
            }
            finally
            {
                foreach (var monster in killed.Where(m => m.Hp == 0 && m.DeathDurable))
                {
                    if (!monster.Dead)
                        try { await _monsters.KillAsync(monster); }
                        catch (Exception e) { _log.LogError(e, "Could not publish durable pet kill {Source}", monster.RewardSource?.Key); }
                    await PublishDurableKillAsync(owner, monster, () => ReferenceEquals(owner.Role, castingRole)
                        && ReferenceEquals(owner.Attachment, castingAttachment) && !owner.SceneLoading
                        && owner.SceneGeneration == castingSceneGeneration && owner.WorldScene == monster.WorldScene);
                    if (monster.Dead) await FinalizeLethalPublicationAsync(monster);
                }
            }
        });
    }

    private async Task MonsterAttackPetAsync(MonsterUnit monster, TowerPetUnit pet)
    {
        if (pet.Dead || pet.Hp <= 0 || monster.Dead || monster.Hp <= 0
            || pet.Owner.TowerAttempt is not { Loaded: true, Settled: false } attempt
            || !attempt.Pets.Contains(pet)) return;
        long castId = ++_sessionIds;
        var use = new UseInSkillInfo
        {
            SrcID = monster.Uid, SkillID = monster.Template.NormalSkill, TargetId = pet.Uid, TargetPos = pet.Pos,
            Face = monster.Face, SessionId = castId, SkillMs = monster.Template.SkillMs, StartTime = Now, PlayAct = true,
        };
        await _world.ToUnitViewersAsync(monster, UseInSkill, use);
        ScheduleRuntime(monster.WorldScene, TimeSpan.FromSeconds(monster.Template.HitPointSeconds), async () =>
        {
            if (pet.Dead || pet.Hp <= 0 || monster.Dead || monster.Hp <= 0 || monster.WorldScene != pet.WorldScene
                || pet.Owner.TowerAttempt is not { Loaded: true, Settled: false } current || !current.Pets.Contains(pet)
                || MonsterService.Distance(monster.X, monster.Z, pet.X, pet.Z) > monster.Template.AttackRange + 2f) return;
            await DamageTowerPetAsync(monster, pet, _data.Row("SkillConfig", monster.Template.NormalSkill), monster.Template.NormalSkill, castId, 1000002);
        });
    }

    /// <summary>A monster's hit on a tower pet: the row's damage, pet support resistance, shields, HP and its death.</summary>
    private async Task DamageTowerPetAsync(MonsterUnit monster, TowerPetUnit pet, JsonElement? row, int skillId, long castId,
        int showPoint, int damagePercent = 100)
    {
        var attacker = MonsterFighter(monster);
        var stats = pet.CombatStats;
        var defender = new Fighter(0, 0, 0, 0, pet.Level, pet.Element)
        { Def = stats[105], Mdef = stats[106], Flee = stats[110], ResCri = stats[112] };
        var (damage, result) = row is { } r ? Damage(attacker, defender, r)
            : (Math.Max(1, monster.Template.Atk), DamageHitResult.Normal);
        damage = ScaleMonsterDamage(damage, monster.DamageRatio);
        damage = MonsterBuffDamage(monster, row, damage);
        if (damagePercent != 100) damage = damagePercent <= 0 ? 0 : ScaleMonsterDamage(damage, damagePercent * 100);
        foreach (var adjust in MonsterDamageAdjusters) damage = adjust(monster, damage, true);
        damage = ApplyPetSupportResistance(damage, _buffs.UnitAttributes(pet), row, monster.Template.Element);
        long beforeShield = damage;
        damage = await _buffs.AbsorbAsync(pet, damage);
        pet.Hp = Math.Max(0, pet.Hp - damage);
        // Reserve death before any awaited broadcasts: neither the pet nor another pending swing
        // may act on zero HP while the death notification and substitution are being delivered.
        pet.Dead = pet.Hp == 0;
        var hit = new SkillHitInfo
        {
            SrcID = monster.Uid, SkillId = skillId, ShowPoint = showPoint,
            SessionId = castId, SrcElement = monster.Template.Element,
            Dsts = { new DamageInfo { DstID = pet.Uid, Damage = damage, SheildDamage = beforeShield - damage, HitResult = result,
                DstElement = pet.Element, IsDead = pet.Hp == 0 } },
        };
        await _world.ToUnitViewersAsync(monster, SkillHit, hit);
        await _world.ToUnitViewersAsync(pet, AoiAttrChange, pet.HpChange());
        if (!pet.Dead) return;
        await _buffs.ClearUnitAsync(pet);
        if (ReferenceEquals(monster.PetTarget, pet)) monster.PetTarget = null;
        await _world.ToUnitViewersAsync(pet, EntityBaseChange, pet.StateChange());
        foreach (var handler in TowerPetDied) await handler(pet);
    }

    private async Task MonsterAttackAsync(MonsterUnit m, GameSession p)
    {
        if (m.Dead || m.Hp <= 0 || _buffs.UnitForbidden(m, ForbiddenState.NormalAttack) || Stealthed(p) || !_buffs.CanSeePosition(m, p.Position)) return;
        var targetRole = p.Role;
        if (targetRole == null) return;
        long targetScene = p.WorldScene;
        long targetGeneration = p.SceneGeneration;
        var targetLease = p.Attachment;
        long session = ++_sessionIds;
        var use = new UseInSkillInfo
        {
            SrcID = m.Uid, SkillID = m.Template.NormalSkill, TargetId = p.Role!.Id, TargetPos = p.Position.Clone(), Face = m.Face,
            SessionId = session, ChantMs = 0, SkillMs = m.Template.SkillMs, StartTime = Now, PlayAct = true,
        };
        // Like a player's cast: a row with an _iSkillEffectLevel takes the HIGH_LEVEL route (the Phantom Realm and
        // Valhalla Maya swings 4340001/5920101 have level 1). A normal attack never starts a boss-panel cooldown
        // (31125 is for the boss's skills).
        await SendMonsterTimelineAsync(m, use, highLevel: _data.Row("SkillConfig", m.Template.NormalSkill) is { } swingRow
            && GameData.Int(swingRow, "_iSkillEffectLevel") > 0);
        ScheduleRuntime(m.WorldScene, TimeSpan.FromSeconds(m.Template.HitPointSeconds), async () =>
        {
            if (m.Dead || m.Hp <= 0 || _buffs.UnitForbidden(m, ForbiddenState.NormalAttack) || Stealthed(p) || p.Dead || p.Role == null || p.SceneLoading || p.WorldScene != m.WorldScene) return;
            if (!ReferenceEquals(p.Role, targetRole) || p.WorldScene != targetScene
                || p.SceneGeneration != targetGeneration || !ReferenceEquals(p.Attachment, targetLease)
                || targetLease?.Cancelled == true) return;
            if (MonsterService.Distance(m, p) > m.Template.AttackRange + 2f || !_buffs.CanSeePosition(m, p.Position)) return; // stepped away
            var stats = _character.Stats(p);
            var attacker = MonsterFighter(m);
            var defender = new Fighter(0, 0, 0, 0, p.Progress!.Level, 1)
            { Def = stats[105], Mdef = stats[106], Flee = stats[110], ResCri = stats[112], FixedDamage = (p.GmCombat & GmCombatSwitches.FixedDamage) != 0 };
            var row = _data.Row("SkillConfig", m.Template.NormalSkill);
            var (damage, result) = row is { } r ? Damage(attacker, defender, r) : (Math.Max(1, m.Template.Atk), DamageHitResult.Normal);
            damage = ScaleMonsterDamage(damage, m.DamageRatio);
            damage = MonsterBuffDamage(m, row, damage);
            foreach (var adjust in MonsterDamageAdjusters) damage = adjust(m, damage, true);
            if (damage > 0) foreach (var modifier in IncomingDamageAdjusters) damage = Math.Max(0, await modifier(m, p, result, damage));
            var committed = await CommitPlayerDamageAsync(p, damage, row,
                () => !m.Dead && m.Hp > 0 && ReferenceEquals(p.Role, targetRole)
                    && p.WorldScene == targetScene && m.WorldScene == targetScene
                    && p.SceneGeneration == targetGeneration && ReferenceEquals(p.Attachment, targetLease)
                    && targetLease?.Cancelled != true,
                sourceElement: m.Template.Element);
            if (committed == null) return;
            (damage, long hp, _, long shieldDamage) = committed.Value;
            try
            {
                foreach (var handler in Taken) handler(m, p, damage, hp == 0);
                var hit = new SkillHitInfo
                {
                    SrcID = m.Uid, SkillId = m.Template.NormalSkill, ShowPoint = 1000002, SessionId = session, SrcElement = m.Template.Element,
                    Dsts = { new DamageInfo { DstID = p.Role.Id, Damage = damage, SheildDamage = shieldDamage, HitResult = result, DstElement = 1, IsDead = hp == 0 } },
                };
                await _world.ToUnitViewersAsync(m, SkillHit, hit);
                if (!m.Viewers.Contains(p)) await p.SendGameAsync(SkillHit, hit);
                await _character.SetHpSpAsync(p, null, null); // publish the reserved/current HP; never undo a later hit
                foreach (var handler in TakenAsync) await handler(m, p, damage);
                await FightingAsync(p);
                await FightingAsync(m);
                if (hp == 0) await PlayerDiesAsync(p);
            }
            finally { if (hp == 0) ReleaseDeathPublication(p); }
        });
    }

    /// <summary>PetFloorConfig attribute 215/per_dmg applies after the ordinary damage formula (inferred application).</summary>
    internal static long ScaleMonsterDamage(long damage, int ratio) => damage <= 0 ? damage
        : Math.Max(1, (long)Math.Round((decimal)damage * ratio / 10000m, MidpointRounding.AwayFromZero));

    /// <summary>One protected HP transition for monster and event hits; reserve lethal state before publication.</summary>
    private async Task<(long Damage, long Hp, long Effective, long ShieldDamage)?> CommitPlayerDamageAsync(GameSession target, long damage, JsonElement? row,
        Func<bool>? valid = null, int sourceElement = 1)
    {
        var targetRole = target.Role;
        using var held = await CharacterService.Lock(target);
        if (target.Dead || targetRole == null || !ReferenceEquals(target.Role, targetRole) || target.SceneLoading
            || valid?.Invoke() == false) return null;
        // A missed/fully absorbed hit must not spend a finite Safety Wall charge.
        // GmUnlimit's invincibility (inferred): the hit lands as 0, like a Safety Wall.
        if (damage > 0 && ((target.GmCombat & GmCombatSwitches.Unlimited) != 0 || _buffs.HasForbidden(target, ForbiddenState.Damage)
            || await GroundProtectsAsync(target, row))) damage = 0;
        if (row is { } incomingSkill) damage = GuardianNormalAttackResistance(target, incomingSkill, damage);
        // (inferred) Support damage reductions precede the finite shield and lethal-hit handling.
        damage = ApplyPetSupportResistance(damage, target.Buffs.Where(b => b.ExpireMs > Now
                && (b.SourceId is >= TowerPetUnit.UidBase and < TowerPetUnit.UidLimit || b.IsGuardianOwned))
            .SelectMany(b => b.Changes).Select(c => (c.Attr, c.Value)), row, sourceElement);
        long beforeShield = damage;
        damage = await _buffs.AbsorbAsync(target, damage);
        long shieldDamage = beforeShield - damage;
        if (!ReferenceEquals(target.Role, targetRole) || target.SceneLoading || valid?.Invoke() == false) return null;
        long before = _character.Stats(target)[2000];
        long floor = GuardianHpFloor(target, before);
        if (floor > 0) damage = Math.Min(damage, Math.Max(0, before - floor));
        long hp = Math.Max(0, before - damage);
        if (hp == 0 && await _buffs.TryPreventLethalAsync(target)) hp = _character.Stats(target)[2000];
        if (!ReferenceEquals(target.Role, targetRole) || target.SceneLoading || valid?.Invoke() == false) return null;
        target.CurrentHp = hp;
        if (hp == 0)
        {
            lock (_reviveClaims)
            {
                target.ReviveDeathSource = RewardSourceId.ProcessRun + "/" + Guid.NewGuid().ToString("N");
                target.Dead = true;
                _deathPublications.Add(target);
            }
        }
        return (damage, hp, Math.Max(0, before - hp), shieldDamage);
    }

    // ---- damage formula (inferred) ----

    private sealed record Fighter(long Atk, long Matk, long Hit, long Cri, int Level, int Element)
    {
        public long Def { get; init; }
        public long Mdef { get; init; }
        public long Flee { get; init; }
        public long ResCri { get; init; }
        /// <summary>A GM with GmDamageFix on: hits it deals or takes skip the DamageFloat roll.</summary>
        public bool FixedDamage { get; init; }
    }

    private (long Damage, DamageHitResult Result) Damage(Fighter a, Fighter b, JsonElement skill, int branch = 0,
        double bonusCrit = 0, bool forceCrit = false)
    {
        if (!NeverMisses(skill) && Rolls.Hit() >= HitRate(a, b, skill)) return (0, DamageHitResult.Miss);
        double dmg = BaseDamage(a, b, skill, branch, out bool magic);
        if (!a.FixedDamage && !b.FixedDamage) dmg *= 0.97 + Rolls.DamageFloat() * 0.06; // DamageFloat [9700, 10300]
        var result = DamageHitResult.Normal;
        // (inferred) Miguel's guardian crit bonus (bonusCrit/forceCrit) also lets a magic hit crit; nothing else changes.
        if (forceCrit || (!magic || bonusCrit > 0) && Rolls.Crit() < CritRate(a, b, skill) + bonusCrit)
        {
            dmg *= 1.5; // FormulaParamCriDmg 15000
            result = DamageHitResult.Critical;
        }
        return (Math.Max(1, (long)Math.Round(dmg)), result);
    }

    // Hit: dodge = FLEE/(FLEE + ParHit 600), attacker HIT adds back; HitRateRange [0.3, 1]; _iSkillHit 10000 never misses.
    private static double HitRate(Fighter a, Fighter b, JsonElement skill) => NeverMisses(skill) ? 1
        : Math.Clamp(1 - b.Flee / (double)(b.Flee + 600) + a.Hit / (double)(a.Hit + 600) * 0.5, 0.3, 1.0);

    /// <summary>
    /// _iSkillHit 10000 never misses (inferred). The data sets it per row: nearly every damaging skill, physical and
    /// magic, and 444 monster normal attacks carry 10000; the players' normal attacks and 55 monster ones (MVP swings,
    /// GvG and Pet Tower monsters) carry 0 and roll against FLEE. So FLEE dodges players and those monsters, not the
    /// rest, as the data was set up (#bug-reports "163 FLEE, every attack hits" is that rule, not a bug).
    /// </summary>
    internal static bool NeverMisses(JsonElement skill) => new Data.Config.SkillConfig(skill).SkillHit >= 10000;

    private static double CritRate(Fighter a, Fighter b, JsonElement skill) =>
        Math.Max(0, a.Cri - b.ResCri) / 10000.0 * GameData.Int(skill, "_iSkillCri") / 10000.0;

    /// <summary>SkillConfig._iAffectByArrow selects the equipped arrow ItemConfig._kParam1 element.</summary>
    internal int AttackElement(GameSession source, JsonElement row)
    {
        var skill = new SkillConfig(row);
        if (skill.AffectByArrow != 0 && source.BagState.Info.CurrentArrowID is var arrowId and > 0
            && ItemConfig.Get(_data, arrowId) is { } arrow && int.TryParse(arrow.Param1, out int element)
            && element is >= 1 and <= 10) return element;
        return (int)Math.Max(1, skill.EleType);
    }

    /// <summary>The roll-free part of a hit: skill ratio and flat, defense and element.</summary>
    private double BaseDamage(Fighter a, Fighter b, JsonElement skill, int branch, out bool magic)
    {
        magic = GameData.Int(skill, "_iDmgType") == 2;
        var ratios = Numbers(skill, "_kDamageParam1");
        var flats = Numbers(skill, "_kDamageParam2");
        double p1 = ratios.Length > branch ? ratios[branch] : 10000, p2 = flats.Length > branch ? flats[branch] : 0;
        double dmg = (magic ? a.Matk : a.Atk) * p1 / 10000 + p2;
        // Defense: def / (def + ParDef 20 × the defender's level), the shape of the client's damage-reduction tooltip.
        double def = magic ? b.Mdef : b.Def;
        dmg *= 1 - def / (def + 20.0 * Math.Max(1, b.Level));
        // Element: EleConfig row = defender element, column = attacker element (/10000), clamped [0.1, 10].
        int element = (int)GameData.Int(skill, "_iEleType") is var se and > 1 ? se : a.Element;
        return dmg * Math.Clamp(1 + ElementFactor(element, b.Element) / 10000.0, 0.1, 10);
    }

    /// <summary>The expected damage of one hit (hit rate, mean float, crit share) from the same formula.</summary>
    private double ExpectedDamage(Fighter a, Fighter b, JsonElement skill)
    {
        double dmg = Math.Max(1, BaseDamage(a, b, skill, 0, out bool magic));
        return HitRate(a, b, skill) * dmg * (magic ? 1 : 1 + 0.5 * CritRate(a, b, skill));
    }

    /// <summary>
    /// A player's and a monster's expected damage per second against each other with their normal attacks, the
    /// same fighters and formula as a real exchange (no buffs, skills, potions or movement). The reconstructed story
    /// bosses are tuned with it (Reconstructed/dungeon-monsters.json soloTuning, DungeonTests.StoryBossTuning).
    /// </summary>
    internal (double PlayerDps, double MonsterDps, long PlayerHp) Duel(GameSession p, MonsterTemplate m)
    {
        var stats = _character.Stats(p);
        var playerSkill = _data.Row("SkillConfig", _skills.NormalAttack(p))!.Value;
        var player = new Fighter(stats[103], stats[104], stats[109], stats[111], p.Progress!.Level, AttackElement(p, playerSkill));
        var asTarget = new Fighter(0, 0, m.Flee, 0, m.Level, m.Element) { Def = m.Def, Mdef = m.Mdef, Flee = m.Flee };
        double playerDps = ExpectedDamage(player, asTarget, playerSkill) * 1000.0 / Math.Max(1, SkillMs(p, playerSkill));
        var monster = new Fighter(m.Atk, m.Matk, m.Hit, m.Cri, m.Level, m.Element);
        var defender = new Fighter(0, 0, 0, 0, p.Progress.Level, 1) { Def = stats[105], Mdef = stats[106], Flee = stats[110], ResCri = stats[112] };
        double monsterHit = _data.Row("SkillConfig", m.NormalSkill) is { } row ? ExpectedDamage(monster, defender, row) : Math.Max(1, m.Atk);
        return (playerDps, monsterHit * 1000.0 / Math.Max(1, m.SwingIntervalMs), stats[101]);
    }

    private static double? FirstNumber(JsonElement row, string field) =>
        row.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0 ? GameData.Number(v[0]) : null;

    private static readonly string[] ElementColumns = ["_iNone", "_iWind", "_iEarth", "_iFire", "_iWater", "_iPoison", "_iHoly", "_iShadow", "_iGhost", "_iUndead"];

    private double ElementFactor(int attacker, int defender)
    {
        if (attacker is < 1 or > 10 || _data.Row("EleConfig", defender) is not { } row) return 0;
        return GameData.Int(row, ElementColumns[attacker - 1]);
    }

    // ---- stance, death, revive, regen ----

    private async Task FightingAsync(GameSession p)
    {
        p.LastCombatMs = Now;
        if ((p.StatusBits & FightBit) != 0) return;
        p.StatusBits |= FightBit;
        await ToPlayerAndViewersAsync(p, AoiAttrChange, Status(p.Role!.Id, p.StatusBits));
    }

    private async Task FightingAsync(MonsterUnit m)
    {
        m.LastCombatMs = Now;
        if ((m.StatusBits & FightBit) != 0) return;
        m.StatusBits |= FightBit;
        _fightingMonsters.Add(m);
        await _world.ToUnitViewersAsync(m, AoiAttrChange, Status(m.Uid, m.StatusBits));
    }

    /// <summary>31048 state_info: the whole status word in values[0] (one element, even 0).</summary>
    private static AOIAttrChange Status(long uid, long bits) => new()
    {
        EntityUuid = (ulong)uid,
        StateInfo = new AoiObjStateInfo { Data = new CommonBigMask { Values = { (ulong)bits } } },
    };

    /// <summary>the Dead state to the player and its viewers, the auto-revive countdown.</summary>
    private async Task PlayerDiesAsync(GameSession p)
    {
        bool ownsPublication;
        lock (_reviveClaims) ownsPublication = _deathPublications.Add(p);
        try
        {
            _eventAttacks.Remove(p);
            if (!p.Dead || p.ReviveDeathSource == null)
                p.ReviveDeathSource = RewardSourceId.ProcessRun + "/" + Guid.NewGuid().ToString("N");
            p.Dead = true;
            await ClearSkillsAsync(p);
            await _buffs.ClearOnDeathAsync(p);
            _log.LogInformation("{Name} died", p.Role?.Name);
            await ToPlayerAndViewersAsync(p, EntityBaseChange, LifeState(p));
            if (!ReviveInfoOwners.Any(owns => owns(p)))
                await p.SendGameAsync(ReviveInfo, new SyncClientPlayerReviveInfoNtf
                {
                    BDead = true, ReviveTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 30, SceneEntry = p.SceneEntry,
                });
            foreach (var handler in PlayerDied) await handler(p);
        }
        finally { if (ownsPublication) ReleaseDeathPublication(p); }
    }

    /// <summary>
    /// Damage without an attacker (the Survivor Battle poison zone, inferred): the same protected HP transition as a
    /// monster hit (shields, lethal prevention), the HP publication and, when lethal, the ordinary death. Returns the
    /// HP actually lost.
    /// </summary>
    public async Task<long> EnvironmentDamageAsync(GameSession p, long damage, Func<bool>? valid = null)
    {
        if (damage <= 0 || p.Role == null) return 0;
        var committed = await CommitPlayerDamageAsync(p, damage, null, valid);
        if (committed == null) return 0;
        var (_, hp, effective, _) = committed.Value;
        try
        {
            await _character.SetHpSpAsync(p, null, null);
            if (hp == 0) await PlayerDiesAsync(p);
        }
        finally { if (hp == 0) ReleaseDeathPublication(p); }
        return effective;
    }

    /// <summary>
    /// GmKillEntity and a GmChangeHp that takes the last HP: HP 0 and the ordinary death, past shields, lethal
    /// prevention and GmUnlimit (inferred: a GM kill is meant to land).
    /// </summary>
    internal async Task KillPlayerAsync(GameSession p)
    {
        using (await CharacterService.Lock(p))
        {
            if (p.Dead || p.Role == null || p.SceneLoading) return;
            p.CurrentHp = 0;
            lock (_reviveClaims)
            {
                p.ReviveDeathSource = RewardSourceId.ProcessRun + "/" + Guid.NewGuid().ToString("N");
                p.Dead = true;
                _deathPublications.Add(p);
            }
        }
        try
        {
            await _character.SetHpSpAsync(p, null, null);
            await PlayerDiesAsync(p);
        }
        finally { ReleaseDeathPublication(p); }
    }

    private static EntityBase LifeState(GameSession p) => new()
    {
        Uuid = (ulong)p.Role!.Id,
        EntityTemplateID = (ulong)p.Role.Career,
        EntityCamp = p.EventFaction > 0 ? p.EventFaction : 1,
        ElementType = 0,
        State = p.Dead ? EntityState.Dead : EntityState.Alive,
        NotifyType = EntityBase.Types.NotifyType.Base,
    };

    private readonly HashSet<GameSession> _reviveClaims = [];
    private readonly HashSet<GameSession> _deathPublications = [];
    private readonly Dictionary<GameSession, TaskCompletionSource> _reviveAvailabilityWaiters = [];

    private void ReleaseDeathPublication(GameSession p)
    {
        TaskCompletionSource? completed;
        lock (_reviveClaims)
        {
            _deathPublications.Remove(p);
            completed = TakeReviveAvailabilityWaiter(p);
        }
        completed?.TrySetResult();
    }

    /// <summary>Let an independent scene-return request wait for reserved death publication and any existing revive
    /// claim before attempting recovery. Callers must recheck role/scene ownership and claim synchronously after
    /// awaiting; never wait while owning either reservation (including from a PlayerDied callback).</summary>
    public Task WaitForReviveAvailabilityAsync(GameSession p)
    {
        lock (_reviveClaims)
        {
            if (!_deathPublications.Contains(p) && !_reviveClaims.Contains(p)) return Task.CompletedTask;
            if (!_reviveAvailabilityWaiters.TryGetValue(p, out var pending))
                _reviveAvailabilityWaiters[p] = pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return pending.Task;
        }
    }

    // Called only under _reviveClaims: publication and a previous revival must both be finished.
    private TaskCompletionSource? TakeReviveAvailabilityWaiter(GameSession p) =>
        !_deathPublications.Contains(p) && !_reviveClaims.Contains(p)
            && _reviveAvailabilityWaiters.Remove(p, out var completed) ? completed : null;

    /// <summary>Reserve one dead player's resurrection before any payment or network await.</summary>
    public bool TryClaimRevive(GameSession p)
    {
        lock (_reviveClaims) return p.Dead && !_deathPublications.Contains(p) && _reviveClaims.Add(p);
    }

    public void ReleaseReviveClaim(GameSession p)
    {
        TaskCompletionSource? completed;
        lock (_reviveClaims)
        {
            _reviveClaims.Remove(p);
            completed = TakeReviveAvailabilityWaiter(p);
        }
        completed?.TrySetResult();
    }

    /// <summary>Single successful dead-to-alive transition with HP/SP and the cached AOI state set before any send.</summary>
    public bool CommitReviveClaim(GameSession p, long hpRate = 10000, long spRate = 10000)
    {
        lock (_reviveClaims)
        {
            if (!_reviveClaims.Contains(p) || !p.Dead) return false;
            var stats = _character.Stats(p);
            p.CurrentHp = Math.Max(1, stats[101] * Math.Clamp(hpRate, 0, 10000) / 10000);
            p.CurrentSp = stats[102] * Math.Clamp(spRate, 0, 10000) / 10000;
            p.Dead = false;
            p.WorldState.ReviveSource = 0;
            p.WorldState.ReviveExpiresMs = 0;
            _character.Refresh(p);
            return true;
        }
    }

    /// <summary>31013: back to life at the map's resurrection point with full HP/SP (ReviveHP/MP 10000).</summary>
    public async Task<bool> ReviveAsync(GameSession p, EntityReliveReq req)
    {
        if (!TryClaimRevive(p))
        {
            await p.SendGameAsync(Relive, new EntityReliveAck { Result = p.Dead ? ErrorCodes.FeatureUnavailable : ErrorCodes.TargetAlive, EntityID = (ulong)p.Role!.Id });
            return false;
        }
        return await ReviveClaimedAsync(p);
    }

    /// <summary>Use after TryClaimRevive, including the dungeon path that must validate its own quota first.</summary>
    public async Task<bool> ReviveClaimedAsync(GameSession p, Action? onCommitted = null)
    {
        try
        {
            bool committed = CommitReviveClaim(p);
            if (committed) onCommitted?.Invoke();
            await p.SendGameAsync(Relive, new EntityReliveAck { Result = committed ? 0 : ErrorCodes.TargetAlive, EntityID = (ulong)p.Role!.Id });
            if (!committed) return false;
            var map = _worldData.Map(p.SceneEntry);
            await PublishReviveAsync(p, map?.Revive ?? map?.Born);
            return true;
        }
        finally { ReleaseReviveClaim(p); }
    }

    /// <summary>Test seam: runs after the first revive packet so a role handover can detach the session mid-publication.</summary>
    internal Func<GameSession, Task>? ReviveBroadcastedForTest;

    /// <summary>Publish an already committed resurrection; never changes the dead/alive state. A role handover can
    /// detach the session (Role = null, RoleHandover.cs:100) at any await; the reconnect reloads the committed state,
    /// so each step first checks that the role is still attached.</summary>
    public async Task PublishReviveAsync(GameSession p, MapPoint? point)
    {
        if (p.Role == null) return;
        await ToPlayerAndViewersAsync(p, EntityBaseChange, LifeState(p));
        if (ReviveBroadcastedForTest is { } seam) await seam(p);
        if (p.Role == null) return;
        if (point is { } at) await _scene.TeleportInMapAsync(p, at);
        if (p.Role == null) return;
        await _character.SetHpSpAsync(p, null, null); // send committed/current health; never overwrite later damage
        if (p.Role == null) return;
        await p.SendGameAsync(ReviveInfo, new SyncClientPlayerReviveInfoNtf { BDead = p.Dead, SceneEntry = p.SceneEntry });
        if (p.Role == null) return;
        await RefreshRevivedAsync(p);
    }

    /// <summary>Restore revive-dependent effects, including totem passives. Scene-return revival calls this before
    /// ENTITYSELF because the usual in-map revive publication requires an entity already installed by the client.</summary>
    internal async Task RefreshRevivedAsync(GameSession p)
    {
        foreach (var handler in PlayerRevived) await handler(p);
    }

    public void Tick(GameTime time)
    {
        long now = Now;
        TickEventPvP(now);
        foreach (var s in _sessions.All())
        {
            if (s.Role == null || s.Progress == null) continue;
            if (s.SkillObjects.Count > 0)
                foreach (var unit in s.SkillObjects.ToArray()) _loop.TrackRuntimeDetached("skill object tick", () => TickSkillObjectAsync(unit));
            if (s.Attack is { } a && !s.Dead && now >= a.NextSwingMs)
            {
                if (s.SceneLoading || !CanHitMonster(s, a.Target) || MonsterService.Distance(a.Target, s) > 30)
                {
                    s.Attack = null;
                    if (s.SkillCast?.Request.SkillId == a.SkillId)
                        _loop.TrackRuntimeDetached("cast cancellation", () => CancelCastAsync(s, SkillInterruptedInfo.Types.SkillInterruptType.LockerInvalid));
                    else _loop.TrackRuntimeDetached("skill end send", () => s.SendGameAsync(SkillEnd, new SkillEndNotify
                    {
                        SrcID = s.Role.Id, Type = SkillEndNotify.Types.SkillEndType.Interrupted,
                        InterruptedInfo = new SkillInterruptedInfo { Type = SkillInterruptedInfo.Types.SkillInterruptType.LockerInvalid },
                    }));
                }
                else if (s.SkillCast == null && EffectiveSkillRow(s, a.SkillId) is { } row && MonsterService.Distance(a.Target, s) <= Range(row) + 1.5f)
                {
                    a.NextSwingMs = now + SkillMs(s, row);
                    _loop.TrackRuntimeDetached("combat swing", () => SwingAsync(s, a.Target, a.SkillId, ++_sessionIds));
                }
            }
            if ((s.StatusBits & FightBit) != 0 && now - s.LastCombatMs > OutOfCombatMs)
            {
                s.StatusBits &= ~FightBit;
                _loop.TrackRuntimeDetached("combat status send", () => ToPlayerAndViewersAsync(s, AoiAttrChange, Status(s.Role.Id, s.StatusBits)));
            }
        }
        if (_fightingMonsters.Count > 0)
            foreach (var m in _fightingMonsters.Where(m => (m.StatusBits & FightBit) == 0 || now - m.LastCombatMs > OutOfCombatMs).ToArray())
            {
                _fightingMonsters.Remove(m);
                if ((m.StatusBits & FightBit) == 0) continue;
                m.StatusBits &= ~FightBit;
                _loop.TrackRuntimeDetached("monster status send", () => _world.ToUnitViewersAsync(m, AoiAttrChange, Status(m.Uid, m.StatusBits)));
            }
        // Every role still regenerates once per AutoRecoverInterval, but in its own phase (role id slot), so one
        // tick no longer computes every online stat sheet at once (150 roles = a 116 ms tick every 5 s).
        long slot = now / GameLoop.TickInterval.Milliseconds;
        if (slot != _regenSlot)
        {
            long slots = RegenIntervalMs / GameLoop.TickInterval.Milliseconds;
            // A late tick also serves the phases it skipped, so no role misses a cycle.
            long first = _regenSlot < 0 ? slot : Math.Max(_regenSlot + 1, slot - slots + 1);
            _regenSlot = slot;
            foreach (var s in _sessions.All())
                if (s.Role != null)
                {
                    long phase = (s.Role.Id % slots + slots) % slots;
                    for (long k = first; k <= slot; k++)
                        if (k % slots == phase)
                        {
                            var role = s.Role;
                            var owner = s.Owner;
                            var attachment = s.Attachment;
                            bool CurrentTick() => ReferenceEquals(s.Role, role)
                                && (_loop.Owners?.IsCurrentAttachedTimer(s, owner, attachment) ?? true);
                            _loop.TrackRuntimeDetached("combat regeneration", () => RegenAsync(s, CurrentTick));
                            break;
                        }
                }
        }
    }

    /// <summary>LC_DAL_PlayerInfo.TryGetAttrValue：FinalReHp／Sp = ReHP／SP + MaxHP／SP × PreReHP／SP。
    /// 整數入帳對齊原生顯示向上取整是本地政策（inferred）；原服權威入帳取整仍未確認。</summary>
    internal static (long Hp, long Sp) Regeneration(StatSheet stats) =>
        (RegenerationAmount(stats[AttributeId.ReHp], stats[AttributeId.MaxHp], stats[AttributeId.PerReHp]),
            RegenerationAmount(stats[AttributeId.ReSp], stats[AttributeId.MaxSp], stats[AttributeId.PerReSp]));

    private static long RegenerationAmount(long flat, long maximum, long percent)
    {
        Int128 numerator = (Int128)flat * 10000 + (Int128)Math.Max(0, maximum) * percent;
        if (numerator <= 0) return 0;
        Int128 amount = (numerator + 9999) / 10000;
        return amount <= 0 ? 0 : amount >= long.MaxValue ? long.MaxValue : (long)amount;
    }

    internal static long RegeneratedValue(long current, long maximum, long amount)
    {
        long cap = Math.Max(0, maximum), before = Math.Clamp(current, 0, cap);
        return amount <= 0 ? before : amount >= cap - before ? cap : before + amount;
    }

    internal async Task RegenAsync(GameSession s, Func<bool> current)
    {
        if (!current() || s.Role == null || s.Progress == null) return;
        IDisposable held;
        try { held = await CharacterService.Lock(s); }
        catch (OperationCanceledException) when (!current()) { return; }
        using (held)
        {
            if (!current() || s.Role == null || s.Progress == null || s.Dead) return;
            if (s.CurrentHp == null && s.CurrentSp == null) return; // unset = full (CharacterService.Stats): nothing to restore
            var st = _character.Stats(s);
            if (st[AttributeId.Hp] >= st[AttributeId.MaxHp] && st[AttributeId.Sp] >= st[AttributeId.MaxSp]) return;
            var (rehp, resp) = Regeneration(st);
            // 戰鬥中SP仍每五秒恢復是本地政策（inferred）；HP保留原有脫戰限制。
            long hp = RegeneratedValue(st[AttributeId.Hp], st[AttributeId.MaxHp], (s.StatusBits & FightBit) == 0 ? rehp : 0);
            long sp = RegeneratedValue(st[AttributeId.Sp], st[AttributeId.MaxSp], resp);
            if (hp == st[AttributeId.Hp] && sp == st[AttributeId.Sp]) return;
            await _character.SetHpSpAsync(s, hp, sp);
        }
    }

    private async Task ToPlayerAndViewersAsync(GameSession p, uint msgId, IMessage body)
    {
        await p.SendGameAsync(msgId, body);
        var viewers = _world.Viewers(p);
        if (viewers.Count == 0) return;
        var prepared = new PreparedMessage(msgId, body);
        foreach (var viewer in viewers) await viewer.SendPreparedAsync(prepared);
    }
}
