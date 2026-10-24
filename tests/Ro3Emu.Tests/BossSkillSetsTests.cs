using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Xunit.Abstractions;

namespace Ro3Emu.Tests;

/// <summary>Each boss's special skills as BossSkillSets resolves them from the exported client data.</summary>
public sealed class BossSkillSetsTests(ITestOutputHelper output)
{
    private static readonly Lazy<GameData> Exported = new(() => new GameData(
        Options.Create(new ServerOptions { GameDataDir = Path.Combine(ClientData.RepoRoot, "data") }), NullLogger<GameData>.Instance));

    private static GameData Data => Exported.Value;
    private static readonly Lazy<BossSkillSets> Sets = new(() => new BossSkillSets(Data, new BossSkillOptions()));

    private static BossSkillBook? Book(int npc) => Sets.Value.For(npc, (int)new BossNormalAttacks(Data).For(npc));

    private static int[] Ids(int npc) => Book(npc)!.Skills.Select(k => k.Id).ToArray();

    [ClientDataFact]
    public void AFieldMvpCastsTheRowsNamedAfterIt()
    {
        var book = Book(5020004)!; // Orc Hero
        Assert.Equal([5203001, 5203101, 5203301, 5203401, 5203501], Ids(5020004));
        Assert.Equal([40000, 80000, 35000, 40000, 35000], book.Skills.Select(k => k.CdMs).ToArray());
        Assert.Equal(1, book.Phases);
        var roar = book.Skills[0]; // Terrifying Roar: FixCircle, _kRange 800 cm
        Assert.Equal((BossSkillArea.AroundSelf, 8f, 3733, 30), (roar.Area, roar.Radius, roar.ActionMs, roar.MaxTargets));
        var strike = book.Skills[4]; // Rock-Shattering Strike: Fan [1500, 60]
        Assert.Equal((BossSkillArea.Fan, 15f, 60f), (strike.Area, strike.Radius, strike.AngleDeg));
        Assert.Equal(2, strike.HitMs.Length); // the show's two attack points
        // Maya's Stone Glyph Slash: Rect360 [2300, 700] = 23 m ahead, 7 m wide.
        var slash = Book(5020005)!.Skills.Single(k => k.Id == 5203601);
        Assert.Equal((BossSkillArea.Rect, 23f, 7f), (slash.Area, slash.Radius, slash.Width));
    }

    // The field MVPs' summon and prop skills name no NPC (no _kLogicData); BossSkillSets.ReconstructedSummons binds them to
    // the 50010xx NPCs made for those MVPs. A summoned unit's own skill (its _kRespawnSkill) leaves the boss's rotation.
    [ClientDataFact]
    public void FieldMvpSummonsAndGroundObjectsComeFromTheirReconstructedNpcs()
    {
        var pharaoh = Book(5020016)!;
        Assert.Equal(5001035, pharaoh.Skills.Single(k => k.Id == 5209701).SummonNpc); // Summon Coffin: Pharaoh's Coffin
        Assert.Equal(5210101, pharaoh.Skills.Single(k => k.Id == 5209701).SummonBlast); // it erupts (Coffin Eruption)
        Assert.Equal(5001036, pharaoh.Skills.Single(k => k.Id == 5209901).SummonNpc); // Summon Pyramid
        Assert.DoesNotContain(5210101, Ids(5020016)); // the coffin's skill, not Pharaoh's
        Assert.DoesNotContain(5210001, Ids(5020016)); // the pyramid's Spinning Pyramid
        Assert.Equal(5001037, Book(5020008)!.Skills.Single(k => k.Id == 5213101).SummonNpc); // Drake's Water Tornado
        var tombstone = Book(5020015)!.Skills.Single(k => k.Id == 5210601); // Dracula's Gift of the Underworld
        Assert.Equal((5001042, 5214901), (tombstone.GroundNpc, tombstone.GroundBlast));
        Assert.Equal(5001031, Book(5020011)!.Skills.Single(k => k.Id == 5207101).GroundNpc); // Osiris's Horong
    }

