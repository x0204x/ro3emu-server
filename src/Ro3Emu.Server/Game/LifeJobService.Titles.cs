using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class LifeJobService
{
    /// <summary>33505 promotes exactly one title, by LifeJobTitleConfig condition, unlock cost and slot id.</summary>
    public async Task UpgradeTitleAsync(GameSession s, UpGradeJobTileReq req)
    {
        using var gate = await CharacterService.Lock(s);
        int error = ActiveLevel(s, req.JobId) < 0 ? ErrorCodes.LifeInactive : 0;
        if (error == 0)
        {
            var job = s.LifeJob!.LifeJobInfoMap[req.JobId];
            var row = _data.Table("LifeJobTitleConfig").Values.FirstOrDefault(r => GameData.Int(r, "_iJobId") == req.JobId
                && GameData.Int(r, "_iLevel") == job.TitleInfo.JobLevel + 1);
            error = row.ValueKind != System.Text.Json.JsonValueKind.Object ? ErrorCodes.LifeTitleMissing
                : !_functions.ConditionMet(s, GameData.Int(row, "_iCondition")) ? ErrorCodes.LifeTitleRequirements : 0;
            if (error == 0)
            {
                var next = s.LifeJob.Clone(); var title = next.LifeJobInfoMap[req.JobId].TitleInfo;
                title.JobLevel++;
                if (_data.Row("LifeSkillSlots", GameData.Int(row, "_iSlotId")) is { } slot)
                    title.JobTitleDetail[(int)GameData.Int(slot, "_iSort")] = Offer(slot);
                var cost = Costs(row, "_kUnlockcost");
                if (await CommitAsync(s, "cd13c/life-title-upgrade", next, s.Restrictions!.Clone(), Copy(s.LifeRuntime), cost, [],
                    result: new { Job = req.JobId, Level = title.JobLevel }) == null) error = _character.ShortageOf(s, cost);
            }
        }
        await s.SendGameAsync(UpgradeTitle, new UpGradeJobTileResp { Result = error });
    }

    /// <summary>Slot weight times rank probability, without replacement (inferred); offers are id-to-id.</summary>
    private LifeJobTitleSkillInfo Offer(System.Text.Json.JsonElement slot, int original = 0)
    {
        var pool = GameData.Pairs(slot, "_kSkillpool").Where(p => p.Value > 0 && CanActivateLifeSkill((int)p.Id))
            .Select(p => (p.Id, Value: p.Value * GameData.Int(_data.Row("LifeSkillConfig", p.Id)!.Value, "_iProbability")))
            .Where(p => p.Value > 0).ToList();
        var info = new LifeJobTitleSkillInfo { OriginalSkill = original, SelectedSkill = original };
        for (int n = 0; n < 3 && pool.Count > 0; n++)
        {
            double roll = Random.Shared.NextDouble() * pool.Sum(p => p.Value);
            int at = 0;
            for (; at < pool.Count - 1 && (roll -= pool[at].Value) >= 0; at++) { }
            int id = checked((int)pool[at].Id); info.OptionalSkills[id] = id; pool.RemoveAt(at);
        }
        return info;
    }

    public async Task ChooseTitleAsync(GameSession s, ChooseTitlePointReq req, bool extra)
    {
        using var gate = await CharacterService.Lock(s);
        int error = ActiveLevel(s, (long)req.LifejobId) < 0 ? ErrorCodes.LifeInactive : req.Level is < 1 or > int.MaxValue
            || req.TitlePoint is < 1 or > int.MaxValue ? ErrorCodes.Parameter : 0;
        if (error == 0)
        {
            var next = s.LifeJob!.Clone(); var title = next.LifeJobInfoMap[(long)req.LifejobId].TitleInfo;
            var slots = extra ? title.SkillExtraSkill : title.JobTitleDetail;
            if (!slots.TryGetValue((int)req.Level, out var skill) || !CanActivateLifeSkill((int)req.TitlePoint)
                || (!skill.OptionalSkills.Values.Contains((int)req.TitlePoint) && skill.OriginalSkill != req.TitlePoint)) error = ErrorCodes.LifeSkillMissing;
            else
            {
                if (skill.OriginalSkill != req.TitlePoint) skill.ExtraInfo = null;
                skill.SelectedSkill = (int)req.TitlePoint; skill.OriginalSkill = skill.SelectedSkill;
                await CommitAsync(s, "cd13c/life-title-choose", next, s.Restrictions!.Clone(), Copy(s.LifeRuntime), [], [],
                    result: new { Job = req.LifejobId, Slot = req.Level, Skill = req.TitlePoint, Extra = extra });
            }
        }
        if (extra) await s.SendGameAsync(ChooseTitleEx, new ChooseTitlePointExResp { Result = error });
        else await s.SendGameAsync(ChooseTitle, new ChooseTitlePointResp { Result = error });
    }

    public async Task RefreshTitleAsync(GameSession s, long job, long level, bool extra)
    {
        using var gate = await CharacterService.Lock(s);
        int error = ActiveLevel(s, job) < 0 ? ErrorCodes.LifeInactive : level is < 1 or > int.MaxValue ? ErrorCodes.Parameter : 0;
        if (error == 0)
        {
            var next = s.LifeJob!.Clone(); var title = next.LifeJobInfoMap[job].TitleInfo;
            var slots = extra ? title.SkillExtraSkill : title.JobTitleDetail;
            var row = _data.Table("LifeSkillSlots").Values.FirstOrDefault(r => GameData.Int(r, "_iJobId") == job && GameData.Int(r, "_iSort") == level);
            if (!slots.TryGetValue((int)level, out var old) || row.ValueKind != System.Text.Json.JsonValueKind.Object) error = ErrorCodes.LifeSkillMissing;
            else
            {
                slots[(int)level] = Offer(row, old.OriginalSkill > 0 ? old.OriginalSkill : old.SelectedSkill);
                slots[(int)level].ExtraInfo = old.ExtraInfo?.Clone();
                var cost = Costs(row, "_kRefreshcost");
                if (await CommitAsync(s, "cd13c/life-title-refresh", next, s.Restrictions!.Clone(), Copy(s.LifeRuntime), cost, [],
                    result: new
                    {
                        Job = job, Slot = level, Extra = extra,
                        Offered = slots[(int)level].OptionalSkills.Keys.ToArray()
                    }) == null) error = _character.ShortageOf(s, cost);
            }
        }
        if (extra) await s.SendGameAsync(RefreshTitleEx, new RefreshTitlePointExResp { Result = error });
        else await s.SendGameAsync(RefreshTitle, new RefreshTitlePointResp { Result = error });
    }

    /// <summary>
    /// Localization 12380100053/56 and LifeSkillConfig types 43/46: other skill ranks and extra slots.
    /// (inferred) Rank bonuses combine across active careers; extra slots belong to the granting career.
    /// Entitlement starts at ordinary title slots, so an extra slot cannot keep itself alive after its grant is removed.
    /// originalSkill retains the offered rank, selectedSkill is the effective rank consumed by the client.
    /// </summary>
    private void NormalizeTitles(DBLifeJobInfo life)
    {
        var active = life.ActiveLifejob.Values.Distinct().Where(life.LifeJobInfoMap.ContainsKey).ToArray();
        foreach (var info in life.LifeJobInfoMap.Values)
            foreach (var chosen in info.TitleInfo.JobTitleDetail.Values.Concat(info.TitleInfo.SkillExtraSkill.Values))
            {
                if (chosen.OriginalSkill == 0) chosen.OriginalSkill = chosen.SelectedSkill;
                chosen.SelectedSkill = chosen.OriginalSkill;
            }
        var eligible = active.ToDictionary(job => job, _ => new HashSet<int>());
        // Two possible extra slots per active profession; monotonic closure also covers rank-up of One for Many.
        for (int pass = 0; pass < 6; pass++)
        {
            var slots = active.SelectMany(job => life.LifeJobInfoMap[job].TitleInfo.JobTitleDetail.Values
                .Concat(life.LifeJobInfoMap[job].TitleInfo.SkillExtraSkill.Where(p => eligible[job].Contains(p.Key)).Select(p => p.Value))
                .Select(chosen => (Job: job, Chosen: chosen))).ToArray();
            long ascension = slots.Sum(p => _data.Row("LifeSkillConfig", p.Chosen.OriginalSkill) is { } row
                && GameData.Int(row, "_iFuctionType") == 43 ? Numbers(row, "_kFuctionDatas").FirstOrDefault() : 0);
            foreach (var (_, chosen) in slots)
            {
                if (_data.Row("LifeSkillConfig", chosen.OriginalSkill) is not { } original) continue;
                long own = GameData.Int(original, "_iFuctionType") == 43 ? Numbers(original, "_kFuctionDatas").FirstOrDefault() : 0;
                long rank = GameData.Int(original, "_iLevel") + ascension - own;
                var effective = _data.Table("LifeSkillConfig").Values.Where(r => GameData.Int(r, "_iGroup") == GameData.Int(original, "_iGroup")
                    && GameData.Int(r, "_iLevel") <= rank).OrderByDescending(r => GameData.Int(r, "_iLevel")).FirstOrDefault();
                chosen.SelectedSkill = effective.ValueKind == System.Text.Json.JsonValueKind.Object
                    ? checked((int)GameData.Int(effective, "_iID")) : chosen.OriginalSkill;
            }
            bool changed = false;
            foreach (var (job, chosen) in slots)
                if (_data.Row("LifeSkillConfig", chosen.SelectedSkill) is { } effect && GameData.Int(effect, "_iFuctionType") == 46)
                    foreach (long sort in Numbers(effect, "_kFuctionExtraData"))
                    {
                        var config = _data.Table("LifeSkillSlots").Values.FirstOrDefault(r => GameData.Int(r, "_iJobId") == job && GameData.Int(r, "_iSort") == sort);
                        if (config.ValueKind != System.Text.Json.JsonValueKind.Object || !eligible[job].Add((int)sort)) continue;
                        changed = true;
                        var extra = life.LifeJobInfoMap[job].TitleInfo.SkillExtraSkill;
                        if (!extra.ContainsKey((int)sort)) extra[(int)sort] = Offer(config);
                    }
            if (!changed) break;
        }
        foreach (long job in active)
        {
            var extra = life.LifeJobInfoMap[job].TitleInfo.SkillExtraSkill;
            foreach (int key in extra.Keys.Where(key => !eligible[job].Contains(key)).ToArray()) extra.Remove(key);
        }
    }
}
