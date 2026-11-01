using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataTheory]
    [InlineData(1430602, 6, 2004101, 2092101, 8000)]
    [InlineData(1430603, 6, 2004301, 1201201, 10000)]
    [InlineData(1450401, 4, 2004501, 0, 0)]
    public async Task RemainingCardGrantedSkillsCastThroughRealWire(int card, int grid, int skill, int buff, int duration)
    {
        await using var alice = await InWorld("Cardcast");
        await Gm("cardcast", "!lvl 80");
        await Gm("cardcast", $"!item {card} 1");
        var player = Session("cardcast");
        long guid = player.Items.Single(i => i.Entry == card).Guid;
        await alice.SendGameAsync(CardService.SlotInsert, new EquipmentCardInsertReq
        { EquipPos = (EquipGridType)grid, SlotIndex = 1, CardGuid = guid });
        Assert.Equal(0, (await Next(alice, CardService.SlotInsert, EquipmentCardInsertResp.Parser)).Result);
        Assert.Equal(1, player.Progress!.GrantedSkills.GetValueOrDefault(skill / 100));
        Assert.DoesNotContain(skill / 100, player.Progress.Skills.Keys);
        var target = await Target("cardcast", stationary: true);
        await Loop.InvokeAsync(() =>
        {
            target.Hp = 1_000_000; target.StaysOutOfBattle = true; target.KeepsDamage = true;
            return Task.CompletedTask;
        });
        if (skill == 2004501)
        {
            // 同張卡的偽裝也由實際請求施放，避免繞過黑暗瞬間的前置場景。
            await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2004001 });
            Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser,
                m => m.UnitId == player.Role!.Id && m.Buffs.Any(b => b.ConfigId == 1500601));
            await Task.Delay(700); // 原偽裝動作 600ms 完成後，再開始新技能。
        }
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        { SkillId = skill, TargetId = skill == 2004301 ? player.Role!.Id : target.Uid, TargetPos = player.Position.Clone() });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        if (buff != 0)
        {
            var update = await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser,
                m => m.UnitId == player.Role!.Id && m.Buffs.Any(b => b.ConfigId == buff));
            Assert.Equal(duration, update.Buffs.Single(b => b.ConfigId == buff).Duration);
        }
        if (skill == 2004301)
            Assert.True(_app.Services.GetRequiredService<CombatService>().DetectionRadiusCm(player) > 0);
        else
        {
            await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
                m => m.SkillId == skill && m.Dsts.Any(d => d.DstID == target.Uid && d.Damage > 0));
            Assert.True(target.Hp < 1_000_000);
        }
        Assert.DoesNotContain(skill / 100, player.Progress.Skills.Keys);
    }

    [ClientDataFact]
    public async Task EveryCardSkillGrantAtEveryStarReachesClientPlanAndBothBars()
    {
        await using var alice = await InWorld("Cardgrants");
        await Gm("cardgrants", "!lvl 80");
        var player = Session("cardgrants");
        var data = _app.Services.GetRequiredService<GameData>();
        var effects = _app.Services.GetRequiredService<CardEffects>();
        var skills = _app.Services.GetRequiredService<SkillService>();
        int operations = 0;
        foreach (var row in data.Table("CardConfig").Values)
        {
            var granted = EquipmentStunts.Ids(row, "_kSkillAttribute").SelectMany(id =>
                EquipmentAffixes.Rows(data.Row("SpecialEffectConfig", id)!.Value, "_kSpecialEffect"))
                .Where(op => op is [3, _]).Select(op => op[1]).ToArray();
            if (granted.Length == 0) continue;
            await Loop.InvokeAsync(async () =>
            {
                using var held = await CharacterService.Lock(player);
                player.Items.RemoveAll(i => i.Guid == 880002);
                player.Items.Add(new InvItem
                {
                    Guid = 880002, Entry = (int)GameData.Int(row, "_iCardID"), Stack = 1,
                    Pack = 3, Grid = CharacterService.CardGrid(6, 1), Attrs = new() { [502] = GameData.Int(row, "_iTier") }
                });
                await effects.RefreshAsync(player);
            });
            // Build 是可儲存方案；卡片臨時授權只存在 SyncAsync 的送出投影。
            // 使用客戶端既有資產重取要求，並預先登記未來 5025 完整快照，避免讀到舊 Inbox。
            using var projectionWait = alice.Inbox.Expect(e => e.IsGame(SkillService.SkillDataSync)
                && DBSkillDataIncrement.Parser.ParseFrom(e.Body).SyncAllMark);
            await alice.SendGameAsync(1023, new PlayerAssetReq { AssetType = { 5 } });
            var projection = DBSkillDataIncrement.Parser.ParseFrom((await projectionWait.WaitAsync(Wait)).Body);
            foreach (long id in granted)
            {
                operations++;
                var config = data.Row("SkillConfig", id)!.Value;
                int group = (int)GameData.Int(config, "_iSkillID"), level = (int)GameData.Int(config, "_iLevel");
                Assert.Equal(1, level); // 本配對版所有 36 個授技操作均固定 Lv1；升星沒有 Lv2。
                var state = await Loop.InvokeAsync(() => Task.FromResult(skills.Build(player)));
                Assert.Contains(group, projection.SkillList.All.Value);
                Assert.Equal(level, projection.SkillAssignPlans.All.Value[projection.CurrentPlanIndex].SkillAssigns[group].SkillLevel);
                Assert.DoesNotContain(group, player.Progress!.Skills.Keys);
                foreach (bool manual in new[] { true, false })
                {
                    state = await Loop.InvokeAsync(() => Task.FromResult(skills.Build(player)));
                    var previousPlan = state.SkillAssignPlans[state.CurrentPlanIndex];
                    var previous = (manual ? previousPlan.ManualLoadSkill : previousPlan.AutoLoadSkill).ElementAtOrDefault(5);
                    bool replace = previous is { SkillId: > 0 } && previous.SkillId != group;
                    await alice.SendGameAsync(SkillService.SkillLoad, new SkillLoadReq
                    {
                        OpType = replace ? SkillLoadOperation.SkillLoadOpReplace : SkillLoadOperation.SkillLoadOpLoad,
                        IsManual = manual, SocketNum = 5,
                        Info = new SkillData { SkillId = group, SkillLevel = level },
                        ReplaceInfo = replace ? previous!.Clone() : null
                    });
                    int result = (await Next(alice, SkillService.SkillLoad, SkillLoadResp.Parser)).Result;
                    Assert.True(result == 0, $"card={GameData.Int(row, "_iCardID")} tier={GameData.Int(row, "_iTier")} "
                        + $"group={group} manual={manual} old={previous?.SkillId} replace={replace} "
                        + $"grant={player.Progress.GrantedSkills.GetValueOrDefault(group)} learned={skills.Level(player.Progress, group)} result={result}");
                    state = await Loop.InvokeAsync(() => Task.FromResult(skills.Build(player)));
                    var plan = state.SkillAssignPlans[state.CurrentPlanIndex];
                    Assert.Equal(group, (manual ? plan.ManualLoadSkill : plan.AutoLoadSkill)[5].SkillId);
                    if (manual) Assert.Equal(id, state.SkillWheel.WhellSkills[6].SkillConfigId);
                }
            }
        }
        Assert.Equal(36, operations);
        await Loop.InvokeAsync(async () =>
        {
            player.Items.RemoveAll(i => i.Guid == 880002);
            await effects.RefreshAsync(player);
        });
        Assert.Empty(player.Progress!.GrantedSkills);
    }
}