    // A show whose displacement EffectData has a knockback curve (击退/震退) pushes the players hit 3 m away; a pull (勾/吸/拉,
    // "Pulls enemies inward toward yourself") brings them to 1 m in front of the boss (both distances inferred).
    [ClientDataFact]
    public void BossKnockbacksAndPullsMoveThePlayersHit()
    {
        var moves = Data.Table("NPCConfig").Values
            .Where(n => GameData.Int(n, "_iNPCType") == 1 && GameData.Int(n, "_iNPCSubType") is 3 or 4 or 6)
            .Select(n => Book((int)GameData.Int(n, "_iID"))).Where(b => b != null)
            .SelectMany(b => b!.Skills).Where(k => k.PushEffect > 0)
            .GroupBy(k => k.Id).ToDictionary(g => g.Key, g => g.First().PushDistance);
        foreach (var (id, distance) in moves) output.WriteLine($"{id} {Data.TextOf(Data.Row("SkillConfig", id)!.Value, "_iName")} {distance}");
        Assert.Equal(-1f, moves[5205501]); // Powerful Drain: a pull
        Assert.Contains(moves.Values, d => d == 3f);
    }

    [ClientDataFact]
    public void MvpServiceKeepsItsOwnPlansAndOtherAngelingsGetThemGenerically()
    {
        // MvpService casts the field Angeling's summon and Realm of Light itself (MvpService.Skills.cs).
        Assert.Equal([5201301, 5201401], Ids(5020001));
        Assert.Equal([5201901, 5202001], Ids(5020002));
        // Boss Challenge Angeling runs the same script without MvpService: the summon is generic there.
        var summon = Book(81000001)!.Skills.Single(k => k.Id == 5202101);
        Assert.Equal((5000036, 3, 15_000, 5201501), (summon.SummonNpc, summon.SummonCount, summon.SummonLifeMs, summon.SummonBlast));
        // Frenzy's _kBuff2 5002201 is a buff (BuffConfig _iType 1): it goes on the boss and the skill hits nobody.
        var frenzy = Book(24000028)!.Skills.Single(k => k.Id == 5202301); // the Pet Tower's Golden Thief Bug
        Assert.Equal((false, false), (frenzy.Damages, frenzy.HitsPlayers));
        Assert.Equal([(5002201, 10000, 15000)], frenzy.SelfBuffs);
        Assert.True(Book(5020004)!.Skills.All(k => k.HitsPlayers));
        // The Pet Tower's Orc Hero ("orkhero") is the field MVP family.
        Assert.Equal(Ids(5020004), Ids(24000071));
        Assert.Equal(Ids(5020014), Ids(24010198)); // "MoonlightPT"
    }

    [ClientDataFact]
    public void PhantomRealmBossesUseTheirBlockWithPhaseAndDifficultyVariants()
    {
        var orc = Book(43100000)!;
        Assert.Equal(2, orc.Phases);
        Assert.Equal([5000], orc.PhaseHpBp);
        var ssj = orc.Skills.Single(k => k.Id == 4310701);
        var ssj2 = orc.Skills.Single(k => k.Id == 4314601);
        Assert.Equal((0, 0), (ssj.FromPhase, ssj.ToPhase));
        Assert.Equal((1, int.MaxValue), (ssj2.FromPhase, ssj2.ToPhase));
        Assert.True(ssj.ChargeEffect > 0); // its show's EffectData moves the caster (a leap)
        Assert.DoesNotContain(orc.Skills, k => k.Id == 4310101 || k.CdMs < 3000); // not the swing, not sub-steps
        // Drake: the easy boss swaps "ylc" for "ylc_eazy"; the normal one never casts an easy row.
        int[] easy = [4323201, 4323301];
        Assert.DoesNotContain(Ids(43200000), id => easy.Contains(id));
        Assert.Contains(4320501, Ids(43200000));
        Assert.All(easy, id => Assert.Contains(id, Ids(43200006)));
        Assert.DoesNotContain(4320501, Ids(43200006));
        // Maya's rows are flagged _iIsNormalAtk; they are still her skills (her swing is the named 4340001).
        Assert.Contains(4340101, Ids(4340000));
    }

    [ClientDataFact]
    public void ValhallaPairsAreThePhasesOfOneSkill()
    {
        var maya = Book(5900021)!;
        Assert.Equal((0, 0), (maya.Skills.Single(k => k.Id == 5920201).FromPhase, maya.Skills.Single(k => k.Id == 5920201).ToPhase));
        Assert.Equal(1, maya.Skills.Single(k => k.Id == 5920301).FromPhase);
        Assert.Equal(Ids(5900021), Ids(5900026)); // tiers _1.._6 share the set
    }

