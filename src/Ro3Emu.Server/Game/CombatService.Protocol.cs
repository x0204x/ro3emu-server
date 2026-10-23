using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>Client presentation and dynamic parameters for actual combat events.</summary>
public sealed partial class CombatService
{
    public const uint SkillSlotModify = 31027;
    public const uint UseInSkillHigh = 31106, ExtraUseInSkillHigh = 31107;
    public const uint SkillBulletHigh = 31108, SkillEffectHigh = 31109;
    public const uint SkillAffectedLegacy = 31118, SkillEffect = 31119, SkillBullet = 31120;
    public const uint ExtraUseInSkill = 31121, SkillSelectionChange = 31122;
    public const uint BossSkillCd = 31125, BossClearSkillCds = 31126, BossAllSkillCds = 31127;
    public const uint DamageStatistics = 31129, SkillAffectedEx = 31132, TrainingOperation = 31133;

    /// <summary>Transient state is scoped to a role/connection; never leaks to another selected role.</summary>
    public sealed class ProtocolState
    {
        public Dictionary<int, int> TemporarySlotRows { get; } = new();
        public string ParameterSignature { get; set; } = "";
        public bool TrainingPaused { get; set; }
        public Dictionary<(long Target, bool White, int Tag), bool> SkillSelection { get; } = new();
    }

    private Dictionary<(int Group, int Level, int Mode), int>? _configuredModeRows;

    private Dictionary<(int Group, int Level, int Mode), int> ConfiguredModeRows =>
        _configuredModeRows ??= _data.Table("SkillConfig").Values
            .Where(r => GameData.Int(r, "_iSkillID") > 0 && GameData.Int(r, "_iLevel") > 0)
            .GroupBy(r => ((int)GameData.Int(r, "_iSkillID"), (int)GameData.Int(r, "_iLevel"), (int)GameData.Int(r, "_iMode")))
            .ToDictionary(g => g.Key, g => (int)GameData.Int(g.First(), "_iID"));

    /// <summary>
    /// The effective configured row for an actual cast. BuffConfig type 15 selects a mode;
    /// a mode can never be selected merely by submitting its row id (inferred server gate).
    /// </summary>
    public JsonElement? EffectiveSkillRow(GameSession session, int requestedRowId)
    {
        if (session.Progress == null || _data.Row("SkillConfig", requestedRowId) is not { } requested) return null;
        int group = (int)GameData.Int(requested, "_iSkillID"), level = (int)GameData.Int(requested, "_iLevel");
        int mode = SkillParameterRules.Mode(group, SkillParameterRules.CategoriesOf(_data, requested), SkillParameterRules.Active(session, _data));
        int requestedMode = (int)GameData.Int(requested, "_iMode");
        if (requestedMode != 0 && requestedMode != mode) return null;
        if (mode != 0 && ConfiguredModeRows.TryGetValue((group, level, mode), out int selectedId)
            && _data.Row("SkillConfig", selectedId) is { } selected)
            return SkillParameterRules.Effective(session, _data, selected);
        if (requestedMode != 0) return null;
        return SkillParameterRules.Effective(session, _data, requested);
    }

    public bool SkillModeAllowed(GameSession session, JsonElement row) =>
        EffectiveSkillRow(session, (int)GameData.Int(row, "_iID")) is { } effective
        && GameData.Int(effective, "_iID") == GameData.Int(row, "_iID");

    /// <summary>SkillConfig._iQueue plus BuffConfig type 44; never trusts a request's queue claim.</summary>
    public bool CanQueueSkill(GameSession session, int group)
    {
        if (session.Progress == null || _skills.Level(session.Progress, group) is not > 0) return false;
        int rowId = _skills.RealId(group, _skills.Level(session.Progress, group));
        if (_data.Row("SkillConfig", rowId) is not { } row) return false;
        long allowed = GameData.Int(row, "_iQueue");
        var tags = SkillParameterRules.CategoriesOf(_data, row);
        foreach (var mod in SkillParameterRules.Active(session, _data))
            if (mod.Type == 44 && SkillParameterRules.Matches(mod, group, tags)) allowed = SkillParameterRules.Apply(allowed, mod);
        return allowed != 0;
    }

