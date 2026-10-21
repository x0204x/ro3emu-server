using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Bosses swing their own normal-attack rows (BossNormalAttacks), on the cast's route, without a boss-panel cooldown.</summary>
public sealed partial class SkillCombatTests
{
    [ClientDataTheory]
    [InlineData(5020009, 5204901)]   // field MVP Eddga, by id
    [InlineData(16000003, 16000101)] // Rift Crisis Cat o' Nine-Tail, 3 stars
    [InlineData(4300000, 4301101)]   // Phantom Realm Golden Thief Bug: GoldenBug_attacking, not the Thief Bug's 4300201
    [InlineData(4300001, 4301101)]   // ...its easy variant
    [InlineData(43200000, 4320001)]  // Drake
    [InlineData(43200001, 4320601)]  // Drake's minion: its own row
    [InlineData(4340005, 4340001)]   // Maya (easy)
    [InlineData(4360000, 4361101)]   // Osiris ("raidFiveBoss06")
    [InlineData(4390007, 4391101)]   // Baphomet ("RaidFive_Boss09_1")
    [InlineData(5900011, 5900101)]   // Valhalla Mistress, tier 1
    [InlineData(5900026, 5920101)]   // Valhalla Maya, tier 6
    [InlineData(5900076, 5970101)]   // Valhalla Drake ("Valhala_07_skill")
    [InlineData(14720027, 14721001)] // Realm of the Gods Este (Peak)
    [InlineData(14740100, 14740100)] // Nessa (simple)
    [InlineData(14761200, 14761000)] // boss 3 (Hard)
    [InlineData(14780008, 14781101)] // boss 4 (emeng)
    [InlineData(81000001, 5204001)]  // Boss Challenge Angeling: the field MVP's AI script, its attack
    [InlineData(81000006, 5212101)]  // Boss Challenge Moonlight Flower
    [InlineData(5300001, 5300101)]   // story Eddga: MainStory_HuWangNormalAttack
    [InlineData(5300002, 5310001)]   // story Golden Thief Bug ("HuangJinChong"): MainStory_GoldenBugNormalAttack
    [InlineData(5300003, 5320001)]   // story Baphomet ("BaFengTe")
    [InlineData(5300006, 5360001)]   // story Maya
    [InlineData(5300010, 5340001)]   // story Pharaoh ("FaLaoWang")
    [InlineData(5300018, 5352001)]   // story Evil Boss ("DevilBoss"): MainStory_EvilBossNormalAttack1
    [InlineData(5300082, 5370101)]   // story Peco Peco ("DaZui"): MainStory_DazuiAttack
    [InlineData(5100000, 5101101)]   // mini boss Rocker ("MiniBossVocal" = 5101201)
    [InlineData(5100001, 5102101)]   // mini boss Eclipse, ahead of the Poporing swing its row lists
    [InlineData(5300042, 5350001)]   // airship Evil Knight: the row its NPCConfig lists, unflagged
    [InlineData(5300046, 5351001)]   // airship Evil Hunter
    [InlineData(5300088, 5353801)]   // the shortest-cooldown swing it lists, not the 15 s 5354101
    [InlineData(5000001, 5001101)]   // a field Poring keeps its flagged _kSkills row
    public void ABossSwingsItsOwnNormalAttack(int npc, int skill)
    {
        var template = _app.Services.GetRequiredService<MonsterService>().Template(npc);
        Assert.NotNull(template);
        Assert.Equal(skill, template.NormalSkill);
    }

    [ClientDataFact]
    public void ThePyramidIsNoVariantOfPharaoh()
    {
        // "RaidFive_Boss07_Jzt1" is the pyramid (UnitConfig 33124), not a difficulty of "RaidFive_Boss07".
        Assert.Equal(0, _app.Services.GetRequiredService<MonsterService>().Template(4370003)!.NormalSkill);
    }

    [ClientDataFact]
    public void EveryAiScriptAttackIsANamedSwingOfABossRunningThatScript()
    {
        var data = _app.Services.GetRequiredService<GameData>();
        var scripts = data.Table("NPCConfig").Values.Where(n => GameData.Int(n, "_iNPCType") == 1)
            .Select(n => n.TryGetProperty("_iAISpecial", out var ai) ? ai.GetString() ?? "" : "").ToHashSet();
        foreach (var (script, skill) in BossNormalAttacks.AiScriptAttacks)
        {
            Assert.True(scripts.Any(s => s.StartsWith(script, StringComparison.OrdinalIgnoreCase)), script);
            var row = data.Row("SkillConfig", skill);
            Assert.NotNull(row);
            // Valhalla Drake's swing is named "Valhala_07_skill" (documented exception).
            if (skill != 5970101) Assert.True(BossNormalAttacks.IsNamedSwing(row.Value), $"{script} -> {skill}");
        }
    }

    [ClientDataFact]
    public async Task ABossSwingOfAnEffectLevelRowTakesTheHighLevelRoute()
    {
        await using var alice = await InWorld("Alice");
        var maya = await Target("alice", stationary: true, npcId: 4340000); // 4340001 "Maya Normal Attack", _iSkillEffectLevel 1
        Assert.Equal(4340001, maya.Template.NormalSkill);
        await Loop.InvokeAsync(() => _app.Services.GetRequiredService<MonsterService>().Attack!(maya, Session("alice")));
        var swing = await Next(alice, CombatService.UseInSkillHigh, UseInSkillInfo.Parser, u => u.SrcID == maya.Uid);
        Assert.Equal(4340001, swing.SkillID);
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.UseInSkill)
            && UseInSkillInfo.Parser.ParseFrom(e.Body).SrcID == maya.Uid);
    }

    [ClientDataFact]
    public async Task ABossNormalAttackWithACooldownStartsNoBossPanelCooldown()
    {
        await using var alice = await InWorld("Alice");
        // 4300005 has _iImportantSkill 1 and swings 4300201 (2 s cooldown, effect level 0).
        var bug = await Target("alice", stationary: true, npcId: 4300005);
        Assert.Equal(4300201, bug.Template.NormalSkill);
        await Loop.InvokeAsync(() => _app.Services.GetRequiredService<MonsterService>().Attack!(bug, Session("alice")));
        await Next(alice, CombatService.UseInSkill, UseInSkillInfo.Parser, u => u.SrcID == bug.Uid && u.SkillID == 4300201);
        var combat = _app.Services.GetRequiredService<CombatService>();
        await Loop.InvokeAsync(() => combat.BossCooldownStartedAsync(bug, 4300201, MonsterService.NowMs + 2000));
        Assert.DoesNotContain(4300201, bug.BossSkillCds.Keys);
        // A boss skill still reaches the panel: 4301201 "Pyroblast".
        await Loop.InvokeAsync(() => combat.BossCooldownStartedAsync(bug, 4301201, MonsterService.NowMs + 30000));
        var shown = await Next(alice, CombatService.BossSkillCd, BossSkillShowCdNotify.Parser, n => n.BossUId == bug.Uid);
        Assert.Equal(4301201, shown.SkillId);
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.BossSkillCd)
            && BossSkillShowCdNotify.Parser.ParseFrom(e.Body).SkillId == 4300201);
    }
}
