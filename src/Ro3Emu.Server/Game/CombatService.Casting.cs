using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Server.Game;

internal readonly record struct CastProjectile(int EffectId, bool PositionTarget, int CastEffectId = 0);

/// <summary>A single accepted cast, bound to its role, scene and generation (LC_Scene_Sync._skillEndNotify).</summary>
public sealed class ActiveSkillCast
{
    public required long Id { get; init; }
    public required long OwnerId { get; init; }
    public required long WorldScene { get; init; }
    public long SceneGeneration { get; init; }
    public Ro3Emu.Server.Game.Ownership.AttachmentLease? Attachment { get; init; }
    public required CastSkillReq Request { get; init; }
    public required JsonElement Row { get; init; }
    public required Vec3 Origin { get; set; }
    /// <summary>Accepted while the caster still walked (WalkSlack): its stop right after is where it cast from.</summary>
    public bool StartedWalking { get; set; }
    /// <summary>The caster's last walk message when the cast was accepted: the walk its stop can have covered starts there.</summary>
    public DateTime WalkFromUtc { get; init; }
    public required long StartMs { get; init; }
    public required int ChantMs { get; init; }
    public required int ActionMs { get; init; }
    public bool Cancelled { get; set; }
    public bool Resolved { get; set; }
    internal bool CompletionPublished { get; set; }
    internal bool PassiveCastResolved { get; set; }
    internal object? PassiveSourceEpoch { get; set; }
    /// <summary>同一次原施放各段已發布的玩家命中；不改變既有 CastResolved 的怪物參數契約。</summary>
    internal List<PublishedPlayerCastHit> PublishedPlayerHits { get; } = [];
    internal int AuthoredStageIndex { get; set; } = -1;
    internal int AuthoredStageCount { get; set; }
    internal CombatService.AuthoredDamageStage AuthoredStage { get; set; }
    internal bool AuthoredStagesFinished { get; set; }
    internal HashSet<long> AuthoredStatusApplied { get; } = [];
    internal HashSet<long>? AttackShieldTargets { get; set; }
    internal int AttackShieldApplied { get; set; }
    internal IReadOnlyList<MonsterUnit>? ReadingMonsters { get; set; }
    internal IReadOnlyDictionary<GameSession, Func<bool>>? ReadingPlayers { get; set; }
    /// <summary>此施放已排程來源目標的彈道；命中通知不得為同一目標再送一次。</summary>
    internal bool HasTimedBullet { get; set; }
    /// <summary>已由場景權威允許的空瞄；保留施放與消耗，不在命中時重新搜尋附近單位。</summary>
    internal bool PositionOnly { get; set; }
    /// <summary>場景砲彈在施放動作結束後獨立飛行，仍綁定角色、連線與原場景。</summary>
    internal bool IndependentProjectile { get; set; }
    public SkillObject? Operator { get; init; }
    /// <summary>Extra effects remain bound to their equipped source after asynchronous publication.</summary>
    internal Func<bool>? SourceEligible { get; init; }
    internal Dictionary<long, long>? GuardianDamage { get; init; }
    internal SemaphoreSlim? GuardianGate { get; init; }
    internal HashSet<int>? GuardianOnce { get; init; }
    public long SourceId => Operator?.Uid ?? OwnerId;
}

public sealed partial class CombatService
{
    public const uint SkillMove = 31112, TrapDamage = 31128;

    /// <summary>
    /// 31102: row identity, ownership, selector, mode, all costs and cooldowns are checked before the cast commits.
    /// Target/position come from CastSkillReq; m_kSelectInfo uses relation masks, never _iDmgType as a selector.
    /// Evidence: LC_Skill_Function.SendSkill, SkillConfig.GetCost*, MG_Skill.proto.
    /// </summary>
    public Task CastAsync(GameSession s, CastSkillReq request) => CastAsync(s, request, lineup: false);

    /// <param name="lineup">One skill of the Skill Bar's Lineup, cast by the server for the player: a skill that cannot
    /// be cast now is skipped without a refusal, and the client gets no cast reply it did not ask for.</param>
    private async Task CastAsync(GameSession s, CastSkillReq request, bool lineup)
    {
        LegacyOwnerScope? admission = null;
        OwnerNotices? notices = null;
        if (s.Owners is { } owners && s.Role is { } role)
            admission = await owners.EnterWithGateAsync("combat/cast", s.SkillCastGate, [ReservationKey.Role(role.Id)]);
        else await s.SkillCastGate.WaitAsync();
        try
        {
            using var state = await CharacterService.Lock(s);
            // Complete the runtime admission before any socket send can suspend. Frozen packets
            // retain the original order; an obsolete attachment cannot start a committed cast.
            if (s.Owners?.Current is { Notices: null } context) notices = new OwnerNotices(context);
            if (IsGuardianRequest(request)) await CastGuardianAsync(s, request.Clone());
            else await CastCoreAsync(s, request, lineup);
        }
        finally { notices?.Dispose(); s.SkillCastGate.Release(); admission?.Dispose(); }
        if (notices != null) await notices.FlushAsync();
    }

    /// <summary>A pet wrapper or its child effect: the guardian path validates and runs it (31102).</summary>
    private bool IsGuardianRequest(CastSkillReq request) => _data.Row("SkillConfig", request.SkillId) is { } row
        && new SkillConfig(row).SystemType is GuardianPetRules.WrapperType or GuardianPetRules.EffectType;

