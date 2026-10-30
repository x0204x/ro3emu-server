using Google.Protobuf;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class SeasonTalentService
{
    /// <summary>
    /// GMSeasonTalent 261 "upgradetree &lt;tree&gt;" (the class presets): every node of that tree at its maximum in the
    /// current plan, free. (inferred) The client names no choice, so each profession choice (SkillPoint gate, Profession_L,
    /// Profession_M) takes the first branch and option open to the career, as one 83550 selection sets them; the battle
    /// oath goes to the cap the unlocked trees give. Locks and quest gates are not checked: BuildUnlocks decides what the
    /// levels activate, as for bought ones.
    /// </summary>
    internal async Task<string> GmUpgradeTreeAsync(GameSession s, long treeId)
    {
        using var held = await CharacterService.Lock(s);
        if (s.SeasonTalent == null || s.Progress == null || !HasTalentGraph()) return "Season talents are not loaded for this role.";
        var tree = Nodes().Where(n => n.Tree == treeId).ToArray();
        if (tree.Length == 0) return $"No season talent tree {treeId}.";
        var next = s.SeasonTalent.Clone();
        Seed(next);
        var plan = next.Plan[next.CurPlanId];
        int raised = 0, chosen = 0;
        foreach (var node in tree.Where(n => n.Type is not (ChoiceNodeType or ProfessionBranchType or ProfessionOptionType or PvpNodeType)))
        {
            plan.TalentNodes[node.Id].CurrentLevel = SeasonTalentConfig.Get(_data, node.Id)!.Value.MaxLevel;
            raised++;
        }
        foreach (var gate in tree.Where(n => n.Type == ChoiceNodeType))
        {
            long pick = ChoiceMembers(gate.Id).FirstOrDefault(m => ChoicePath(m, s.Progress.Career)?.Gate == gate.Id);
            if (pick == 0 || ChoicePath(pick, s.Progress.Career) is not { } path) continue;
            foreach (long member in ChoiceMembers(gate.Id)) plan.TalentNodes[member].CurrentLevel = 0;
            plan.TalentNodes[gate.Id].CurrentLevel = 1;
            plan.TalentNodes[path.Branch].CurrentLevel = 1;
            plan.TalentNodes[pick].CurrentLevel = 1;
            chosen++;
        }
        BuildUnlocks(s, next);
        if (next.BattleOathLevel < next.BattleOathMaxLevel)
        {
            next.BattleOathLevel = next.BattleOathMaxLevel;
            BuildUnlocks(s, next);
        }
        var transaction = _roles.NewTransaction();
        transaction.RoleBlob(s.Role!.Id, "season_talent", next.ToByteArray());
        await _roles.CommitAsync(transaction, "season-talent/gm-upgrade-tree", _ => s.SeasonTalent = next);
        await CharacterService.SyncAsync(s, TalentSync, next.Clone());
        await _character.RefreshStatsAsync(s);
        await RefreshEffectsAsync(s);
        return $"Talent tree {treeId}: {raised} nodes at their maximum, {chosen} profession choices, oath {next.BattleOathLevel}."
            + (s.TalentEffectsEnabled ? "" : $" Inactive until quests {ParentTask} and {TalentTask} are done.");
    }
}
