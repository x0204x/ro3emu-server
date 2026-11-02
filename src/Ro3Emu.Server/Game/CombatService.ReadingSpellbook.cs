using Romsg;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    private const int ReadingBuff = 1231001, ReadingEnergy = 240001, ReadingToggleGroup = 12319;
    private readonly Dictionary<ActiveSkillCast, GameSession> _readingCopies = [];

    // SkillConfig 的八組明確 copy 列；不是把所有職業技能無條件再放一次。
    internal static int ReadingCopyGroup(long group) => group switch
    {
        12302 => 12328,
        12308 => 12321,
        12310 => 12322,
        12312 => 12323,
        12314 => 12324,
        12315 => 12325,
        12316 => 12326,
        12342 => 12347,
        _ => 0,
    };

    internal static int ReadingPageCost(SkillConfig row)
    {
        // 各列描述的「閱讀消耗」參數。靈魂爆發使用固定 @{2}，其他使用 ${n}。
        if (row.SkillId == 12302)
            return checked((int)GuardianPetRules.DescriptionLiterals(row.Row).ElementAtOrDefault(1));
        int index = row.SkillId switch { 12308 or 12310 or 12314 => 6, 12312 or 12316 or 12342 => 5, 12315 => 4, _ => -1 };
        return index >= 0 && int.TryParse(row.DescData.ElementAtOrDefault(index), out int amount) ? amount : 0;
    }

    private async Task<bool> CloseReadingSpellbookAsync(GameSession source, ActiveSkillCast cast)
    {
        var row = new SkillConfig(cast.Row);
        if (row.SkillId != ReadingToggleGroup || row.Mode != 4 || row.ScriptLogic != "book") return false;
        foreach (var buff in source.Buffs.Where(b => b.ConfigId == ReadingBuff).ToArray())
        {
            if (!CurrentCastMap(source, cast)) return true;
            await _buffs.RemoveAsync(source, buff);
        }
        await CompleteCastOnceAsync(source, cast);
        return true;
    }

    private async Task CompleteReadingSpellbookAsync(GameSession source, ActiveSkillCast original)
    {
        if (original.Request.IsExtraSkill || !CurrentCastMap(source, original)
            || !source.Buffs.Any(b => b.ConfigId == ReadingBuff && b.ExpireMs > Now)) return;
        var skill = new SkillConfig(original.Row);
        int group = ReadingCopyGroup(skill.SkillId), pages = ReadingPageCost(skill);
        if (group == 0 || pages <= 0 || SkillConfig.Get(_data, group * 100L + skill.Mode * 10 + skill.Level) is not { } copyRow
            || !await _buffs.ConsumeEnergyAmountAsync(source, ReadingEnergy, pages)
            || !CurrentCastMap(source, original)) return;
        var request = original.Request.Clone();
        request.SkillId = checked((int)copyRow.Id);
        request.IsExtraSkill = true;
        var copy = new ActiveSkillCast
        {
            Id = ++_sessionIds, OwnerId = original.OwnerId, WorldScene = original.WorldScene,
            SceneGeneration = original.SceneGeneration, Attachment = original.Attachment,
            Request = request, Row = ExtraRow(source, copyRow.Row), Origin = original.Origin.Clone(),
            StartMs = Now, ChantMs = 0, ActionMs = SkillMs(source, copyRow.Row),
            ReadingMonsters = original.ReadingMonsters ?? [],
            ReadingPlayers = original.ReadingPlayers ?? new Dictionary<GameSession, Func<bool>>(),
        };
        _readingCopies[copy] = source;
        await PublishReadingCopyAsync(source, copy, copyRow);
    }

    private async Task PublishReadingCopyAsync(GameSession source, ActiveSkillCast copy, SkillConfig row)
    {
        // (inferred) 額外效果不再詠唱、不重播角色動作。沿用 copy 原模板的動作段落點；
        // 原服精確複製相位仍待同次封包驗收，不能以 LAB 時序取代。
        var template = _data.SkillTemplate(row.ScriptShow);
        double point = template is { } t ? Numbers(t, "m_kAtkPoints").FirstOrDefault(0.2) : 0.2;
        int impact = ImpactOffset(template, point, 0, copy.ActionMs);
        if (!TryScheduleAuthoredStages(source, copy, template, () => CurrentCastMap(source, copy),
            () => { _readingCopies.Remove(copy); return Task.CompletedTask; }, chosenTargets: copy.ReadingMonsters ?? []))
            ScheduleCastDeadline(copy, impact, async () =>
            {
                try
                {
                    if (CurrentCastMap(source, copy))
                        await ApplyEffectsAsync(source, copy, copy.Request.TargetPos ?? copy.Origin,
                            chosenTargets: copy.ReadingMonsters?.Where(m => !m.Removed).ToArray());
                }
                finally { _readingCopies.Remove(copy); }
            });
        await SendCastTimelineAsync(source, new UseInSkillInfo
        {
            SrcID = copy.OwnerId, SkillID = copy.Request.SkillId, SessionId = copy.Id,
            TargetId = copy.Request.TargetId, TargetPos = copy.Request.TargetPos ?? copy.Origin,
            Face = copy.Request.Face, StartTime = copy.StartMs, ChantMs = 0, SkillMs = copy.ActionMs,
        }, highLevel: row.SkillEffectLevel > 0, extra: true, playAct: false);
    }

    private void CancelReadingCopies(GameSession source)
    {
        foreach (var copy in _readingCopies.Where(p => p.Value == source).Select(p => p.Key).ToArray())
        {
            copy.Cancelled = true;
            _readingCopies.Remove(copy);
        }
    }
}