    private async Task CastCoreAsync(GameSession s, CastSkillReq request, bool lineup = false)
    {
        var req = request.Clone();
        var row = EffectiveSkillRow(s, req.SkillId);
        bool granted = row != null && SkillGrants.Any(grant => grant(s, req));
        // A mounted gameplay-status skill can name its scene actor as the operator. Its effects still originate from
        // the role's authoritative position, as the ordinary client-side custom-skill path does.
        if (granted) req.OpUnitId = 0;
        var owned = req.OpUnitId == 0 || req.OpUnitId == s.Role?.Id ? null
            : s.SkillObjects.FirstOrDefault(u => u.Uid == req.OpUnitId && u.WorldScene == s.WorldScene && u.ExpiresMs > Now);
        int error = s.Role == null || s.Progress == null || s.Dead || s.SceneLoading ? ErrorCodes.SkillBadState
            : row == null ? ErrorCodes.SkillUnknown
            : req.OpUnitId != 0 && req.OpUnitId != s.Role.Id && owned == null ? ErrorCodes.SkillRequirements
            : !float.IsFinite(req.Face) || req.TargetPos is { } pos && !Finite(pos) ? ErrorCodes.Parameter : 0;
        int? own = null;
        if (error == 0)
        {
            req.SkillId = (int)new SkillConfig(row!.Value).Id;
            own = owned == null ? OwnSkillAdmit?.Invoke(s, req.SkillId) : null;
            if (own is { } ownError && ownError != 0) error = ownError;
            else if (own == null && !granted
                && (owned != null ? !owned.Skills.Contains(req.SkillId)
                    : !Learned(s, row!.Value) && s.GmCastSkill != request.SkillId
                        && !GrantedSkill.Exists(grant => grant(s, req.SkillId))))
                error = ErrorCodes.SkillNotLearned;
            else if (!SkillModeAllowed(s, row!.Value)) error = ErrorCodes.SkillRequirements;
            else if (!KnownSkillConditionsMet(s, row.Value, owned,
                request.OpUnitId == 0 || request.OpUnitId == s.Role?.Id)) error = ErrorCodes.SkillRequirements;
            else if (new SkillConfig(row!.Value).BasicType != 1) error = ErrorCodes.SkillPassive;
            else if (_buffs.ActionBlocked(s, new SkillConfig(row.Value).IsNormalAttack(_data)
                ? ForbiddenState.NormalAttack : ForbiddenState.CastSkill, (int)new SkillConfig(row.Value).SkillId)) error = ErrorCodes.SkillBadState;
            else if (req.SkillId == PublicSkill("WorldTreeSkillId") && _buffs.HasForbidden(s, ForbiddenState.UseItem)) error = ErrorCodes.ItemUseForbidden;
            else if (req.SkillId == PublicSkill("WorldTreeSkillId") && !ItemAllowedInMap(s, (int)PublicSkill("WorldTreeItemId"))) error = ErrorCodes.ItemUseAreaForbidden;
            else if (req.IsExtraSkill && owned == null && new SkillConfig(row.Value).ExtraType == 0) error = ErrorCodes.SkillRequirements;
        }
        var origin = owned?.Pos ?? s.Position;
        if (error == 0)
        {
            int group = (int)new SkillConfig(row!.Value).SkillId, mode = (int)new SkillConfig(row.Value).Mode;
            if (own == null && Math.Max(s.SkillCds.GetValueOrDefault(group), s.SkillModeCds.GetValueOrDefault((group, mode))) > Now) error = ErrorCodes.SkillCooldown;
            // Preserve the established cooldown refusal for a repeated cast, including during its action tail.
            else if (s.SkillCast is { Cancelled: false } active && Now < active.StartMs + active.ChantMs + active.ActionMs
                && (!new SkillConfig(row.Value).CancelCast || RepeatsActiveSkill(active, row.Value, req)
                    || SwingInFlight(active, row.Value, req))) error = ErrorCodes.SkillBusy;
            else
            {
                error = TargetAimErrors.Select(validate => validate(s, req)).FirstOrDefault(value => value != 0);
                if (error == 0)
                {
                    error = ValidateTarget(s, row.Value, req, origin, owned == null ? WalkSlack(s) : 0,
                        _data.Row("SkillConfig", request.SkillId) is { } askedRow ? Range(askedRow) : 0);
                    if (error != 0 && SkillTargetErrorOverrides.Any(allow => allow(s, req, error))) error = 0;
                }
            }
        }
        // GmUnlimit's "no consumption" (inferred): no HP, SP, item, buff pile or energy cost.
        bool unlimited = (s.GmCombat & GmCombatSwitches.Unlimited) != 0;
        var costs = row is { } value && !unlimited ? ResolveCosts(s, value) : new CastCosts();
        long errorParam = 0;
        if (error == 0) (error, errorParam) = ValidateCosts(s, costs);
        if (error == 0 && !unlimited && !_buffs.CanSpendEnergy(s, row!.Value)) error = ErrorCodes.SkillEnergyShort;
        if (error != 0)
        {
            if (!lineup) await RefuseCastAsync(s, req, error, errorParam);
            return;
        }

        long id = ++_sessionIds;
        var lease = s.Attachment;
        long scene = s.SceneGeneration, worldScene = s.WorldScene;
        // E05c: the receipt is the paid action's admission. Repeating a protocol request
        // is a new intent and pays again; a lost writer ACK resolves this exact source.
        if (!await AdmitCastCostAsync(s, row!.Value, req, costs, id))
        {
            if (!lineup) await RefuseCastAsync(s, req, ErrorCodes.SkillCostShort, costs.Items[0].Entry);
            return;
        }
        if (lease?.Cancelled == true || !ReferenceEquals(s.Attachment, lease) ||
            s.SceneGeneration != scene || s.WorldScene != worldScene || s.SceneLoading) return;
        int turn = owned == null ? SwingTurnLeft(s.SkillCast, row!.Value) : 0;
        await CancelCastAsync(s, SkillInterruptedInfo.Types.SkillInterruptType.Skill);
        _eventAttacks.Remove(s);
        await PayCostsAsync(s, costs);
        if (!unlimited) await _buffs.SpendEnergyAsync(s, row!.Value);
        int lead = (owned == null && BeforeCast is { } before ? await before(s) : 0) + turn;
        if (!lineup) await s.SendGameAsync(UseSkill, new CastSkillRsp { SessionId = id, IsAutoCast = req.IsAutoCast });
        var target = FindMonster(s.WorldScene, req.TargetId);
        var playerTarget = EventTarget(s, req.TargetId);
        // _iIfAutoAtk 2 (enum_AutoNormalAttackType.AutoNormalAttack) follows the cast with the career's normal attack
        // (LC_AutoAttackAppendState: GetNormalSkill). Repeating the cast skill itself bypassed its cooldown: Blade
        // Smash and Sonic Slash fired every swing.
        int loopSkill = _skills.IsCurrentNormalAttack(s, req.SkillId) ? req.SkillId : _skills.NormalAttack(s);
        if (new SkillConfig(row!.Value).IfAutoAtk == 2 && owned == null && target != null && CanHitMonster(s, target))
        {
            s.Attack = new AttackLoop
            {
                Target = target, SkillId = loopSkill,
                NextSwingMs = Now + lead + SkillMs(s, row.Value) + ChantMs(s, row.Value) + 1,
                StartX = s.Position.X, StartZ = s.Position.Z, StartedWalking = s.Walking, StartMs = Now,
                ArrivalSpeed = s.Speed / 100f, WalkFromUtc = s.LastWalkUtc
            };
        }
        else s.Attack = null;
        if (new SkillConfig(row.Value).IfAutoAtk == 2 && owned == null && playerTarget != null)
            _eventAttacks[s] = new EventAttack(playerTarget, loopSkill,
                Now + lead + SkillMs(s, row.Value) + ChantMs(s, row.Value) + 1,
                s.Position.X, s.Position.Z);
        if (own != null) await OwnSkillCast!(s, req.SkillId);
        else await StartCooldownAsync(s, row.Value, SkillConfig.Get(_data, request.SkillId) is { } asked ? (int)asked.Mode : null);
        await StartCastAsync(s, row.Value, req, id, owned, lead);
        if (owned == null && !lineup && new SkillConfig(row.Value).SystemType == SkillService.QueueSystemType)
            StartLineup(s, s.SkillCast);
    }

