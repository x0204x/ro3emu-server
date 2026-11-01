using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    // 只供明確測試成功施放的 fixture 指定原版裝備；Teach 不會隱性改裝備或條件。
    private Task EquipConditionFixtureAsync(string account, int entry, int grid = 1) => Loop.InvokeAsync(() =>
    {
        var source = Session(account);
        source.Items.RemoveAll(i => i.Pack == 2 && i.Grid is 1 or 2);
        source.Items.Add(new InvItem { Guid = 9874203, Entry = entry, Pack = 2, Grid = grid, Stack = 1 });
        return Task.CompletedTask;
    });

    [ClientDataTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EquipmentAndPendantConditionsGateRealWireCastWithoutSpOrCooldownMutation(bool pendant)
    {
        await using var bot = await InWorld("EquipWire");
        IsolateDirectHealing();
        await Gm("equipwire", pendant ? "!job 3200" : "!job 3100");
        int group = pendant ? 13102 : 13004;
        await Teach("equipwire", group, 1);
        var source = Session("equipwire");
        var combat = _app.Services.GetRequiredService<CombatService>();
        combat.Rolls = new CardOffenceRolls();
        var target = await Target("equipwire", stationary: true, immobile: true);
        await CardOffenceDummyAsync(target);
        await HealingStat(bot, 102, 10000);
        await Loop.InvokeAsync(async () =>
        {
            source.Items.RemoveAll(i => i.Pack == 2 && i.Grid is >= 1 and <= 8);
            source.CareerPendants = []; source.Mounts!.MountIds.Clear();
            await Character.SetHpSpAsync(source, null, 10000);
        });
        async Task Rejected()
        {
            long? before = source.CurrentSp;
            await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq
            { SkillId = group * 100 + 1, TargetId = target.Uid, TargetPos = target.Pos.Clone() });
            Assert.Equal(ErrorCodes.SkillRequirements, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            await Loop.InvokeAsync(() =>
            {
                Assert.Equal(before, source.CurrentSp);
                Assert.Empty(source.SkillCds); Assert.Empty(source.SkillModeCds); Assert.Empty(source.SkillPublicCds);
                return Task.CompletedTask;
            });
        }
        await Rejected();
        await Loop.InvokeAsync(() =>
        {
            // 真表施放資格；fixture直接安裝目前裝備／附件狀態，不宣稱測過裝備或租用網路操作。
            if (pendant) source.CareerPendants = [52001];
            else source.Items.Add(new InvItem { Guid = 9874202, Entry = 2110120, Pack = 2, Grid = 1, Stack = 1 });
            source.Mounts!.MountIds[2] = new MountInfo { MountId = 1500400, IsRiding = true };
            return Task.CompletedTask;
        });
        await Rejected(); // 正確裝備／附件仍不能略過同棵 AND 的 2004：騎狼時拒絕。
        await Loop.InvokeAsync(() => { source.Mounts!.MountIds.Clear(); return Task.CompletedTask; });
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        combat.SkillCastCompleted.Add((s, _, _) =>
        { if (s == source) { source.Attack = null; done.TrySetResult(); } return Task.CompletedTask; });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        { SkillId = group * 100 + 1, TargetId = target.Uid, TargetPos = target.Pos.Clone() });
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        Assert.Contains(hit.Dsts, d => d.DstID == target.Uid && d.Damage > 0);
        await done.Task.WaitAsync(Wait);
        await Loop.InvokeAsync(async () =>
        {
            Assert.True(source.SkillCds.ContainsKey(group) || source.SkillModeCds.Keys.Any(k => k.Item1 == group));
            if (!pendant) Assert.True(source.CurrentSp < 10000);
            await combat.ClearSkillsAsync(source);
            // 獨立驗卸裝資格，清除已成功施放的原版冷卻；不改產品冷卻秒數。
            source.SkillCds.Clear(); source.SkillModeCds.Clear(); source.SkillPublicCds.Clear();
            source.CareerPendants = [];
            foreach (var item in source.Items.Where(i => i.Guid == 9874202)) item.Grid = 0;
        });
        await Rejected();
    }

    [ClientDataTheory]
    [InlineData(2050120, 5)]
    [InlineData(2040130, 4)]
    [InlineData(2010120, 1)]
    [InlineData(2500120, 50)]
    public async Task EquipmentConditionUsesCurrentlyWornItemSubtypeRatherThanEquipPart(int entry, int subtype)
    {
        await using var bot = await InWorld("EquipLeaf");
        var source = Session("equipleaf");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var data = _app.Services.GetRequiredService<GameData>();
        await Loop.InvokeAsync(() =>
        {
            source.Items.Clear();
            var row = data.Row("ItemConfig", entry)!.Value;
            Assert.Equal(5, GameData.Int(row, "_iType"));
            Assert.Equal(subtype, GameData.Int(row, "_iSubType"));
            var item = new InvItem { Guid = 9874201, Entry = entry, Pack = 2, Grid = 0, Stack = 1 };
            source.Items.Add(item);
            Assert.False(combat.EvaluateEquipmentCastCondition(source, [2001, subtype], true));
            item.Grid = subtype == 50 ? 2 : 1; item.Pack = 1;
            Assert.False(combat.EvaluateEquipmentCastCondition(source, [2001, subtype], true));
            item.Pack = 2;
            Assert.True(combat.EvaluateEquipmentCastCondition(source, [2001, subtype], true));
            Assert.True(combat.EvaluateEquipmentCastCondition(source, [2001, subtype], false));
            Assert.False(combat.EvaluateEquipmentCastCondition(source, [2001, 99], true));
            item.Grid = 9;
            Assert.False(combat.EvaluateEquipmentCastCondition(source, [2001, subtype], true));
            item.Grid = 0; // 卸下／非目前方案保存的裝備不可繼續滿足條件。
            Assert.False(combat.EvaluateEquipmentCastCondition(source, [2001, subtype], true));
            return Task.CompletedTask;
        });
    }

    [ClientDataTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    public async Task PendantConditionUsesActiveConfigTypeAndDoesNotBorrowPlayerStateForOtherActor(int type)
    {
        await using var bot = await InWorld("PendantLeaf");
        var source = Session("pendantleaf");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var data = _app.Services.GetRequiredService<GameData>();
        int id = checked((int)GameData.Int(data.Table("PendantConfig").Values.First(r => GameData.Int(r, "_iType") == type), "_iID"));
        await Loop.InvokeAsync(() =>
        {
            source.CareerPendants = [];
            Assert.False(combat.EvaluateEquipmentCastCondition(source, [2006, type], true));
            source.CareerPendants = [id];
            Assert.NotEqual(type, id);
            Assert.True(combat.EvaluateEquipmentCastCondition(source, [2006, type], true));
            Assert.False(combat.EvaluateEquipmentCastCondition(source, [2006, id], true));
            Assert.Null(combat.EvaluateEquipmentCastCondition(source, [2006, type], false));
            source.CareerPendants = [];
            Assert.False(combat.EvaluateEquipmentCastCondition(source, [2006, type], true));
            Assert.Null(combat.EvaluateEquipmentCastCondition(source, [2006], true));
            Assert.Null(combat.EvaluateEquipmentCastCondition(source, [2006, -1], true));
            Assert.Null(combat.EvaluateEquipmentCastCondition(source, [2007, type], true));
            return Task.CompletedTask;
        });
    }
}