    /// <summary>Reconcile actual buff/energy-derived parameters and wheel replacements after login/skill/buff changes.</summary>
    public async Task RefreshSkillParametersAsync(GameSession session)
    {
        if (session.Role == null || session.Progress == null) return;
        var passives = PassiveModifiers(session);
        if (!passives.SequenceEqual(session.PassiveSkillModifiers)) session.PassiveSkillModifiers = passives;
        var mods = SkillParameterRules.Active(session, _data);
        var update = new SkillBeAffectedParamExNotify { IsAll = true, Uuid = session.Role.Id };
        // Scope 0 names its group; a scope-1 tag selector reaches every learned skill whose row carries its tags.
        var groups = new SortedSet<int>(mods.Where(m => m.Scope == 0).Select(m => m.Group));
        if (mods.Any(m => m.Scope == 1))
            foreach (int learned in session.Progress.Skills.Keys)
                if (_skills.Level(session.Progress, learned) is var at and > 0
                    && _data.Row("SkillConfig", _skills.RealId(learned, at)) is { } learnedRow
                    && mods.Any(m => m.Scope == 1 && SkillParameterRules.Matches(m, learned, SkillParameterRules.CategoriesOf(_data, learnedRow))))
                    groups.Add(learned);
        foreach (var group in groups)
        {
            int level = _skills.Level(session.Progress, group);
            if (level <= 0 || _data.Row("SkillConfig", _skills.RealId(group, level)) is not { } baseRow) continue;
            var row = EffectiveSkillRow(session, (int)GameData.Int(baseRow, "_iID")) ?? baseRow;
            var tags = SkillParameterRules.CategoriesOf(_data, baseRow);
            var groupInfo = new BeAffectedParamInfoEx();
            foreach (var type in mods.Where(m => SkillParameterRules.Matches(m, group, tags)).Select(m => m.Type).Distinct().Order())
            {
                if (type == 15) groupInfo.AffectParam[type] = new BeAffectedValueEx { AffectValue = SkillParameterRules.Mode(group, tags, mods) };
                else if (type == 44) groupInfo.AffectParam[type] = new BeAffectedValueEx { AffectValue = CanQueueSkill(session, group) ? 1 : 0 };
                else if (SkillParameterRules.Field(type) is { } field)
                {
                    var value = new BeAffectedValueEx();
                    if (type is 29 or 30)
                    {
                        if (row.TryGetProperty(field, out var array) && array.ValueKind == JsonValueKind.Array)
                            foreach (var v in array.EnumerateArray()) value.AffectValueList.Add((long)(GameData.Number(v) ?? 0));
                    }
                    else value.AffectValue = GameData.Int(row, field);
                    groupInfo.AffectParam[type] = value;
                }
            }
            if (groupInfo.AffectParam.Count > 0) update.AffectParamDict[group] = groupInfo;
        }
        // Empty proto maps may decode as Lua nil. A zero-group entry makes isAll clear the old map safely;
        // LC_DynamicParamMgr.ProcessDynamicSkillParam ignores its absent affectParam.
        if (update.AffectParamDict.Count == 0) update.AffectParamDict[0] = new BeAffectedParamInfoEx();
        string signature = Convert.ToHexString(update.ToByteArray());
        if (session.SkillProtocol.ParameterSignature != signature)
        {
            session.SkillProtocol.ParameterSignature = signature;
            await session.SendGameAsync(SkillAffectedEx, update);
        }
        await RefreshTemporarySlotsAsync(session);
    }

    private async Task RefreshTemporarySlotsAsync(GameSession session)
    {
        var old = session.SkillProtocol.TemporarySlotRows;
        var plan = session.SkillPlans is { } state && state.CurrentPlanIndex >= 0 && state.CurrentPlanIndex < state.SkillAssignPlans.Count
            ? state.SkillAssignPlans[state.CurrentPlanIndex] : null;
        foreach (int slot in session.Progress!.SkillBar.Keys.Concat(old.Keys).Distinct().Order())
        {
            int group = session.Progress.SkillBar.GetValueOrDefault(slot);
            int learned = group > 0 ? _skills.Level(session.Progress, group) : 0;
            int selected = plan != null && slot > 0 && slot <= plan.ManualLoadSkill.Count
                && plan.ManualLoadSkill[slot - 1].SkillId == group ? plan.ManualLoadSkill[slot - 1].SkillLevel : learned;
            int baseId = learned > 0 && selected > 0 && selected <= learned ? _skills.RealId(group, selected) : 0;
            int next = baseId > 0 && EffectiveSkillRow(session, baseId) is { } row ? (int)GameData.Int(row, "_iID") : baseId;
            int current = old.GetValueOrDefault(slot);
            if (next == baseId && current == 0) continue;
            if (current == next) continue;
            if (next == 0)
                await session.SendGameAsync(SkillSlotModify, new SkillSlotModifyNotify { Type = 4, SkillId = current, Slot = slot });
            else
                await session.SendGameAsync(SkillSlotModify, new SkillSlotModifyNotify
                {
                    Type = 1, SourceSkillId = current == 0 ? baseId : current, SkillId = next,
                    Slot = slot, Enable = true,
                });
            if (next == baseId) old.Remove(slot);
            else old[slot] = next;
        }
    }

