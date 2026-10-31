using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Server.Game;

internal readonly record struct CastProjectile(int EffectId, bool PositionTarget);

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
    public async Task CastAsync(GameSession s, CastSkillReq request)
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
            else await CastCoreAsync(s, request);
        }
        finally { notices?.Dispose(); s.SkillCastGate.Release(); admission?.Dispose(); }
        if (notices != null) await notices.FlushAsync();
    }

    /// <summary>A pet wrapper or its child effect: the guardian path validates and runs it (31102).</summary>
    private bool IsGuardianRequest(CastSkillReq request) => _data.Row("SkillConfig", request.SkillId) is { } row
        && GameData.Int(row, "_iSystemType") is GuardianPetRules.WrapperType or GuardianPetRules.EffectType;

    private async Task CastCoreAsync(GameSession s, CastSkillReq request)
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
            req.SkillId = (int)GameData.Int(row!.Value, "_iID");
            own = owned == null ? OwnSkillAdmit?.Invoke(s, req.SkillId) : null;
            if (own is { } ownError && ownError != 0) error = ownError;
            else if (own == null && !granted
                && (owned != null ? !owned.Skills.Contains(req.SkillId)
                    : !Learned(s, row!.Value) && s.GmCastSkill != request.SkillId
                        && !GrantedSkill.Exists(grant => grant(s, req.SkillId))))
                error = ErrorCodes.SkillNotLearned;
            else if (!SkillModeAllowed(s, row!.Value)) error = ErrorCodes.SkillRequirements;
            else if (GameData.Int(row!.Value, "_iBasicType") != 1) error = ErrorCodes.SkillPassive;
            else if (_buffs.HasForbidden(s, new SkillConfig(row.Value).IsNormalAttack(_data)
                ? ForbiddenState.NormalAttack : ForbiddenState.CastSkill)) error = ErrorCodes.SkillBadState;
            else if (req.SkillId == PublicSkill("WorldTreeSkillId") && _buffs.HasForbidden(s, ForbiddenState.UseItem)) error = ErrorCodes.ItemUseForbidden;
            else if (req.SkillId == PublicSkill("WorldTreeSkillId") && !ItemAllowedInMap(s, (int)PublicSkill("WorldTreeItemId"))) error = ErrorCodes.ItemUseAreaForbidden;
            else if (req.IsExtraSkill && owned == null && GameData.Int(row.Value, "_iExtraType") == 0) error = ErrorCodes.SkillRequirements;
        }
        var origin = owned?.Pos ?? s.Position;
        if (error == 0)
        {
            int group = (int)GameData.Int(row!.Value, "_iSkillID"), mode = (int)GameData.Int(row.Value, "_iMode");
            if (own == null && Math.Max(s.SkillCds.GetValueOrDefault(group), s.SkillModeCds.GetValueOrDefault((group, mode))) > Now) error = ErrorCodes.SkillCooldown;
            // Preserve the established cooldown refusal for a repeated cast, including during its action tail.
            else if (s.SkillCast is { Cancelled: false } active && Now < active.StartMs + active.ChantMs + active.ActionMs
                && (!new SkillConfig(row.Value).CancelCast || RepeatsActiveSkill(active, row.Value, req)
                    || SwingInFlight(active, row.Value, req))) error = ErrorCodes.SkillBusy;
            else
            {
                error = ValidateTarget(s, row.Value, req, origin, owned == null ? WalkSlack(s) : 0,
                    _data.Row("SkillConfig", request.SkillId) is { } askedRow ? Range(askedRow) : 0);
                if (error != 0 && SkillTargetErrorOverrides.Any(allow => allow(s, req, error))) error = 0;
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
            await RefuseCastAsync(s, req, error, errorParam);
            return;
        }

        long id = ++_sessionIds;
        var lease = s.Attachment;
        long scene = s.SceneGeneration, worldScene = s.WorldScene;
        // E05c: the receipt is the paid action's admission. Repeating a protocol request
        // is a new intent and pays again; a lost writer ACK resolves this exact source.
        if (!await AdmitCastCostAsync(s, row!.Value, req, costs, id))
        {
            await RefuseCastAsync(s, req, ErrorCodes.SkillCostShort, costs.Items[0].Entry);
            return;
        }
        if (lease?.Cancelled == true || !ReferenceEquals(s.Attachment, lease) ||
            s.SceneGeneration != scene || s.WorldScene != worldScene || s.SceneLoading) return;
        await CancelCastAsync(s, SkillInterruptedInfo.Types.SkillInterruptType.Skill);
        _eventAttacks.Remove(s);
        await PayCostsAsync(s, costs);
        if (!unlimited) await _buffs.SpendEnergyAsync(s, row!.Value);
        int lead = owned == null && BeforeCast is { } before ? await before(s) : 0;
        await s.SendGameAsync(UseSkill, new CastSkillRsp { SessionId = id, IsAutoCast = req.IsAutoCast });
        var target = FindMonster(s.WorldScene, req.TargetId);
        var playerTarget = EventTarget(s, req.TargetId);
        // _iIfAutoAtk 2 (enum_AutoNormalAttackType.AutoNormalAttack) follows the cast with the career's normal attack
        // (LC_AutoAttackAppendState: GetNormalSkill). Repeating the cast skill itself bypassed its cooldown: Blade
        // Smash and Sonic Slash fired every swing.
        int loopSkill = _skills.IsCurrentNormalAttack(s, req.SkillId) ? req.SkillId : _skills.NormalAttack(s);
        if (GameData.Int(row!.Value, "_iIfAutoAtk") == 2 && owned == null && target != null && CanHitMonster(s, target))
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
        if (GameData.Int(row.Value, "_iIfAutoAtk") == 2 && owned == null && playerTarget != null)
            _eventAttacks[s] = new EventAttack(playerTarget, loopSkill,
                Now + lead + SkillMs(s, row.Value) + ChantMs(s, row.Value) + 1,
                s.Position.X, s.Position.Z);
        if (own != null) await OwnSkillCast!(s, req.SkillId);
        else await StartCooldownAsync(s, row.Value, SkillConfig.Get(_data, request.SkillId) is { } asked ? (int)asked.Mode : null);
        await StartCastAsync(s, row.Value, req, id, owned, lead);
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
    /// otherwise a burst of clicks restarts the swing each time and hits at click speed (issue #121). Turning to another
    /// target (the last one died) is left to the client, which plays the swing out anyway. The client paces swings at the same
    /// ASPD-scaled action time (the normal attack shows' m_fInterruptTime is their whole length) from its own send, so
    /// network jitter can bring a legitimate next swing a little early: <see cref="SwingJitterMs"/> of slack (inferred).
    /// </summary>
    private bool SwingInFlight(ActiveSkillCast active, JsonElement row, CastSkillReq req) =>
        req.TargetId != 0 && req.TargetId == active.Request.TargetId
        && new Data.Config.SkillConfig(row).IsNormalAttack(_data) && new Data.Config.SkillConfig(active.Row).IsNormalAttack(_data)
        && Now < active.StartMs + active.ChantMs + active.ActionMs - SwingJitterMs;

    internal const int SwingJitterMs = 150;

    private bool Learned(GameSession s, JsonElement row)
    {
        if (GameData.Int(row, "_iID") == PublicSkill("WorldTreeSkillId")) return true; // paid item skill, LC_ReviveHelper.
        // A Survivor Battle career owns the match skill set (TimedBattleService.Chicken), not the job tree.
        if (MatchSkillRule?.Invoke(s, (int)GameData.Int(row, "_iID")) is { } granted) return granted;
        if (_skills.IsCareerNormalAttack(s.Progress!.Career, (int)GameData.Int(row, "_iID")))
            return GameData.Int(row, "_iID") == _skills.NormalAttack(s);
        return _skills.Level(s.Progress!, (int)GameData.Int(row, "_iSkillID")) >= GameData.Int(row, "_iLevel");
    }

    private MonsterUnit? FindMonster(long sceneId, long uid) => _world.Map(sceneId).Units.OfType<MonsterUnit>().FirstOrDefault(m => m.Uid == uid && !m.Dead);
    private GameSession? FindPlayer(GameSession s, long uid, bool dead = false) => _world.PlayersIn(s.WorldScene).FirstOrDefault(p => p.Role?.Id == uid && p.Dead == dead);
    private long PublicSkill(string key) => _data.Table("PublicParamConfig").TryGetValue(key, out var value) ? (long)(GameData.Number(value) ?? 0) : 0;
    private static bool Finite(Vec3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    private static float Distance(Vec3 a, Vec3 b) => MonsterService.Distance(a.X, a.Z, b.X, b.Z);
    private static float Range(JsonElement row) => Math.Max(0, GameData.Int(row, "_iDistanceMax") / 100f);
    private static string Script(JsonElement row) => row.TryGetProperty("_kScriptLogic", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static double[] Numbers(JsonElement row, string key) => row.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array
        ? list.EnumerateArray().Select(v => GameData.Number(v) ?? 0).ToArray() : [];
    private static IEnumerable<JsonElement> Tuples(JsonElement row, string key) => row.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array
        ? list.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Array) : [];

    private bool Relation(JsonElement row, bool friendly, bool self = false, int race = 0, bool dead = false)
    {
        var template = _data.SkillTemplate(GameData.Int(row, "_iScriptShow"));
        if (template is not { } t || !t.TryGetProperty("m_kSelectInfo", out var selectors) || selectors.ValueKind != JsonValueKind.Array)
            return friendly == (GameData.Int(row, "_iDmgType") == 3); // (inferred) typed recovery fallback for rows without a visual selector.
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

    /// <param name="askedRange">The range of the row the client asked for: it walks into that range, not into the
    /// range of a mode row a talent or a mount selects (Arrow Smash 18 m, its wolf row 5 m; issue #62).</param>
    private int ValidateTarget(GameSession s, JsonElement row, CastSkillReq req, Vec3 origin, float slack = 0, float askedRange = 0)
    {
        int release = (int)GameData.Int(row, "_iRelease");
        var monster = FindMonster(s.WorldScene, req.TargetId);
        bool revive = Script(row) == "Resurrection";
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
        else { req.TargetPos ??= origin.Clone(); return 0; }
        if (!_buffs.CanSeePosition(s, targetPosition)) return ErrorCodes.SkillTargetInvalid;
        float d = Distance(origin, targetPosition);
        if (d < GameData.Int(row, "_iDistanceMin") / 100f - slack || d > Math.Max(Range(row), askedRange) + 0.5f + slack)
            return ErrorCodes.SkillDistance;
        float height = GameData.Int(row, "_iHeightRange") / 100f;
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

    /// <summary>LC_ItemHelper.ItemCanUseInCurrentMap: MapConfig type/subtype blacklist (1) or whitelist (2).</summary>
    private bool ItemAllowedInMap(GameSession s, int entry)
    {
        if (_data.Row("MapConfig", s.SceneEntry) is not { } map || _data.Row("ItemConfig", entry) is not { } item) return false;
        int mode = (int)GameData.Int(map, "_iLimitType");
        if (mode is not (1 or 2)) return true;
        bool listed = Tuples(map, "_kLimitItem").Any(t => t.GetArrayLength() >= 2 && GameData.Number(t[1]) ==
            GameData.Int(item, GameData.Number(t[0]) == 1 ? "_iType" : "_iSubType"));
        return mode == 1 ? !listed : listed;
    }

    private sealed class CastCosts
    {
        public Dictionary<int, long> Attrs { get; } = new();
        public List<(int Entry, long Count)> Items { get; } = new();
        public Dictionary<int, long> Buffs { get; } = new();
        public Dictionary<int, long> Energy { get; } = new();
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
        int group = (int)GameData.Int(row, "_iSkillID"), mode = (int)GameData.Int(row, "_iMode");
        long now = Now, cd = GameData.Int(row, "_iCD");
        var notice = new SkillCdNotify { Uuid = s.Role!.Id };
        var shown = new Dictionary<(int Group, int Mode), SkillCdInfo>();
        void Set(int g, int m, long duration, bool shared, int shownMode)
        {
            if (duration <= 0) return;
            long end = now + duration;
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
            if (_data.Row("SkillConfig", sibling) is { } siblingRow) sibling = (int)GameData.Int(siblingRow, "_iSkillID");
            if (sibling > 0) Set(sibling, 0, (long)shared[i], true, 0);
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
        var request = new CastSkillReq
        {
            SkillId = (int)GameData.Int(row, "_iID"), TargetId = target?.Uid ?? 0,
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
        float face = target != null ? MonsterService.Angle(origin.X, origin.Z, target.X, target.Z) : req.Face;
        var action = new ActiveSkillCast
        {
            Id = id, OwnerId = s.Role!.Id, WorldScene = s.WorldScene, SceneGeneration = s.SceneGeneration,
            Attachment = s.Attachment, Request = req, Row = row, Origin = origin, StartedWalking = actor == null && s.Walking, WalkFromUtc = s.LastWalkUtc,
            StartMs = Now + leadMs, ChantMs = ChantMs(s, row), ActionMs = SkillMs(s, row), Operator = actor,
        };
        action.PositionOnly = PositionOnlyCasts.Any(rule => rule(s, action));
        bool highLevel = skill.SkillEffectLevel > 0 || HighLevelCasts.Any(rule => rule(s, action));
        s.SkillCast = action;
        s.WorldState.ResurrectionCastId = Script(row) == "Resurrection" ? id : 0;
        s.Face = face;
        req.Face = face;
        Task Timeline() => SendCastTimelineAsync(s, new UseInSkillInfo
        {
            SrcID = action.SourceId, SkillID = req.SkillId, TargetId = req.TargetId, TargetPos = req.TargetPos ?? origin,
            Face = face, SessionId = id, ChantMs = action.ChantMs, SkillMs = action.ActionMs, StartTime = action.StartMs, PlayAct = true,
        }, highLevel: highLevel, extra: actor != null || req.IsExtraSkill);
        if (leadMs <= 0) await Timeline();
        else ScheduleRuntime(action.WorldScene, TimeSpan.FromMilliseconds(leadMs), () => Live(s, action) ? Timeline() : Task.CompletedTask);
        var template = _data.SkillTemplate(skill.ScriptShow);
        var points = template is { } t ? Numbers(t, "m_kAtkPoints") : [];
        double point = points.FirstOrDefault(0.2);
        int at = Script(row) == "Resurrection" ? action.ChantMs : ImpactOffset(template, point, action.ChantMs, action.ActionMs);
        var projectile = ProjectileCasts.Select(rule => rule(s, action)).FirstOrDefault(value => value.HasValue);
        int bulletEffect = projectile?.EffectId ?? (actor == null && req.TargetId != 0 && !HasClientBulletPoints(template)
            ? BulletEffect(skill.ScriptShow) : 0);
        var bulletTarget = req.TargetPos ?? target?.Pos ?? origin;
        int bulletTravel = bulletEffect > 0 ? BulletTravelMs(bulletEffect, origin, bulletTarget,
            fixedPosition: projectile is { PositionTarget: true }) : 0;
        if (bulletTravel > 0)
        {
            action.HasTimedBullet = true;
            action.IndependentProjectile = projectile is { PositionTarget: true };
            ScheduleRuntime(action.WorldScene, TimeSpan.FromMilliseconds(leadMs + at), async () =>
            {
                if (!Live(s, action)) return;
                await SendSkillBulletAsync(s, new SkillFixTimeBulletNotify
                {
                    SourceId = action.OwnerId, TargetId = projectile is { PositionTarget: true } ? 0 : req.TargetId, EffectId = bulletEffect,
                    FixedTime = bulletTravel, Split = PresentationSplit(row), TargetPosition = bulletTarget.Clone(),
                    SkillConfigId = req.SkillId, MasterId = action.OwnerId, SourcePosition = action.Origin.Clone(),
                    // LC_Bullet_Helper subtracts elapsed server time from the authored projectile duration. Preserve
                    // the scheduled onset rather than hiding a late scheduler callback behind its execution time.
                    StartTimestampMs = action.StartMs + at,
                }, highLevel: highLevel);
            });
        }
        int resolveAt = at + bulletTravel;
        ScheduleRuntime(action.WorldScene, TimeSpan.FromMilliseconds(leadMs + resolveAt), async () =>
        {
            if (!Live(s, action)) return;
            action.Resolved = true;
            await ResolveCastAsync(s, action);
        });
        int releaseAt = action.IndependentProjectile ? action.ChantMs + action.ActionMs
            : Math.Max(resolveAt, action.ChantMs + action.ActionMs);
        ScheduleRuntime(action.WorldScene, TimeSpan.FromMilliseconds(leadMs + releaseAt + 1), () =>
        {
            if (ReferenceEquals(s.SkillCast, action)) s.SkillCast = null;
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
        double influence = GameData.Int(row, "_iActionTimeInfluence") / 100.0;
        return (int)Math.Round(Math.Max(1, GameData.Int(row, "_iActionTime")) / Math.Max(0.1, 1 + (aspd - 1) * influence));
    }

    public async Task InterruptAsync(GameSession s, SkillInterruptReq? req = null)
    {
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
        if (s.SkillCast is not { } cast || Distance(cast.Origin, s.Position) <= 0.1f || GameData.Int(cast.Row, "_bMovement") != 0) return;
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
