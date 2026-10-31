using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// 既有soloTuning是重建補償而非原服證據。未改造的三名首領保留原平衡回歸門檻；本輪實包確認的故事場景
/// 改驗實際隊形及既有數值未遭擅改，不把12名冒險者或4/5登船套回舊兩人／三人混編假設。
/// </summary>
public sealed partial class DungeonTests
{
    private static readonly int[] UnchangedStoryBosses = [5300002, 5300003, 5300010];

    private async Task<GameSession> ReferencePlayer(string name, int level)
    {
        var bot = await InWorld(name);
        await Gm(bot, $"!lvl {level}");
        await Gm(bot, "!job 1100");
        var character = Service<CharacterService>();
        await OnLoop(async () =>
        {
            var s = Session(bot);
            var stats = new[] { 1, 1, 1, 1, 1, 1 }; // STR AGI VIT INT DEX LUK
            for (int i = 0; ; i++)
            {
                int stat = i % 3 == 2 ? 2 : 0; // STR, STR, VIT
                stats[stat]++;
                if (stats.Sum(character.StatCost) > character.TotalPoints(s.Progress!)) { stats[stat]--; break; }
            }
            var req = new AllocateAttrPointReq { PlanIndex = s.StatPlans.Info.CurPlanIndex };
            for (int i = 0; i < 6; i++) req.Points[i + 1] = stats[i];
            await character.AllocateAsync(s, req);
        });
        return Session(bot);
    }

    private sealed record Matchup(int Npc, int Level, double SecondsToKill, double SecondsToDie, double PlayerDps, double MonsterDps, long PlayerHp, long MonsterHp)
    {
        /// <summary>The companions' expected damage per second beside the player (0: a solo fight).</summary>
        public double AlliesDps { get; init; }
    }

    private async Task<Matchup> Measure(int npc)
    {
        var template = Service<MonsterService>().Template(npc)!;
        var player = await ReferencePlayer("Ref" + npc % 1000, template.Level);
        var (playerDps, monsterDps, playerHp) = await Service<GameLoop>().InvokeAsync(() =>
            Task.FromResult(Service<CombatService>().Duel(player, template)));
        // 目前配置實際出現的同伴；缺一份合法kit就失敗，不可靜默忽略。
        var companions = Service<ReconstructedDungeons>().AllScripted.SelectMany(s => s.Steps)
            .Where(s => s.Fight.Any(f => f.NpcId == npc)).SelectMany(s => s.Companions)
            .Select(c => Assert.IsType<CompanionUnit.Kit>(CompanionUnit.Build(Service<GameData>(), Service<MonsterService>(), c.NpcId))).ToArray();
        double alliesDps = companions.Sum(kit => Service<CombatService>().CompanionDps(kit, template));
        return new Matchup(npc, template.Level, template.MaxHp / (playerDps + alliesDps), playerHp / monsterDps,
            playerDps, monsterDps, playerHp, template.MaxHp)
        { AlliesDps = alliesDps };
    }

    [ClientDataFact]
    public async Task UnobservedStoryBossesKeepTheirExistingSoloTuningRange()
    {
        var results = new List<Matchup>();
        foreach (int npc in UnchangedStoryBosses) results.Add(await Measure(npc));
        string table = string.Join("; ", results.Select(r => FormattableString.Invariant(
            $"{r.Npc} L{r.Level}: boss falls in {r.SecondsToKill:0} s, player falls in {r.SecondsToDie:0} s (x{r.SecondsToDie / r.SecondsToKill:0.00}) [player {r.PlayerHp} hp {r.PlayerDps:0}/s, companions {r.AlliesDps:0}/s, boss {r.MonsterHp} hp {r.MonsterDps:0}/s]")));
        Assert.True(results.All(r => r.SecondsToKill is >= 60 and <= 120 && r.SecondsToDie / r.SecondsToKill is >= 1.1 and <= 1.8), table);
    }

    [ClientDataFact]
    public void CapturedStoryBossesKeepTheExistingScalingAndExportedAttributes()
    {
        // 凍結本輪之前已存在的重建補償；不是宣稱這些比例等於原服，也不以它們重調boss。
        AssertExistingStoryAttributes(5300001, 1170, 1500);
        AssertExistingStoryAttributes(5300006, 2420, 1035);
        AssertExistingStoryAttributes(5300018, 2870, 1230);
        AssertExistingStoryAttributes(5300082, 5210, 2880);
    }

    [ClientDataFact]
    public void CapturedAirshipBoardersUseFourWarriorsThenFiveArchersWithoutRetuning()
    {
        var airship = Assert.Single(Service<ReconstructedDungeons>().AllScripted, scene => scene.Scene == 1000501);
        var boarders = airship.Steps.Where(step => step.Fight.Any(spawn => spawn.NpcId is >= 5300042 and <= 5300050)).ToArray();
        Assert.Equal(2, boarders.Length);
        Assert.Equal([(5300042, 44L), (5300043, 45L), (5300044, 46L), (5300045, 47L)],
            boarders[0].Fight.Select(spawn => (spawn.NpcId, spawn.Point)));
        Assert.Equal([(5300046, 48L), (5300047, 49L), (5300048, 50L), (5300049, 51L), (5300050, 52L)],
            boarders[1].Fight.Select(spawn => (spawn.NpcId, spawn.Point)));
        foreach (int npc in Enumerable.Range(5300042, 9)) AssertExistingStoryAttributes(npc, 10000, 1900);
    }

    private void AssertExistingStoryAttributes(int npcId, int hpScale, int attackScale)
    {
        Assert.Equal((hpScale, attackScale), Service<ReconstructedDungeons>().SoloTuning(npcId));
        var template = Assert.IsType<MonsterTemplate>(Service<MonsterService>().Template(npcId));
        var npc = Service<GameData>().Row("NPCConfig", npcId) ?? throw new InvalidOperationException($"Missing NPC {npcId}");
        var raw = MonsterService.TemplateAttributes(Service<GameData>(), npc, (int)GameData.Int(npc, "_iLevel"));
        long Scaled(long attribute, int scale) => (long)Math.Round(raw.GetValueOrDefault(attribute) * scale / 10000);
        Assert.Equal(Math.Max(1, Scaled(101, hpScale)), template.MaxHp);
        Assert.Equal(Scaled(103, attackScale), template.Atk);
        Assert.Equal(Scaled(104, attackScale), template.Matk);
        Assert.Equal(Scaled(105, 10000), template.Def);
        Assert.Equal(Scaled(106, 10000), template.Mdef);
    }
}