    /// <summary>
    /// The Skill Bar (SkillConfig._iSystemType 6, _kScriptLogic jinengduilie*): "Activating the Skill Bar casts each skill
    /// in the Skill Bar that currently meets its activation requirements once" (client text). The client only lists,
    /// checks and shows the Lineup: its button sends a plain 31102 for the Skill Bar row (UI_Sys_Main_JoyStickSkillQueueItem
    /// WidgetCtrl, usable while any member is off cooldown) and on its success lights the members' dots in slot order,
    /// skipping those on cooldown (_startShowSkillQueueFX); it never sends the members. The casting is this script (inferred
    /// timing): each member is cast on the player in slot order, one after the previous one's action (a new cast ends the
    /// running one, and an ended cast never lands), and one that cannot be cast now (cooldown, SP, state) is skipped.
    /// </summary>
    private void StartLineup(GameSession s, ActiveSkillCast? bar)
    {
        if (bar == null || s.SkillPlans is not { } plans || s.Progress == null) return;
        var members = plans.SkillAssignPlans[plans.CurrentPlanIndex].SkillQueue.OrderBy(kv => kv.Key)
            .Select(kv => kv.Value).Where(m => m.SkillId > 0).Select(m => (m.SkillId, m.SkillLevel)).ToList();
        if (members.Count == 0) return;
        var run = new object();
        s.LineupRun = run; // a second press replaces a Lineup still running (inferred)
        var step = new LineupStep(s, run, members, s.SceneGeneration, s.WorldScene);
        ScheduleRuntime(step.WorldScene, TimeSpan.FromMilliseconds(Math.Max(0, bar.StartMs - Now) + bar.ChantMs + bar.ActionMs + 1),
            () => LineupStepAsync(step, 0, waited: false));
    }

    private sealed record LineupStep(GameSession Session, object Run, List<(int Group, int Level)> Members, long Generation, long WorldScene);

    private async Task LineupStepAsync(LineupStep step, int index, bool waited)
    {
        var s = step.Session;
        for (; index < step.Members.Count; index++, waited = false)
        {
            if (!ReferenceEquals(s.LineupRun, step.Run) || s.Dead || s.SceneLoading || s.Role == null || s.Progress == null
                || s.SceneGeneration != step.Generation || s.WorldScene != step.WorldScene)
                return;
            // Another cast still in its action (the player's own, pressed meanwhile) would refuse the member as busy:
            // the member waits for it once, then is skipped if a cast is still running (inferred).
            if (!waited && s.SkillCast is { Cancelled: false } running && Now < running.StartMs + running.ChantMs + running.ActionMs)
            {
                int resume = index;
                ScheduleRuntime(step.WorldScene, TimeSpan.FromMilliseconds(running.StartMs + running.ChantMs + running.ActionMs + 1 - Now),
                    () => LineupStepAsync(step, resume, waited: true));
                return;
            }
            var (group, level) = step.Members[index];
            int learned = _skills.Level(s.Progress, group);
            int id = learned > 0 ? _skills.RealId(group, Math.Min(level, learned)) : 0;
            if (id <= 0) continue;
            await CastAsync(s, new CastSkillReq { SkillId = id, TargetId = s.Role.Id, TargetPos = s.Position.Clone(), Face = s.Face }, lineup: true);
            if (s.SkillCast is not { Cancelled: false } cast || cast.Request.SkillId != id) continue; // skipped
            int next = index + 1;
            if (next < step.Members.Count)
                ScheduleRuntime(step.WorldScene, TimeSpan.FromMilliseconds(Math.Max(0, cast.StartMs - Now) + cast.ChantMs + cast.ActionMs + 1),
                    () => LineupStepAsync(step, next, waited: false));
            else if (ReferenceEquals(s.LineupRun, step.Run)) s.LineupRun = null;
            return;
        }
        if (ReferenceEquals(s.LineupRun, step.Run)) s.LineupRun = null;
    }

    /// <summary>Cast item cost and frozen intent share one owner receipt; HP/SP/buffs remain runtime resources.
    /// The admission uses the same scheduled cast and lethal pipeline as every ordinary skill.</summary>
    private async Task<bool> AdmitCastCostAsync(GameSession s, JsonElement row, CastSkillReq request, CastCosts costs, long id)
    {
        if (costs.Items.Count == 0) return true;
        var mutation = _character.ProgressionEconomy(s, "cd13c/cast-admit", costs.Items, [],
            new
            {
                SessionId = id, Skill = row.GetRawText(), Request = Convert.ToBase64String(Google.Protobuf.MessageExtensions.ToByteArray(request)),
                Plan = s.SkillPlans?.CurrentPlanIndex, Attachment = s.Attachment?.Generation,
                s.WorldScene, s.SceneGeneration, AttributeCosts = costs.Attrs, BuffCosts = costs.Buffs,
                EnergyCosts = costs.Energy, Outcome = "admitted"
            });
        return await _character.ExchangeLockedAsync(s, costs.Items, [], popup: false,
            persist: tx => tx.WithEconomy(mutation), asyncCommit: true) != null;
    }

    private Task RefuseCastAsync(GameSession s, CastSkillReq req, int error, long parameter) => RefuseCoreAsync(s, req, error, parameter);

    // Refusal must not interrupt a different accepted cast: the client does not compare 31103.sessionId.
    private async Task RefuseCoreAsync(GameSession s, CastSkillReq req, int error, long parameter)
    {
        // LC_PbHelper formats error 3012's first parameter through SkillConfig._iName, using the skill row ID.
        if (error == ErrorCodes.SkillCooldown) parameter = req.SkillId;
        var reply = new CastSkillRsp { Result = error, IsAutoCast = req.IsAutoCast };
        if (parameter != 0) reply.Params.Add(parameter);
        await s.SendGameAsync(UseSkill, reply);
        if (s.SkillCast is not { Cancelled: false })
        {
            var end = new SkillEndNotify { SrcID = s.Role?.Id ?? 0, Type = SkillEndNotify.Types.SkillEndType.Interrupted, Result = error };
            if (parameter != 0) end.Params.Add(parameter);
            await s.SendGameAsync(SkillEnd, end);
        }
    }

    /// <summary>
    /// (inferred safeguard) A repeated non-normal row cannot restart its own chant/action when its configured CD is
    /// zero or shorter than that action. Different rows keep their configured cancellation behavior; normal attacks
    /// keep their separate swing/jitter rule.
    /// </summary>
    private bool RepeatsActiveSkill(ActiveSkillCast active, JsonElement row, CastSkillReq req) =>
        req.SkillId == active.Request.SkillId && !new SkillConfig(row).IsNormalAttack(_data);

