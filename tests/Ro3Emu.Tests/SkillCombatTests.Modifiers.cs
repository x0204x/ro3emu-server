using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataFact]
    public async Task ProfessionTalentPreservesBuffIdWhileChangingItsDurationColumn()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99");
        await Gm("alice", "!job 1200");
        await Teach("alice", 11002);
        var combat = _app.Services.GetRequiredService<CombatService>();
        var player = Session("alice");
        var before = combat.EffectiveSkillRow(player, 1100201)!.Value.GetProperty("_kBuff1")[0];
        long buffId = before[0].GetInt64(), chance = before[1].GetInt64();
        // 客戶端選擇子節點，伺服器才會一併啟用 10102129；直接升有選項的分支是無效請求。
        await ChooseProfessionTalent(alice, "alice", 10102130);
        var after = combat.EffectiveSkillRow(player, 1100201)!.Value.GetProperty("_kBuff1")[0];
        Assert.Equal(buffId, after[0].GetInt64());
        Assert.Equal(chance, after[1].GetInt64());
        Assert.Equal(120000, after[2].GetInt64());
        Assert.NotEqual(before[2].GetInt64(), after[2].GetInt64());
    }

    [ClientDataFact]
    public async Task ExplicitCostModifierPublishesAndPaysTheSameCostThenRemovalRestoresIt()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99");
        await Gm("alice", "!job 4300");
        await Teach("alice", 14201);
        var player = Session("alice");
        var combat = _app.Services.GetRequiredService<CombatService>();
        await Loop.InvokeAsync(async () =>
        {
            player.TalentSkillModifiers = [(0, 0, new SkillParameterRules.Modifier(14201, 35, 2, 7, 0, Column: 3))];
            await combat.RefreshSkillParametersAsync(player);
            await Character.SetHpSpAsync(player, null, 7);
        });
        var update = await Next(alice, CombatService.SkillAffectedEx, SkillBeAffectedParamExNotify.Parser,
            n => n.AffectParamDict.TryGetValue(14201, out var skill) && skill.AffectParam.ContainsKey(35));
        Assert.Equal([1L, AttributeId.Sp, 0, 7], Assert.Single(update.AffectParamDict[14201].AffectParam[35].AffectValueListList).AffectValueList.ToArray());
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1420101 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Loop.InvokeAsync(async () =>
        {
            Assert.Equal(0, player.CurrentSp);
            player.TalentSkillModifiers = [];
            await combat.RefreshSkillParametersAsync(player);
            Assert.Equal(15, combat.EffectiveSkillRow(player, 1420101)!.Value.GetProperty("_kCost")[0][3].GetInt64());
        });
        await Next(alice, CombatService.SkillAffectedEx, SkillBeAffectedParamExNotify.Parser,
            n => n.IsAll && (!n.AffectParamDict.TryGetValue(14201, out var skill) || !skill.AffectParam.ContainsKey(35)));
    }

    [ClientDataFact]
    public async Task RangeModifiersPublishFinalCastingDistanceAndTheSameAreaVector()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99");
        await Teach("alice", 15210);
        await Teach("alice", 11002);
        var player = Session("alice");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var buffs = _app.Services.GetRequiredService<BuffService>();
        await Loop.InvokeAsync(async () =>
        {
            player.TalentSkillModifiers = [(0, 0, new SkillParameterRules.Modifier(11002, 36, 2, 1500, 0))];
            await buffs.AddAsync(player, 1524801, 60000, player.Role!.Id);
            await combat.RefreshSkillParametersAsync(player);
        });
        var update = await Next(alice, CombatService.SkillAffectedEx, SkillBeAffectedParamExNotify.Parser,
            n => n.AffectParamDict.TryGetValue(15210, out var skill) && skill.AffectParam.ContainsKey(5)
                && n.AffectParamDict.TryGetValue(11002, out var area) && area.AffectParam.ContainsKey(36));
        Assert.Equal(10, update.AffectParamDict[15210].AffectParam[5].AffectValue);
        Assert.Equal(1000, combat.EffectiveSkillRow(player, 1521001)!.Value.GetProperty("_iDistanceMax").GetInt64());
        var effectiveArea = combat.EffectiveSkillRow(player, 1100201)!.Value.GetProperty("_kRange");
        Assert.Equal(1500, effectiveArea[0].GetInt64());
        Assert.Equal(effectiveArea.EnumerateArray().Select(v => v.GetInt64()), update.AffectParamDict[11002].AffectParam[36].AffectValueList);
        await Loop.InvokeAsync(async () =>
        {
            player.TalentSkillModifiers = [];
            await buffs.RemoveAsync(player, player.Buffs.Single(b => b.ConfigId == 1524801));
            await combat.RefreshSkillParametersAsync(player);
        });
        await Next(alice, CombatService.SkillAffectedEx, SkillBeAffectedParamExNotify.Parser,
            n => n.IsAll && !n.AffectParamDict.ContainsKey(15210) && !n.AffectParamDict.ContainsKey(11002));
        Assert.Equal(800, combat.EffectiveSkillRow(player, 1521001)!.Value.GetProperty("_iDistanceMax").GetInt64());
    }

    [ClientDataFact]
    public async Task AllSkillCooldownModifierReachesLearnedSkillsAndClearsAfterRemoval()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99");
        await Gm("alice", "!job 4300");
        await Teach("alice", 11002);
        await Teach("alice", 14201);
        var player = Session("alice");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var data = _app.Services.GetRequiredService<GameData>();
        var buffs = _app.Services.GetRequiredService<BuffService>();
        await Loop.InvokeAsync(async () =>
        {
            using (await CharacterService.Lock(player)) player.Progress!.GrantedSkills[12004] = 1;
            await buffs.AddAsync(player, 16321001, 60000, player.Role!.Id);
            await Character.SetHpSpAsync(player, null, 1000);
        });
        var update = await Next(alice, CombatService.SkillAffectedEx, SkillBeAffectedParamExNotify.Parser,
            n => n.AffectParamDict.TryGetValue(11002, out var skill) && skill.AffectParam.ContainsKey(0)
                && n.AffectParamDict.TryGetValue(14201, out var other) && other.AffectParam.ContainsKey(0));
        foreach (int group in new[] { 11002, 14201, 12004 })
        {
            long original = GameData.Int(data.Row("SkillConfig", group * 100 + 1)!.Value, "_iCD");
            long expected = (long)Math.Round(original * 0.7, MidpointRounding.AwayFromZero);
            Assert.Equal(expected, update.AffectParamDict[group].AffectParam[0].AffectValue);
            Assert.Equal(expected, GameData.Int(combat.EffectiveSkillRow(player, group * 100 + 1)!.Value, "_iCD"));
        }
        Assert.DoesNotContain(15210, update.AffectParamDict.Keys);
        long castBefore = MonsterService.NowMs;
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1420101 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        long castAfter = MonsterService.NowMs;
        await Loop.InvokeAsync(() =>
        {
            long expected = update.AffectParamDict[14201].AffectParam[0].AffectValue;
            Assert.InRange(player.SkillCds[14201], castBefore + expected, castAfter + expected);
            return Task.CompletedTask;
        });
        await Loop.InvokeAsync(() => buffs.RemoveAsync(player, player.Buffs.Single(b => b.ConfigId == 16321001)));
        await Next(alice, CombatService.SkillAffectedEx, SkillBeAffectedParamExNotify.Parser,
            n => n.IsAll && !n.AffectParamDict.ContainsKey(11002) && !n.AffectParamDict.ContainsKey(14201));
    }

    [ClientDataFact]
    public async Task SonicLogicModifierChangesTheRealHpThreshold()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99");
        await Gm("alice", "!job 5200");
        await Teach("alice", 15102);
        await EquipConditionFixtureAsync("alice", 2120120);
        var player = Session("alice");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var buffs = _app.Services.GetRequiredService<BuffService>();
        combat.Rolls = new GuardianStableDamageRolls();
        await alice.SendGameAsync(GmCommands.GeneralProcGm, new GeneralGMProcReq
        { GmType = (GmOpEnum)33, ParamString = $"{alice.Self!.Uuid},103,1000" });
        Assert.Equal(0, (await Next(alice, GmCommands.GeneralProcGm, GeneralGMProcRsp.Parser)).Result);
        Assert.Equal(1000, Character.Stats(player)[103]);
        Assert.Null(combat.LearnedPassive(player, 15103));

        async Task<long> Hit(double life)
        {
            var target = await Target("alice", stationary: true);
            await Loop.InvokeAsync(() =>
            {
                // 固定非零 ATK、零防禦與中性目標，排除小傷害取整造成的百分比失真。
                target.Template = target.Template with { MaxHp = 1_000_000, Def = 0, Element = 1 };
                target.Hp = Math.Max(1, (long)(target.Template.MaxHp * life));
                target.StaysOutOfBattle = true;
                target.KeepsDamage = true;
                player.SkillCds.Clear();
                player.SkillModeCds.Clear();
                return Task.CompletedTask;
            });
            await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1510201, TargetId = target.Uid });
            var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
            Assert.Equal(0, accepted.Result);
            var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
            await Task.Delay(1200);
            return hit.Dsts.Single(d => d.DstID == target.Uid).Damage;
        }

        long original = await Hit(0.69), enhanced = await Hit(0.49);
        // 同版資料的兩組獨立期望值：1000×76%+41 與 1000×91.2%+50。
        Assert.Equal(801, original);
        Assert.Equal(962, enhanced);
        await Loop.InvokeAsync(() => buffs.AddAsync(player, 1522701, 60000, player.Role!.Id));
        var effective = combat.EffectiveSkillRow(player, 1510201)!.Value;
        Assert.Equal(7000, effective.GetProperty("_kLogicData")[2].GetInt64());
        // 同一 Buff 還有三筆 type29/+100，以及 index1/2 的 type30/+10；不可只計門檻修改。
        Assert.Equal([7700L, 9220, 9600], effective.GetProperty("_kDamageParam1").EnumerateArray().Select(v => v.GetInt64()).ToArray());
        Assert.Equal([41L, 60, 62], effective.GetProperty("_kDamageParam2").EnumerateArray().Select(v => v.GetInt64()).ToArray());
        Assert.Equal(982, await Hit(0.69)); // 1000×92.2%+60，選中提升門檻後的增強分支。
        Assert.Equal(811, await Hit(0.71)); // 1000×77%+41，門檻外仍使用普通分支。
        await Loop.InvokeAsync(() => buffs.RemoveAsync(player, player.Buffs.Single(b => b.ConfigId == 1522701)));
        Assert.Equal(original, await Hit(0.69));
    }
}