    [ClientDataFact]
    public void StoryMiniAndRiftBossesGetTheirOwnRows()
    {
        Assert.Equal([5101201], Ids(5100000));     // Rocker: "Rocker Skills"
        Assert.Equal([5109201], Ids(5100008));     // Gryphon: not the Rift copy 16003201
        Assert.Equal([16001601, 16001701, 16002201, 16002401, 16002501], Ids(16000030)); // Rift Pharaoh
        Assert.Equal([16000201], Ids(16000003));   // Rift Cat o' Nine-Tail, 3 stars
        // "Skill1..3" number different skills: no phases for the story Golden Thief Bug.
        var bug = Book(5300002)!;
        Assert.Equal([5310101, 5310201, 5310301], bug.Skills.Select(k => k.Id).ToArray());
        Assert.Equal(1, bug.Phases);
        // The airship Evil Warrior's 9999999 ms row is cast once, when the second phase begins.
        var warrior = Book(5300042)!;
        var once = Assert.Single(warrior.Skills, k => k.Once);
        Assert.Equal((5350301, 1), (once.Id, once.FromPhase));
        Assert.Equal(2, warrior.Phases);
        // Story Baphomet's rows carry 2 s placeholder cooldowns: the configured pace instead.
        Assert.All(Book(5300003)!.Skills, k => Assert.Equal(new BossSkillOptions().PlaceholderCooldownMs, k.CdMs));
    }

    [ClientDataFact]
    public void BossesWithoutSkillRowsHaveNoBook()
    {
        Assert.Null(Book(5020018));  // Kraken: no AI script, no skills
        Assert.Null(Book(43200003)); // Drake-Hard Dummy
        Assert.Null(Book(5001027));  // Timid Poring (mini boss subtype, plain monster data)
    }

    [ClientDataFact]
    public void EveryBossCategoryWithDataIsCovered()
    {
        var bosses = Data.Table("NPCConfig").Values
            .Where(n => GameData.Int(n, "_iNPCType") == 1 && GameData.Int(n, "_iNPCSubType") is 3 or 4 or 6)
            .Select(n => (int)GameData.Int(n, "_iID")).ToArray();
        var families = bosses.Select(Book).Where(b => b != null).GroupBy(b => b!.Family.Split(' ')[0]).ToDictionary(g => g.Key, g => g.Count());
        Assert.True(families["field"] >= 17, "field MVPs");
        Assert.True(families["Phantom"] >= 18, "Phantom Realm");
        Assert.True(families["Valhalla"] >= 36, "Valhalla");
        Assert.True(families["Realm"] >= 12, "Realm of the Gods");
        Assert.True(families["Rift"] >= 30, "Rift Crisis");
        Assert.True(families["tower"] >= 26, "Pet Tower");
        Assert.True(families["mini"] >= 12, "mini bosses");
        Assert.True(families["story"] >= 10, "story");
        // Every resolved skill is castable by the client: a timeline and a show.
        foreach (var book in bosses.Select(Book).Where(b => b != null))
            Assert.All(book!.Skills, k => Assert.True(k.BusyMs > 0 && GameData.Int(k.Row, "_iScriptShow") > 0 && k.HitMs.Length > 0, $"{k.Id}"));
    }

    /// <summary>Every boss's resolved set and the totals per family, for review (dotnet test --logger "console;verbosity=detailed").</summary>
    [ClientDataFact]
    public void Census()
    {
        var bosses = Data.Table("NPCConfig").Values
            .Where(n => GameData.Int(n, "_iNPCType") == 1 && GameData.Int(n, "_iNPCSubType") is 3 or 4 or 6)
            .Select(n => (int)GameData.Int(n, "_iID")).Order().ToArray();
        var families = new SortedDictionary<string, (int Bosses, int Skills)>();
        foreach (int npc in bosses)
        {
            var book = Book(npc);
            if (book == null) continue;
            string family = book.Family.Split(' ')[0] switch { "field" => "field MVP", "Phantom" => "Phantom Realm", "Realm" => "Realm of the Gods", "Rift" => "Rift Crisis", "mini" => "mini boss", var f => f };
            var (b, s) = families.GetValueOrDefault(family);
            families[family] = (b + 1, s + book.Skills.Length);
            output.WriteLine($"{npc} {Data.TextOf(Data.Row("NPCConfig", npc)!.Value, "_kName")} [{book.Family}] phases {book.Phases}: "
                + string.Join(" ", book.Skills.Select(k => $"{k.Id}:{k.Logic}:{k.CdMs}:{k.Area}:{k.Radius:0.#}:p{k.FromPhase}{(k.ToPhase == int.MaxValue ? "+" : "-" + k.ToPhase)}{(k.Once ? ":once" : "")}{(k.SummonNpc > 0 ? ":summon" : "")}{(k.ChargeEffect > 0 ? ":charge" : "")}")));
        }
        foreach (var (family, (b, s)) in families) output.WriteLine($"== {family}: {b} bosses, {s} skills");
    }
}