    /// <summary>One authoritative cast timeline. High-level routes share the same Lua handler, not another cast.</summary>
    private static bool OwnsActiveSkillSource(GameSession owner, long uid) => owner.Role != null
        && owner.SkillObjects.Any(o => o.Uid == uid && o.OwnerSession == owner && o.OwnerId == owner.Role.Id
            && o.WorldScene == owner.WorldScene && o.ExpiresMs > Now && !o.Removing);

    public Task SendCastTimelineAsync(GameSession caster, UseInSkillInfo info, bool highLevel = false, bool extra = false)
    {
        bool ownedSource = extra && OwnsActiveSkillSource(caster, info.SrcID);
        if (caster.Role == null || (info.SrcID != caster.Role.Id && !ownedSource) || info.SessionId <= 0
            || _data.Row("SkillConfig", info.SkillID) == null) return Task.CompletedTask;
        uint route = extra ? highLevel ? ExtraUseInSkillHigh : ExtraUseInSkill : highLevel ? UseInSkillHigh : UseInSkill;
        return ToPlayerAndViewersAsync(caster, route, info);
    }

    public Task SendMonsterTimelineAsync(MonsterUnit monster, UseInSkillInfo info, bool highLevel = false, bool extra = false)
    {
        if (monster.Dead || info.SrcID != monster.Uid || info.SessionId <= 0
            || _data.Row("SkillConfig", info.SkillID) == null) return Task.CompletedTask;
        uint route = extra ? highLevel ? ExtraUseInSkillHigh : ExtraUseInSkill : highLevel ? UseInSkillHigh : UseInSkill;
        return _world.ToUnitViewersAsync(monster, route, info);
    }

    /// <summary>Standalone presentation only, after the script resolves a real effect; never replaces 31101.</summary>
    public Task SendSkillEffectAsync(GameSession source, SkillEffectDisplayNotify effect, bool highLevel = false) =>
        source.Role != null && effect.SourceId == source.Role.Id && effect.EffectId > 0
            && _data.Row("SkillConfig", effect.SkillId) != null
            ? ToPlayerAndViewersAsync(source, highLevel ? SkillEffectHigh : SkillEffect, effect) : Task.CompletedTask;

    /// <summary>Server-timed bullet only when a skill script owns projectile travel.</summary>
    public Task SendSkillBulletAsync(GameSession source, SkillFixTimeBulletNotify bullet, bool highLevel = false) =>
        source.Role != null && bullet.SourceId == source.Role.Id && bullet.EffectId > 0 && bullet.FixedTime > 0
            && _data.Row("SkillConfig", bullet.SkillConfigId) != null
            ? ToPlayerAndViewersAsync(source, highLevel ? SkillBulletHigh : SkillBullet, bullet) : Task.CompletedTask;

    /// <summary>Reflect a real target eligibility transition for an AOI-visible unit (31122).</summary>
    public async Task SetSkillTargetSelectionAsync(GameSession viewer, WorldUnit target, int skillTag, bool white, bool enabled)
    {
        if (viewer.Role == null || skillTag <= 0 || !target.Viewers.Contains(viewer) || target.WorldScene != viewer.WorldScene) return;
        var key = (target.Uid, white, skillTag);
        bool previous = viewer.SkillProtocol.SkillSelection.GetValueOrDefault(key);
        if (previous == enabled) return;
        if (enabled) viewer.SkillProtocol.SkillSelection[key] = true;
        else viewer.SkillProtocol.SkillSelection.Remove(key);
        var info = new SkillBlackWhiteOpInfo
        {
            ObjectId = target.Uid,
            Operation = enabled ? SkillBlackWhiteOpInfo.Types.SkillBlackWhiteOpInfoOperation.Add
                : SkillBlackWhiteOpInfo.Types.SkillBlackWhiteOpInfoOperation.Delete,
            SkillBlackWhiteInfo = new SkillBlackWhiteInfo
            {
                Type = white ? SkillBlackWhiteInfo.Types.SkillBlackWhiteInfoType.White
                    : SkillBlackWhiteInfo.Types.SkillBlackWhiteInfoType.Black,
                SkillTagId = skillTag,
            },
        };
        await viewer.SendGameAsync(SkillSelectionChange, new SkillWhiteListChangeNotify { DirtyInfo = { info } });
    }

