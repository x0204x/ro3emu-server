using System.Text.Json;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class SeasonTalentService
{
    // enum_SeasonTalentType: SkillPoint (the free gate of a profession choice), Profession_L, Profession_M.
    private const int ChoiceNodeType = 5, ProfessionBranchType = 6, ProfessionOptionType = 7;

    // The selection panel sends the chosen Profession_M, not separate upgrades of its
    // SkillPoint and Profession_L parents (UI_Sys_Skill_SeasonTalent_SelectPanelCtrl._onClickUpgrade).
    // Resolve the same job-filtered graph as Lua_CFG_SeasonTalentConfig.GetTreeDataByTreeID.
    private (long Gate, long Branch)? ChoicePath(long id, int career)
    {
        Nodes();
        if (_data.Row("SeasonTalentConfig", id) is not { } node) return null;
        long tree = GameData.Int(node, "_iTalentTreeID"), branch = id;
        long type = GameData.Int(node, "_iType");
        if (type is not (ProfessionBranchType or ProfessionOptionType)
            || Effect(GameData.Int(node, "_iLevelGroupId"), 1, career) == null) return null;
        if (type == ProfessionOptionType)
        {
            var parents = _parents!.GetValueOrDefault((id, tree), [])
                .Where(p => _data.Row("SeasonTalentConfig", p.Parent) is { } r
                    && GameData.Int(r, "_iType") == ProfessionBranchType).ToArray();
            if (parents.Length != 1) return null;
            branch = parents[0].Parent;
        }
        var branchRow = _data.Row("SeasonTalentConfig", branch)!.Value;
        if (Effect(GameData.Int(branchRow, "_iLevelGroupId"), 1, career) == null) return null;
        // A branch with selectable children must name one; otherwise the choice is incomplete.
        if (type == ProfessionBranchType && Children(branchRow).Any(child =>
            _data.Row("SeasonTalentConfig", child) is { } r && GameData.Int(r, "_iType") == ProfessionOptionType
            && Effect(GameData.Int(r, "_iLevelGroupId"), 1, career) != null)) return null;
        var gates = _parents!.GetValueOrDefault((branch, tree), [])
            .Where(p => _data.Row("SeasonTalentConfig", p.Parent) is { } r
                && GameData.Int(r, "_iType") == ChoiceNodeType).ToArray();
        return gates.Length == 1 ? (gates[0].Parent, branch) : null;
    }

    private static IEnumerable<long> Children(JsonElement row) =>
        row.TryGetProperty("_kAfterids", out var children) && children.ValueKind == JsonValueKind.Array
            ? children.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)) : [];

    private IEnumerable<long> ChoiceMembers(long gate)
    {
        if (_data.Row("SeasonTalentConfig", gate) is not { } row) yield break;
        foreach (long branch in Children(row))
        {
            if (_data.Row("SeasonTalentConfig", branch) is not { } b
                || GameData.Int(b, "_iType") != ProfessionBranchType) continue;
            yield return branch;
            foreach (long option in Children(b))
                if (_data.Row("SeasonTalentConfig", option) is { } o
                    && GameData.Int(o, "_iType") == ProfessionOptionType) yield return option;
        }
    }

    private async Task SelectProfessionAsync(GameSession s, SeasonTalentUpgradeReq req)
    {
        int result = 0;
        using (await CharacterService.Lock(s))
        {
            if (s.SeasonTalent == null || s.Progress == null || !Available(s)) result = ErrorCodes.FeatureUnavailable;
            else
            {
                BuildUnlocks(s);
                var path = ChoicePath(req.TalentId, s.Progress.Career);
                if (path == null) result = ErrorCodes.TalentInvalid;
                else if (!s.SeasonTalent.UnlockedNode.GetValueOrDefault(path.Value.Gate)) result = ErrorCodes.TalentLocked;
                else
                {
                    var next = s.SeasonTalent.Clone();
                    var plan = next.Plan[next.CurPlanId];
                    // (inferred) choosing again replaces this one choice, within this plan only.
                    foreach (long member in ChoiceMembers(path.Value.Gate)) plan.TalentNodes[member].CurrentLevel = 0;
                    plan.TalentNodes[path.Value.Gate].CurrentLevel = 1;
                    plan.TalentNodes[path.Value.Branch].CurrentLevel = 1;
                    plan.TalentNodes[req.TalentId].CurrentLevel = 1;
                    BuildUnlocks(s, next);
                    try
                    {
                        var transaction = _roles.NewTransaction();
                        transaction.RoleBlob(s.Role!.Id, "season_talent", next.ToByteArray());
                        await _roles.CommitAsync(transaction, "season-talent/profession", _ => s.SeasonTalent = next);
                    }
                    catch (Exception error) when (error is SqliteException or InvalidOperationException)
                    {
                        _log.LogError(error, "Profession talent selection {Node} failed for {Role}", req.TalentId, s.Role!.Id);
                        result = ErrorCodes.Unknown;
                    }
                    if (result == 0)
                    {
                        await CharacterService.SyncAsync(s, TalentSync, next.Clone());
                        await _character.RefreshStatsAsync(s);
                        await RefreshEffectsAsync(s);
                    }
                }
            }
        }
        await s.SendGameAsync(Upgrade, new SeasonTalentUpgradeResp { Result = result, TalentId = req.TalentId });
    }
}