    /// <summary>
    /// A normal attack (_bCancelCast 1) may cut a skill's action short, but not the swing before it at the same target:
    /// otherwise a burst of clicks restarts the swing each time and hits at click speed (issue #121). The client paces swings
    /// at the same ASPD-scaled action time (the normal attack shows' m_fInterruptTime is their whole length) from its own
    /// send, so network jitter can bring a legitimate next swing a little early: <see cref="SwingJitterMs"/> of slack (inferred).
    /// </summary>
    private bool SwingInFlight(ActiveSkillCast active, JsonElement row, CastSkillReq req) =>
        req.TargetId != 0 && req.TargetId == active.Request.TargetId && SwingTurnLeft(active, row) > 0;

    /// <summary>
    /// Turning to another target during a swing is the client's own business (it plays the swing out), so the turn is taken,
    /// but the new swing starts when the one in flight would have ended: the milliseconds left, 0 when it is no such turn.
    /// Starting it at once let clicks alternating between two targets swing at click speed (inferred from the same pacing).
    /// </summary>
    private int SwingTurnLeft(ActiveSkillCast? active, JsonElement row)
    {
        if (active is not { Cancelled: false } || !new SkillConfig(row).IsNormalAttack(_data) || !new SkillConfig(active.Row).IsNormalAttack(_data))
            return 0;
        long left = active.StartMs + active.ChantMs + active.ActionMs - SwingJitterMs - Now;
        return left > 0 ? (int)Math.Min(left, int.MaxValue) : 0;
    }

    internal const int SwingJitterMs = 150;

    private bool Learned(GameSession s, JsonElement row)
    {
        if (new SkillConfig(row).Id == PublicSkill("WorldTreeSkillId")) return true; // paid item skill, LC_ReviveHelper.
        // A Survivor Battle career owns the match skill set (TimedBattleService.Chicken), not the job tree.
        if (MatchSkillRule?.Invoke(s, (int)new SkillConfig(row).Id) is { } granted) return granted;
        if (_skills.IsCareerNormalAttack(s.Progress!.Career, (int)new SkillConfig(row).Id))
            return new SkillConfig(row).Id == _skills.NormalAttack(s);
        return _skills.Level(s.Progress!, (int)new SkillConfig(row).SkillId) >= new SkillConfig(row).Level;
    }