    /// <summary>
    /// Called only after a boss actually starts a configured skill cooldown. The boss panel (LC_Skill_BossShow) lists
    /// the boss's skills with their names, icons and cooldowns; its normal attack is never one of them.
    /// </summary>
    public async Task BossCooldownStartedAsync(MonsterUnit boss, int skillId, long endMs)
    {
        if (boss.Dead || endMs <= Now || skillId == boss.Template.NormalSkill || _data.Row("SkillConfig", skillId) == null
            || _data.Row("NPCConfig", boss.Template.NpcId) is not { } npc || GameData.Int(npc, "_iImportantSkill") != 1) return;
        boss.BossSkillCds[skillId] = endMs;
        await _world.ToUnitViewersAsync(boss, BossSkillCd,
            new BossSkillShowCdNotify { SkillId = skillId, EndSkillCdTime = endMs, BossUId = boss.Uid });
    }

    /// <summary>31127 reads the visible live boss's cooldown state, not a guessed static table.</summary>
    public async Task BossCooldownSnapshotAsync(GameSession viewer, BossAllSkillShowCdReq request)
    {
        var response = new BossAllSkillShowCdRes { BossUId = request.BossUId };
        var boss = _world.Map(viewer.WorldScene).Units.OfType<MonsterUnit>()
            .FirstOrDefault(m => m.Uid == request.BossUId && !m.Dead && m.Viewers.Contains(viewer));
        if (viewer.Role == null || boss == null || _data.Row("NPCConfig", boss.Template.NpcId) is not { } npc
            || GameData.Int(npc, "_iImportantSkill") != 1) response.Result = ErrorCodes.SkillTargetInvalid;
        else foreach (var (skillId, end) in boss.BossSkillCds.Where(x => x.Value > Now).OrderBy(x => x.Key))
            response.BossSkillCds.Add(new BossSkillShowCdNotify { SkillId = skillId, EndSkillCdTime = end, BossUId = boss.Uid });
        await viewer.SendGameAsync(BossAllSkillCds, response);
    }

    /// <summary>Clear scene-scoped target and boss presentation when the scene changes.</summary>
    public async Task ClearSceneSkillProtocolAsync(GameSession viewer)
    {
        viewer.SkillProtocol.SkillSelection.Clear();
        if (viewer.Role != null) await viewer.SendGameAsync(BossClearSkillCds, new BossClearAllSkillShowCdNotify());
    }

    /// <summary>31129 records an actual resolved result; omit FormulaInfo until real calculation steps exist.</summary>
    public async Task RecordDamageStatisticAsync(GameSession source, Romsg.DamageStatistics entry)
    {
        if (source.Role == null || (entry.DamageSourceId != source.Role.Id
            && !OwnsActiveSkillSource(source, entry.DamageSourceId)) || entry.SkillId == 0 && entry.BuffId == 0) return;
        var notification = new DamageStatisticsNotify { Damages = { entry } };
        foreach (var viewer in _world.Viewers(source).Prepend(source).Distinct())
            if (viewer.Role != null && !viewer.SkillProtocol.TrainingPaused)
                await viewer.SendGameAsync(DamageStatistics, notification);
    }

    /// <summary>31133 toggles whether 31129 reaches this viewer; numeric 2 is Lua Resume/proto CancelPause.</summary>
    public async Task TrainingOperationAsync(GameSession session, BattleTrainingGroundOpReq request)
    {
        int op = (int)request.OpType;
        int error = session.Role == null || session.SceneLoading || op is not (1 or 2) ? ErrorCodes.Parameter : 0;
        if (error == 0) session.SkillProtocol.TrainingPaused = op == 1;
        await session.SendGameAsync(TrainingOperation, new BattleTrainingGroundOpResp { Result = error, OpType = request.OpType });
    }
}
