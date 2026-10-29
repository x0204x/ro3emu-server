using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// The reconstructed story bosses are tuned for a solo player (Reconstructed/dungeon-monsters.json soloTuning): the
/// original fought them with allied NPC companions, most of which are not reproduced. The reference player is a
/// Swordman at the boss's level with every stat point spent STR:VIT 2:1 through 31500 and no equipment, fighting with
/// normal attacks only; CombatService.Duel gives both sides' expected damage per second from the real formula. A boss
/// whose scripted step shows companions (Eddga beside Boya and Wule) falls to the player and them together:
/// CombatService.CompanionDps adds each companion's expected damage per second, standing in reach.
/// Target: the boss falls in 60-120 s, and it would kill the player in 1.1-1.8 times that (no potions, no skills).
/// </summary>
public sealed partial class DungeonTests
{
    private static readonly int[] StoryBosses = [5300001, 5300002, 5300003, 5300006, 5300010, 5300018, 5300082];

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
        // The companions shown during the step that fights this boss (Payon Forest: Boya and Wule beside Eddga).
        var companions = Service<ReconstructedDungeons>().AllScripted.SelectMany(s => s.Steps)
            .Where(s => s.Fight.Any(f => f.NpcId == npc)).SelectMany(s => s.Companions)
            .Select(c => CompanionUnit.Build(Service<GameData>(), Service<MonsterService>(), c.NpcId)!).ToArray();
        double alliesDps = companions.Sum(kit => Service<CombatService>().CompanionDps(kit, template));
        return new Matchup(npc, template.Level, template.MaxHp / (playerDps + alliesDps), playerHp / monsterDps,
            playerDps, monsterDps, playerHp, template.MaxHp)
        { AlliesDps = alliesDps };
    }

    [ClientDataFact]
    public async Task EveryStoryBossIsASoloFightAtItsLevel()
    {
        var results = new List<Matchup>();
        foreach (int npc in StoryBosses) results.Add(await Measure(npc));
        string table = string.Join("; ", results.Select(r => FormattableString.Invariant(
            $"{r.Npc} L{r.Level}: boss falls in {r.SecondsToKill:0} s, player falls in {r.SecondsToDie:0} s (x{r.SecondsToDie / r.SecondsToKill:0.00}) [player {r.PlayerHp} hp {r.PlayerDps:0}/s, companions {r.AlliesDps:0}/s, boss {r.MonsterHp} hp {r.MonsterDps:0}/s]")));
        Assert.True(results.Single(r => r.Npc == 5300001).AlliesDps > 0, table); // Eddga falls to Boya and Wule too
        Assert.True(results.All(r => r.SecondsToKill is >= 60 and <= 120 && r.SecondsToDie / r.SecondsToKill is >= 1.1 and <= 1.8), table);
    }

    [ClientDataFact]
    public async Task AGroupOfThreeAirshipBoardersIsASoloFight()
    {
        // The nine boarders come in groups of three (two warriors and an archer, or one and two): all three attack at
        // once, the player kills them one after another.
        var warrior = await Measure(5300042);
        var archer = await Measure(5300046);
        double kill = 3 * (warrior.SecondsToKill + archer.SecondsToKill) / 2;
        double die = warrior.PlayerHp / (warrior.MonsterDps + warrior.MonsterDps + archer.MonsterDps);
        Assert.True(kill is >= 45 and <= 120 && die / kill is >= 1.1 and <= 1.8,
            FormattableString.Invariant($"group falls in {kill:0} s, player falls in {die:0} s"));
    }
}
