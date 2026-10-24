using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Field-MVP skills, born buffs and the team score decay as the exported client data settles them.</summary>
public sealed class MvpBossSkillsTests
{
    private static readonly Lazy<GameData> Exported = new(() => new GameData(
        Options.Create(new ServerOptions { GameDataDir = Path.Combine(ClientData.RepoRoot, "data") }), NullLogger<GameData>.Instance));

    private static GameData Data => Exported.Value;

    [ClientDataFact]
    public void AngelingSummonsBlastingPoringsAndLightFields()
    {
        var plans = MvpService.ResolveBossSkills(Data, 5020001);
        var summon = Assert.Single(plans, p => p.Kind == BossSkillKind.Summon);
        Assert.Equal(5202101, summon.Skill);
        Assert.Equal(60_000, summon.CdMs);
        // 5000036 casts its _kRespawnSkill 5201501 "Poring Explosion": explode after 15 s, 20 m, up to 30 players.
        Assert.Equal(new MinionPlan(5000036, 3, 15_000, 5201501, 20f, 30), summon.Minions);
        var field = Assert.Single(plans, p => p.Kind == BossSkillKind.Field);
        Assert.Equal(5201701, field.Skill);
        Assert.Equal(45_000, field.CdMs);
        // AngelingSelect marks 5 players within 30 m for 5 s (5001901); the field lasts 20 s, hits every second, 5 m.
        Assert.Equal(new FieldPlan(5000037, 5201601, 5001901, 5000, 5, 30f, 20_000, 1000, 5f), field.Field);
        Assert.Equal(2, plans.Count);
    }

    [ClientDataFact]
    public void DevilingMirrorsAngelingWithBombringsAndDarkFields()
    {
        var plans = MvpService.ResolveBossSkills(Data, 5020003);
        Assert.Equal(new MinionPlan(5000039, 3, 15_000, 5202601, 20f, 30),
            Assert.Single(plans, p => p.Kind == BossSkillKind.Summon).Minions);
        Assert.Equal(new FieldPlan(5000040, 5202701, 5002401, 5000, 5, 30f, 20_000, 1000, 5f),
            Assert.Single(plans, p => p.Kind == BossSkillKind.Field).Field);
        // Both blasts and fields leave their curse: 5201501/5201701 Divine Retribution, 5202601/5202801 Dark Curse.
        Assert.Equal(5001801, FirstBuff(5201501));
        Assert.Equal(5001801, FirstBuff(5201701));
        Assert.Equal(5002301, FirstBuff(5202601));
        Assert.Equal(5002301, FirstBuff(5202801));
    }

    [ClientDataFact]
    public void GoldenThiefBugSummonsSixtySecondBugsAndFrenzies()
    {
        var plans = MvpService.ResolveBossSkills(Data, 5020002);
        var summon = Assert.Single(plans, p => p.Kind == BossSkillKind.Summon);
        // No blast: the Thief Bug dies with its born buff 5002801 "Summoned Unit Self-Destruct" (60 s).
        Assert.Equal(new MinionPlan(5000038, 3, 60_000, 0, 0f, 0), summon.Minions);
        var frenzy = Assert.Single(plans, p => p.Kind == BossSkillKind.SelfBuff);
        Assert.Equal((5202301, 5002201, 15_000, 30_000), (frenzy.Skill, frenzy.Buff, frenzy.BuffMs, frenzy.CdMs));
        // Delusion: damage +30 %, damage reduction -30 % (more damage taken), ASPD +100.
        var delusion = Data.Row("BuffConfig", 5002201)!.Value;
        Assert.Equal([(215L, 3000d), (216L, -3000d), (114L, 100d)], GameData.Pairs(delusion, "_kChangeAttri").ToArray());
    }

    [ClientDataFact]
    public void TheSecondThiefBugSummonHasNoFieldMvpOwner()
    {
        var skill = Data.Row("SkillConfig", 5206401)!.Value;
        Assert.Equal(5001003, skill.GetProperty("_kLogicData")[0].GetInt32());
        foreach (var mvp in Data.Table("MVPConfig").Values)
            Assert.DoesNotContain(MvpService.ResolveBossSkills(Data, (int)GameData.Int(mvp, "_iNPCID")), p => p.Skill == 5206401);
        // No NPC or AI row names it; only the summoned bug itself mentions 5001003.
        Assert.DoesNotContain(Data.Table("NPCConfig").Values, n => n.GetRawText().Contains("5206401"));
        Assert.DoesNotContain(Data.Table("AIConfig").Values, n => n.GetRawText().Contains("5206401"));
    }