    private MonsterUnit? FindMonster(long sceneId, long uid) => _world.Map(sceneId).Units.OfType<MonsterUnit>().FirstOrDefault(m => m.Uid == uid && !m.Dead);
    private GameSession? FindPlayer(GameSession s, long uid, bool dead = false) => _world.PlayersIn(s.WorldScene).FirstOrDefault(p => p.Role?.Id == uid && p.Dead == dead);
    private long PublicSkill(string key) => _data.Table("PublicParamConfig").TryGetValue(key, out var value) ? (long)(GameData.Number(value) ?? 0) : 0;
    private static bool Finite(Vec3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    private static float Distance(Vec3 a, Vec3 b) => MonsterService.Distance(a.X, a.Z, b.X, b.Z);
    private static float Range(JsonElement row) => Math.Max(0, new SkillConfig(row).DistanceMax / 100f);
    private static string Script(JsonElement row) => row.TryGetProperty("_kScriptLogic", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static double[] Numbers(JsonElement row, string key) => row.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array
        ? [.. list.EnumerateArray().Select(v => GameData.Number(v) ?? 0)] : [];
    private static IEnumerable<JsonElement> Tuples(JsonElement row, string key) => row.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array
        ? list.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Array) : [];

    private bool Relation(JsonElement row, bool friendly, bool self = false, int race = 0, bool dead = false)
    {
        var template = _data.SkillTemplate(new SkillConfig(row).ScriptShow);
        if (template is not { } t || !t.TryGetProperty("m_kSelectInfo", out var selectors) || selectors.ValueKind != JsonValueKind.Array)
            return friendly == (new SkillConfig(row).DmgType == 3); // (inferred) typed recovery fallback for rows without a visual selector.
        foreach (var select in selectors.EnumerateArray())
        {
            int relation = (int)GameData.Int(select, "m_kSelectType"), subtype = (int)GameData.Int(select, "m_kSelectSubType");
            if ((relation & (dead ? friendly ? 32 : 16 : friendly ? 2 : 1)) == 0 && (relation & (dead ? 64 : 4)) == 0) continue;
            if (subtype == 2 && !self || subtype == 16 && self) continue;
            long raceMask = GameData.Int(select, "m_kSelectRaceType");
            if (!friendly && raceMask > 0 && (race <= 0 || (raceMask & (1L << (race - 1))) == 0)) continue;
            return true;
        }
        return false;
    }

    // The client casts on reaching range while it still walks; its stop arrives just after the cast (in-game 11.10:
    // 13 ms later, refused 3024 "invalid skill distance" on most approaches). The cast carries no caster position, so
    // a walking caster's range allows the distance it can have covered since its last walk. The client sends a walk
    // only when its direction changes (in-game 22.10: straight legs of 0.86 s and 0.9 s), so the time is not capped
    // below the 5 s SceneService.AcceptWalkAsync allows.
    internal const double MaxWalkSlackSeconds = 5;
    private static double WalkedSeconds(DateTime since) => Math.Clamp((DateTime.UtcNow - since).TotalSeconds, 0, MaxWalkSlackSeconds);
    private static float WalkSlack(GameSession s) => !s.Walking ? 0 : (float)(s.Speed / 100.0 * WalkedSeconds(s.LastWalkUtc));

    /// <summary>
    /// Where along the walk in progress the caster can be nearest to the target: the walk goes straight from the last walk's
    /// position in its direction (WalkReq.dir, or the facing), <paramref name="travelled"/> metres at most. The slack is no
    /// circle: a forged "walking" report does not reach behind or beside the caster. <see cref="LegJitter"/> metres of
    /// sideways tolerance (inferred) cover a direction the client rounds.
    /// </summary>
    private static Vec3 WalkPoint(GameSession s, Vec3 origin, Vec3 target, float travelled)
    {
        var best = origin;
        float bestDistance = Distance(origin, target);
        foreach (float degrees in new[] { s.WalkDir, s.Face })
        {
            double angle = degrees * Math.PI / 180;
            float ux = (float)Math.Sin(angle), uz = (float)Math.Cos(angle);
            float along = Math.Clamp((target.X - origin.X) * ux + (target.Z - origin.Z) * uz, 0, travelled);
            var point = new Vec3 { X = origin.X + ux * along, Y = origin.Y, Z = origin.Z + uz * along };
            if (Distance(point, target) < bestDistance) (best, bestDistance) = (point, Distance(point, target));
        }
        return best;
    }

    internal const float LegJitter = 1f;

    /// <param name="askedRange">The range of the row the client asked for: it walks into that range, not into the
    /// range of a mode row a talent or a mount selects (Arrow Smash 18 m, its wolf row 5 m; issue #62).</param>
    private int ValidateTarget(GameSession s, JsonElement row, CastSkillReq req, Vec3 origin, float slack = 0, float askedRange = 0)
    {
        int release = (int)new SkillConfig(row).Release;
        var monster = FindMonster(s.WorldScene, req.TargetId);
        bool revive = IsResurrectionRow(row);
        var player = req.TargetId == s.Role!.Id ? s : FindPlayer(s, req.TargetId, revive);
        var enemyPlayer = EventTarget(s, req.TargetId);
        Vec3 targetPosition;
        if (release == 1)
        {
            if (monster != null && CanHitMonster(s, monster) && Relation(row, false, race: monster.Template.Race)) targetPosition = monster.Pos;
            else if (!revive && enemyPlayer != null) targetPosition = enemyPlayer.Position;
            else if (player != null && player.Dead == revive && Relation(row, true, player == s, dead: revive)
                && (!revive || !_buffs.HasForbidden(player, ForbiddenState.Revive))) targetPosition = player.Position;
            else if (req.TargetId == 0 && Relation(row, true, true)) { req.TargetId = s.Role.Id; targetPosition = s.Position; }
            else return ErrorCodes.SkillTargetInvalid;
            if (req.TargetPos is null || !PreserveTargetAim.Any(preserve => preserve(s, req)))
                req.TargetPos = targetPosition.Clone();
            if (revive)
            {
                if (player == s) return ErrorCodes.SkillTargetInvalid;
                if (req.SkillId == PublicSkill("WorldTreeSkillId") ? ReviveItemSkill == null : ReviveOffer == null)
                    return ErrorCodes.SkillReviveNotAllowed;
                if (ReviveCheck?.Invoke(player!) is { } reviveError && reviveError != 0) return reviveError;
            }
        }
        else if (release is 7 or 8)
        {
            if (req.TargetPos == null) return ErrorCodes.SkillTargetInvalid;
            targetPosition = req.TargetPos;
        }
        else
        {
            // Released around the caster (0, 2-6): the client's point is only shown, but procs read it as the centre; one
            // beyond the skill's reach and area is not a point it could aim at (inferred), so the caster's is used.
            float aim = Math.Max(Math.Max(Range(row), askedRange), new SkillConfig(row).Range.FirstOrDefault(0) / 100f) + 1 + slack;
            if (req.TargetPos == null || Distance(origin, req.TargetPos) > aim) req.TargetPos = origin.Clone();
            return 0;
        }
        if (!_buffs.CanSeePosition(s, targetPosition)) return ErrorCodes.SkillTargetInvalid;
        float d = Distance(origin, targetPosition);
        float reach = slack > 0 ? Distance(WalkPoint(s, origin, targetPosition, slack), targetPosition) - LegJitter : d;
        if (d < new SkillConfig(row).DistanceMin / 100f - slack || reach > Math.Max(Range(row), askedRange) + 0.5f)
            return ErrorCodes.SkillDistance;
        float height = new SkillConfig(row).HeightRange / 100f;
        if (height > 0 && MathF.Abs(origin.Y - targetPosition.Y) > height) return ErrorCodes.SkillHeight;
        int passLevel = (int)new SkillConfig(row).PassBlock;
        if (passLevel >= 0 && _worldData.Map(s.SceneEntry) is { Nav: not null } map
            && !map.CanSkillPass(origin.X, origin.Y, origin.Z, targetPosition.X, targetPosition.Y,
                targetPosition.Z, passLevel)
            && (release != 1 || monster == null
                || BlockedMonsterEndpointAllowed?.Invoke(s, monster, origin, targetPosition) != true))
            return ErrorCodes.SkillBlocked;
        // 上游城戰靜態牆的推定規則保留原 pass level 2 範圍，動態牆仍逐技能等級驗證。
        if (passLevel == 2 && SkillLineBlocked?.Invoke(s, release == 1 ? monster : null, origin, targetPosition) == true)
            return ErrorCodes.SkillBlocked;
        return 0;
    }

    /// <summary>The scene's item limit (<see cref="ItemMapLimit"/>), in a dungeon through its DungeonConfig row.</summary>
    private bool ItemAllowedInMap(GameSession s, int entry) =>
        _data.Row("ItemConfig", entry) is { } item && ItemMapLimit.Allows(_data, s.SceneEntry, item);

    private sealed class CastCosts
    {
        public Dictionary<int, long> Attrs { get; } = [];
        public List<(int Entry, long Count)> Items { get; } = [];
        public Dictionary<int, long> Buffs { get; } = [];
        public Dictionary<int, long> Energy { get; } = [];
        public bool Invalid { get; set; }
    }

    private CastCosts ResolveCosts(GameSession s, JsonElement row)
    {
        var result = new CastCosts();
        var stats = _character.Stats(s);
        foreach (var tuple in Tuples(row, "_kCost"))
        {
            var c = tuple.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray();
            // (inferred) retained opaque type-4 markers follow the live client checker, which skips them.
            // Lua_GameDefine.enum_SkillCostType / LC_Skill_Function.CheckSkillCost only consume 1/2/3/6/7/8.
            if (c.Length == 2 && c[0] == 4) continue;
            if (c.Length < 3) { result.Invalid = true; continue; }
            int key = (int)c[1];
            long amount = c.Length > 3 ? c[3] : c[2];
            switch (c[0])
            {
                case 1 when c.Length >= 4 && key is 2000 or 2001:
                    amount += stats[key == 2000 ? 101 : 102] * c[2] / 10000;
                    result.Attrs[key] = result.Attrs.GetValueOrDefault(key) + Math.Max(0, amount);
                    break;
                case 7 when c.Length >= 4 && key is 2000 or 2001:
                    // AttrConvert [7, resource, basis-property, ratio], LC_Skill_Function cost checker.
                    result.Attrs[key] = result.Attrs.GetValueOrDefault(key) + Math.Max(0, stats[(int)c[2]] * c[3] / 10000);
                    break;
                case 2: result.Items.Add((key, Math.Max(0, c[2]))); break;
                case 3: result.Buffs[key] = result.Buffs.GetValueOrDefault(key) + Math.Max(0, c[2]); break;
                case 8: result.Energy[key] = result.Energy.GetValueOrDefault(key) + Math.Max(0, amount); break;
                default: result.Invalid = true; break; // reject an unsupported cost rather than grant a free cast.
            }
        }
        if (result.Attrs.TryGetValue(AttributeId.Sp, out long sp)) result.Attrs[AttributeId.Sp] = ModifiedSpCost(_buffs.GuardianAttributes(s), sp);
        return result;
    }

    private (int Error, long Parameter) ValidateCosts(GameSession s, CastCosts costs)
    {
        if (costs.Invalid) return (ErrorCodes.SkillRequirements, 0);
        var stats = _character.Stats(s);
        foreach (var (attr, cost) in costs.Attrs)
            if (cost > stats[attr] || attr == 2000 && cost >= stats[attr]) return (ErrorCodes.SkillCostShort, attr);
        foreach (var (group, count) in costs.Buffs)
            if (!_buffs.HasBuffPiles(s, group, (int)count))
                return (ErrorCodes.SkillBuffCostShort, group);
        return (0, 0);
    }

    private async Task PayCostsAsync(GameSession s, CastCosts costs)
    {
        if (costs.Attrs.Count > 0)
        {
            var stats = _character.Stats(s);
            await _character.SetHpSpAsync(s, stats[2000] - costs.Attrs.GetValueOrDefault(2000), stats[2001] - costs.Attrs.GetValueOrDefault(2001));
        }
        foreach (var (group, count) in costs.Buffs)
            await _buffs.ConsumeBuffPilesAsync(s, group, (int)count);
    }

    /// <summary>Ends a skill group's running cooldowns now, each under the mode it was shown (a talent's reset).</summary>
    internal static async Task ResetCooldownAsync(GameSession s, int group)
    {
        s.SkillPublicCds.Remove(group); // 完整重置（GM／既有天賦）同步清除公共下限；卡片另用 ResetOwnCooldownAsync。
        long now = Now;
        var notice = new SkillCdNotify { Uuid = s.Role!.Id };
        foreach (var (key, end) in s.SkillModeCds.Where(c => c.Key.Group == group && c.Value > now).ToArray())
        {
            s.SkillModeCds.Remove(key);
            notice.Infos.Add(new SkillCdInfo { SkillId = group, SkillMode = key.Mode, CdStartTimestampMs = now, CdEndTimestampMs = now });
        }
        if (s.SkillCds.Remove(group) && notice.Infos.Count == 0)
            notice.Infos.Add(new SkillCdInfo { SkillId = group, CdStartTimestampMs = now, CdEndTimestampMs = now });
        if (notice.Infos.Count > 0) await s.SendGameAsync(SkillCdUpdate, notice);
    }

    private async Task StartCooldownAsync(GameSession s, JsonElement row, int? askedMode = null)
    {
        if ((s.GmCombat & GmCombatSwitches.NoCooldown) != 0) return; // GmNoCD / GmUnlimit 1: no skill cooldown starts
        int group = (int)new SkillConfig(row).SkillId, mode = (int)new SkillConfig(row).Mode;
        long now = Now, cd = new SkillConfig(row).Cd;
        var notice = new SkillCdNotify { Uuid = s.Role!.Id };
        var shown = new Dictionary<(int Group, int Mode), SkillCdInfo>();
        void Set(int g, int m, long duration, bool shared, int shownMode)
        {
            if (duration <= 0) return;
            long end = now + duration;
            s.SkillCooldownShownModes[(g, m)] = shownMode;
            s.SkillModeCds[(g, m)] = Math.Max(end, s.SkillModeCds.GetValueOrDefault((g, m)));
            if (shared) s.SkillCds[g] = Math.Max(end, s.SkillCds.GetValueOrDefault(g));
            end = Math.Max(s.SkillModeCds[(g, m)], s.SkillCds.GetValueOrDefault(g));
            if (shown.TryGetValue((g, shownMode), out var prior))
                prior.CdEndTimestampMs = Math.Max(prior.CdEndTimestampMs, end);
            else shown[(g, shownMode)] = new SkillCdInfo
            {
                SkillId = g, SkillMode = shownMode, CdStartTimestampMs = now, CdEndTimestampMs = end,
            };
        }
        // The client files a 31113 under group * 10000 + its mode (LC_Skill_Function.GetSkillCDInfoKey) and its bar reads
        // the key of the row it keeps, the row it asks to cast. A mode a talent or a mount selects (type 15: Joint Beat,
        // Spear Throw, Arrow Smash on the wolf) casts another row, and a cooldown sent under that row's mode never showed
        // (issue #62): it goes under the asked row's mode. Without one, a mode row's _iCdModeGroup (0: mode 0's) decides.
        int cdModeGroup = (int)new SkillConfig(row).CdModeGroup;
        Set(group, mode, cd, cdModeGroup < 0, askedMode ?? (cdModeGroup >= 0 ? cdModeGroup : mode));
        var shared = Numbers(row, "_iPubCD");
        var groups = Numbers(row, "_iPubCDGroup");
        // Parallel arrays, e.g. BackSlide [15104,15330,15329] / [1000,15000,0] (inferred server application).
        for (int i = 0; i < Math.Min(shared.Length, groups.Length); i++)
        {
            int sibling = (int)groups[i];
            if (_data.Row("SkillConfig", sibling) is { } siblingRow) sibling = (int)new SkillConfig(siblingRow).SkillId;
            if (sibling > 0)
            {
                s.SkillPublicCds[sibling] = Math.Max(s.SkillPublicCds.GetValueOrDefault(sibling), now + (long)shared[i]);
                Set(sibling, 0, (long)shared[i], true, 0);
            }
        }
        // LC_Scene_Sync overwrites a cache entry for every 31113 row. A short public cooldown must not replace
        // this cast's longer own cooldown (BackSlide), or an already running group/mode cooldown, in that cache.
        foreach (var info in shown.Values)
        {
            info.CdEndTimestampMs = Math.Max(info.CdEndTimestampMs, s.SkillCds.GetValueOrDefault((int)info.SkillId));
            notice.Infos.Add(info);
        }
        if (notice.Infos.Count > 0) await s.SendGameAsync(SkillCdUpdate, notice);
    }

    /// <summary>Legacy auto-attack entry point, using the same cancellation-safe cast owner as manual skills.</summary>
    private async Task SwingAsync(GameSession s, MonsterUnit? target, int skillId, long id)
    {
        if (s.Role == null || s.Progress == null) return;
        using var held = await CharacterService.Lock(s);
        if (target != null && !CanHitMonster(s, target)) return;
        if (EffectiveSkillRow(s, skillId) is not { } row || !Learned(s, row) || !SkillModeAllowed(s, row)
            || _buffs.HasForbidden(s, ForbiddenState.NormalAttack) || s.SkillCast is { Cancelled: false }) return;
        // 每次續擊仍須符合原 _iSkillCondition；已知條件失效後停止舊迴圈，不等待重新上騎而復活。
        if (!KnownSkillConditionsMet(s, row, null, true)) { s.Attack = null; return; }
        var request = new CastSkillReq
        {
            SkillId = (int)new SkillConfig(row).Id, TargetId = target?.Uid ?? 0,
            TargetPos = target?.Pos ?? s.Position.Clone(), Face = s.Face
        };
        if (!await AdmitAutomaticCostAsync(s, row, request, id)) { s.Attack = null; return; }
        await StartCastAsync(s, row, request, id);
    }

    private async Task<bool> AdmitAutomaticCostAsync(GameSession s, JsonElement row, CastSkillReq request, long id)
    {
        var costs = ResolveCosts(s, row);
        if (costs.Items.Count == 0) return true;
        if (ValidateCosts(s, costs).Error != 0 || !_buffs.CanSpendEnergy(s, row)) return false;
        var lease = s.Attachment;
        long scene = s.SceneGeneration, worldScene = s.WorldScene;
        bool Current() => lease?.Cancelled != true && ReferenceEquals(s.Attachment, lease) &&
            s.SceneGeneration == scene && s.WorldScene == worldScene && !s.SceneLoading && !s.Dead;
        if (!await AdmitCastCostAsync(s, row, request, costs, id) || !Current()) return false;
        await PayCostsAsync(s, costs);
        await _buffs.SpendEnergyAsync(s, row);
        return Current();
    }

    /// <param name="leadMs">How long the cast waits before it starts (<see cref="BeforeCast"/>): its timeline is sent and its
    /// impact lands that much later, and an interrupt in between cancels it like any cast.</param>
    private async Task StartCastAsync(GameSession s, JsonElement row, CastSkillReq req, long id, SkillObject? actor = null, int leadMs = 0)
    {
        var skill = new SkillConfig(row);
        var origin = actor?.Pos ?? s.Position.Clone();
        var target = FindMonster(s.WorldScene, req.TargetId);
        var aim = PreserveTargetAim.Any(preserve => preserve(s, req)) ? req.TargetPos : target?.Pos;
        float face = aim != null ? MonsterService.Angle(origin.X, origin.Z, aim.X, aim.Z) : req.Face;
        var action = new ActiveSkillCast
        {
            Id = id, OwnerId = s.Role!.Id, WorldScene = s.WorldScene, SceneGeneration = s.SceneGeneration,
            Attachment = s.Attachment, Request = req, Row = row, Origin = origin, StartedWalking = actor == null && s.Walking, WalkFromUtc = s.LastWalkUtc,
            StartMs = Now + leadMs, ChantMs = ChantMs(s, row), ActionMs = SkillMs(s, row), Operator = actor,
        };
        action.PositionOnly = PositionOnlyCasts.Any(rule => rule(s, action));
        bool highLevel = skill.SkillEffectLevel > 0 || HighLevelCasts.Any(rule => rule(s, action));
        s.SkillCast = action;
        // A caster that may not walk while casting (_bMovement 0) saves up no walking distance until the cast ends: a client
        // that walks on without reporting it cannot claim the distance afterwards (SceneService.AcceptWalkAsync).
        if (actor == null && new SkillConfig(row).Movement == 0)
            SceneService.HoldWalk(s, DateTime.UtcNow.AddMilliseconds(leadMs + action.ChantMs + action.ActionMs), s.Walking);
        s.WorldState.ResurrectionCastId = IsResurrectionRow(row) ? id : 0;
        s.Face = face;
        req.Face = face;
        var projectile = ProjectileCasts.Select(rule => rule(s, action)).FirstOrDefault(value => value.HasValue);
        async Task Timeline()
        {
            await SendCastTimelineAsync(s, new UseInSkillInfo
            {
                SrcID = action.SourceId, SkillID = req.SkillId, TargetId = projectile is { PositionTarget: true } ? 0 : req.TargetId,
                TargetPos = req.TargetPos ?? origin,
                Face = face, SessionId = id, ChantMs = action.ChantMs, SkillMs = action.ActionMs, StartTime = action.StartMs, PlayAct = true,
            }, highLevel: highLevel, extra: actor != null || req.IsExtraSkill);
            TraceImpact(action, "timeline-sent");
        }
        if (leadMs <= 0) await Timeline();
        else ScheduleCastDeadline(action, 0, () => Live(s, action) ? Timeline() : Task.CompletedTask);
        await PrepareVolleyArrowsAsync(s, action);
        var template = _data.SkillTemplate(skill.ScriptShow);
        var points = template is { } t ? Numbers(t, "m_kAtkPoints") : [];
        double point = points.FirstOrDefault(0.2);
        int at = IsResurrectionRow(row) ? action.ChantMs : ImpactOffset(template, point, action.ChantMs, action.ActionMs);
        int bulletEffect = projectile?.EffectId ?? (actor == null && req.TargetId != 0 && !HasClientBulletPoints(template)
            ? BulletEffect(skill.ScriptShow) : 0);
        var bulletTarget = req.TargetPos ?? target?.Pos ?? origin;
        int bulletTravel = bulletEffect > 0 ? BulletTravelMs(bulletEffect, origin, bulletTarget,
            fixedPosition: projectile is { PositionTarget: true }) : 0;
        if (bulletTravel > 0)
        {
            action.HasTimedBullet = true;
            action.IndependentProjectile = projectile is { PositionTarget: true };
            ScheduleCastDeadline(action, at, async () =>
            {
                if (!Live(s, action)) return;
                if (projectile is { CastEffectId: > 0 } authored)
                    await SendSkillEffectAsync(s, new SkillEffectDisplayNotify
                    {
                        SourceId = action.OwnerId, MasterId = action.OwnerId, SkillId = req.SkillId,
                        EffectId = authored.CastEffectId, EffectType = 3,
                    });
                await SendSkillBulletAsync(s, new SkillFixTimeBulletNotify
                {
                    SourceId = action.OwnerId, TargetId = projectile is { PositionTarget: true } ? 0 : req.TargetId, EffectId = bulletEffect,
                    FixedTime = bulletTravel, Split = PresentationSplit(row), TargetPosition = bulletTarget.Clone(),
                    SkillConfigId = req.SkillId, MasterId = action.OwnerId, SourcePosition = action.Origin.Clone(),
                    // LC_Bullet_Helper subtracts elapsed server time from the authored projectile duration. Preserve
                    // the scheduled onset rather than hiding a late scheduler callback behind its execution time.
                    StartTimestampMs = action.StartMs + at,
                }, highLevel: highLevel);
                TraceImpact(action, "bullet-sent");
            });
        }
        int resolveAt = at + bulletTravel;
        if (!TryScheduleAuthoredStages(s, action, template, () => Live(s, action), () =>
        {
            if (Now >= action.StartMs + action.ChantMs + action.ActionMs && ReferenceEquals(s.SkillCast, action))
                s.SkillCast = null;
            return Task.CompletedTask;
        }, travelMs: bulletTravel)) ScheduleCastDeadline(action, resolveAt, async () =>
        {
            if (!Live(s, action)) return;
            action.Resolved = true;
            TraceImpact(action, "resolve-entered");
            await ResolveCastAsync(s, action);
        });
        int releaseAt = action.IndependentProjectile ? action.ChantMs + action.ActionMs
            : Math.Max(resolveAt, action.ChantMs + action.ActionMs);
        ScheduleCastDeadline(action, releaseAt + 1, () =>
        {
            if (ReferenceEquals(s.SkillCast, action) && (action.AuthoredStageCount == 0 || action.AuthoredStagesFinished)) s.SkillCast = null;
            return Task.CompletedTask;
        });
    }

    private static bool HasClientBulletPoints(JsonElement? template)
        => template is { } value && new SkillTimelineData(value).HasClientPoints;

    /// <summary>Piecewise template time mapping; same scale as LC_Skill_RunTimeContext (inferred for attack points).</summary>
    internal static int ImpactOffset(JsonElement? template, double point, int chantMs, int actionMs)
    {
        double chant = 0, end = 1;
        if (template is { } t && t.TryGetProperty("m_kState", out var state))
        {
            var times = Numbers(state, "Time");
            if (times.Length >= 3) { chant = times[1]; end = times[2]; }
        }
        double ms = point < chant && chant > 0 ? point / chant * chantMs : chantMs + Math.Clamp((point - chant) / Math.Max(0.001, end - chant), 0, 1) * actionMs;
        return (int)Math.Max(0, Math.Round(ms));
    }

    private static bool ReleasedProjectile(ActiveSkillCast action) => action.IndependentProjectile
        && Now >= action.StartMs + action.ChantMs + action.ActionMs;

    private static bool Live(GameSession s, ActiveSkillCast action) => CurrentCastMap(s, action)
        && (ReferenceEquals(s.SkillCast, action) || ReleasedProjectile(action))
        && (action.Operator == null || !action.Operator.Removing && action.Operator.ExpiresMs > Now);

    private int SkillMs(GameSession s, JsonElement row)
    {
        double aspd = Math.Clamp(_character.Stats(s)[115] / 100.0, 0.1, 5);
        double influence = new SkillConfig(row).ActionTimeInfluence / 100.0;
        return (int)Math.Round(Math.Max(1, new SkillConfig(row).ActionTime) / Math.Max(0.1, 1 + (aspd - 1) * influence));
    }

    public async Task InterruptAsync(GameSession s, SkillInterruptReq? req = null)
    {
        // 31124 with a normal attack as nxtSkill during a swing: at the same target it is the click burst the cast rule refuses
        // (SwingInFlight), at another the turn CastCoreAsync paces (SwingTurnLeft), which needs the swing still in flight.
        if (req?.NxtSkill is { } nextSwing && s.SkillCast is { Cancelled: false } swinging
            && EffectiveSkillRow(s, nextSwing.SkillId) is { } nextRow && SwingTurnLeft(swinging, nextRow) > 0)
        {
            bool same = SwingInFlight(swinging, nextRow, nextSwing);
            await s.SendGameAsync(SkillInterrupt, new SkillInterruptResp { IsInterruptSucc = !same });
            if (same) await RefuseCastAsync(s, nextSwing, ErrorCodes.SkillBusy, 0);
            else await CastAsync(s, nextSwing);
            return;
        }
        bool valid = req == null || req.InterruptSkillId == 0 || req.InterruptSkillId == s.SkillCast?.Request.SkillId
            || req.InterruptSkillId == s.Attack?.SkillId || _eventAttacks.TryGetValue(s, out var eventAttack) && req.InterruptSkillId == eventAttack.SkillId;
        bool interrupted = valid && (s.SkillCast != null || s.Attack != null || _eventAttacks.ContainsKey(s));
        if (interrupted)
        {
            s.Attack = null;
            _eventAttacks.Remove(s);
            await CancelCastAsync(s, req?.NxtSkill != null ? SkillInterruptedInfo.Types.SkillInterruptType.Skill : SkillInterruptedInfo.Types.SkillInterruptType.Move);
        }
        await s.SendGameAsync(SkillInterrupt, new SkillInterruptResp { IsInterruptSucc = interrupted });
        if (interrupted && req?.NxtSkill is { } next) await CastAsync(s, next);
    }

    private async Task CancelCastAsync(GameSession s, SkillInterruptedInfo.Types.SkillInterruptType cause)
    {
        s.WorldState.ResurrectionCastId = 0;
        if (s.SkillCast is not { } action) return;
        if (ReleasedProjectile(action)) { s.SkillCast = null; return; }
        action.Cancelled = true;
        s.SkillCast = null;
        s.WalkAllowance.Release(DateTime.UtcNow);
        await ToPlayerAndViewersAsync(s, SkillEnd, new SkillEndNotify
        {
            SrcID = action.SourceId, SessionId = action.Id, Type = SkillEndNotify.Types.SkillEndType.Interrupted,
            ClearEffect = true, InterruptedInfo = new SkillInterruptedInfo { Type = cause },
        });
    }

    public void PlayerMoved(GameSession s)
    {
        if (_eventAttacks.TryGetValue(s, out var eventAttack)
            && MonsterService.Distance(eventAttack.StartX, eventAttack.StartZ, s.Position.X, s.Position.Z) > 1.5f) _eventAttacks.Remove(s);
        if (s.Attack is { } a && MonsterService.Distance(a.StartX, a.StartZ, s.Position.X, s.Position.Z) > 0.1f)
        {
            // Clicking an enemy from afar: the client attacks on reaching range and stops a few ms later (in-game
            // 11.10), so that first stop is where the auto-attack starts, not a step away that ends it. The 300 ms grace is inferred.
            if (a.StartedWalking && !s.Walking && Now - a.StartMs <= 300
                && MonsterService.Distance(a.StartX, a.StartZ, s.Position.X, s.Position.Z) <= a.ArrivalSpeed * WalkedSeconds(a.WalkFromUtc) + 0.5)
            {
                a.StartX = s.Position.X; a.StartZ = s.Position.Z;
                a.StartedWalking = false;
            }
            else s.Attack = null;
        }
        if (s.SkillCast is not { } cast || new SkillConfig(cast.Row).Movement != 0) return;
        // A walk that sets off (not a stop) after the arrival grace ends a cast that holds the caster in place, even before it
        // has moved: the client interrupts such a cast to walk, and the walk budget is held until the cast ends.
        if (Distance(cast.Origin, s.Position) <= 0.1f && !(s.Walking && Now - cast.StartMs > 300)) return;
        // The client casts on reaching range and stops 1 ms later (in-game 11.10): that first stop is the cast's real
        // origin, within the distance the walk can have covered, not a move that interrupts it.
        if (cast.StartedWalking && !s.Walking && Now - cast.StartMs <= 300
            && Distance(cast.Origin, s.Position) <= s.Speed / 100.0 * WalkedSeconds(cast.WalkFromUtc) + 0.5)
        {
            cast.Origin = s.Position.Clone();
            cast.StartedWalking = false;
            return;
        }
        _loop.TrackRuntimeDetached("cast movement cancellation", () => CancelCastAsync(s, SkillInterruptedInfo.Types.SkillInterruptType.Move));
    }
}