    [ClientDataFact]
    public void FieldMvpsCarryEffectlessBornAndOutOfCombatBuffs()
    {
        var mvps = Data.Table("MVPConfig").Values.Select(r => Data.Row("NPCConfig", GameData.Int(r, "_iNPCID"))!.Value).ToArray();
        Assert.Equal(17, mvps.Length);
        foreach (var npc in mvps)
        {
            var born = npc.GetProperty("_kBornEventBuff").EnumerateArray().Select(t => (t[0].GetInt32(), t[1].GetInt32())).ToArray();
            Assert.Equal([(900901, -1), (904301, -1), (904101, -1)], born);
            Assert.Equal(20200001, GameData.Int(npc, "_iAI"));
        }
        var ai = Data.Row("AIConfig", 20200001)!.Value;
        Assert.Equal([901401], ai.GetProperty("_iUnFightBUFF").EnumerateArray().Select(v => v.GetInt32()).ToArray());
        Assert.Equal([1], ai.GetProperty("_iUnFightType").EnumerateArray().Select(v => v.GetInt32()).ToArray());
        Assert.Equal([5000], ai.GetProperty("_iUnFightParam").EnumerateArray().Select(v => v.GetInt32()).ToArray());
        foreach (int id in new[] { 900901, 904301, 904101, 901401 })
        {
            var buff = Data.Row("BuffConfig", id)!.Value;
            Assert.Empty(GameData.Pairs(buff, "_kChangeAttri"));
            Assert.Equal(-1, GameData.Int(buff, "_iTime"));
        }
    }

    [ClientDataFact]
    public void TeamScoreDecaysWithCappedMembers()
    {
        Assert.Equal([100, 90, 80, 70, 60, 50, 50], Enumerable.Range(0, 7).Select(k => MvpService.DecayPercent(Data, k)).ToArray());
        // Party 7 (600 points, one capped member) falls behind solo player 3 (560): 600 x 90 % = 540.
        var ranked = MvpService.RankTeams([(1, 7, 1, 300), (2, 7, 1, 300), (3, 0, 0, 560), (4, 9, 4, -25)],
            role => role == 2, k => MvpService.DecayPercent(Data, k));
        Assert.Equal([-3L, 7L, 9L], ranked.Select(t => t.Key).ToArray());
        Assert.Equal((540L, true), (ranked[1].Score, ranked[1].Decayed));
        Assert.Equal((560L, false), (ranked[0].Score, ranked[0].Decayed));
        Assert.Equal(-25, ranked[2].Score);
        // The rules text that settles the decay.
        Assert.Contains("total score is Decayed", Data.Text(10960000073));
    }

    [ClientLuaFact]
    public void AwardDropNoticeShowsNothingInTheClient()
    {
        var evidenceDir = Path.Combine(ClientData.RepoRoot, "re", "clientB");
        var source = Path.Combine(ClientData.LuaPayloadDir, "Logic", "Asset", "LC_DAL_MVP.lua.bytes");
        var output = Path.Combine(evidenceDir, "lua-src", "Logic", "Asset", "LC_DAL_MVP.lua");
        var receipt = Path.Combine(evidenceDir, "decompile.json");
        Assert.True(File.Exists(source), $"D client Lua source is missing: {source}");
        Assert.True(File.Exists(output), $"D client Lua evidence is missing: {output}. Run tools/PrepareClientLuaEvidence.ps1.");
        Assert.True(File.Exists(receipt), $"D client Lua decompilation receipt is missing: {receipt}. Run tools/PrepareClientLuaEvidence.ps1.");
        using (var receiptJson = JsonDocument.Parse(File.ReadAllText(receipt)))
        {
            Assert.Equal(Sha256(source), receiptJson.RootElement.GetProperty("SourceSha256").GetString(), ignoreCase: true);
            Assert.Equal(Sha256(output), receiptJson.RootElement.GetProperty("OutputSha256").GetString(), ignoreCase: true);
        }
        var lua = File.ReadAllText(output);
        var handler = Regex.Match(lua, @"function L5_1\(A0_2, A1_2\)\s*(?<body>(?:(?!\nend).)*)\nend\s*\nL0_1\._proessGetAwardDropNtf = L5_1",
            RegexOptions.Singleline);
        Assert.True(handler.Success);
        // The 43010 handler only reads drop_id into a local: no dataset, event or UI.
        Assert.Equal(["local L2_2", "L2_2 = A1_2.drop_id"],
            handler.Groups["body"].Value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static int FirstBuff(int skill) =>
        Data.Row("SkillConfig", skill)!.Value.GetProperty("_kBuff2")[0][0].GetInt32();
}
