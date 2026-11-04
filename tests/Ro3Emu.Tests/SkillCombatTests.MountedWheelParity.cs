using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataTheory]
    [InlineData(0, 1000001, 1003201)] // 空手：依原表選取狼擊。
    [InlineData(2110120, 1300001, 1003201)] // 弓：騎狼時改為狼擊。
    [InlineData(2010120, 1000001, 1000001)] // 原表無對應的匕首：不得借用空手騎狼列。
    public async Task MountedWheelFollowsUpstreamSelectionAndKeepsWalkingPlan(int weapon, int walkingAttack, int mountedAttack)
    {
        await using var bot = await InWorld("MountedParity");
        await Gm("mountedparity", "!lvl 99"); await Gm("mountedparity", "!job 3400");
        var source = Session("mountedparity");
        var skills = _app.Services.GetRequiredService<SkillService>();
        byte[] originalPlan = [];
        await Loop.InvokeAsync(async () =>
        {
            using var held = await CharacterService.Lock(source);
            source.Items.RemoveAll(i => i.Pack == 2 && i.Grid is 1 or 2);
            if (weapon != 0)
                source.Items.Add(new InvItem { Guid = 9270002, Entry = weapon, Pack = 2, Grid = 1, Stack = 1 });
            source.Mounts!.MountIds[2] = new MountInfo { MountId = 1500400, IsRiding = false };
            Assert.Equal(walkingAttack, skills.NormalAttack(source));
            await skills.SyncAsync(source);
            originalPlan = source.SkillPlans!.ToByteArray();
        });
        if (mountedAttack != walkingAttack)
            await Next(bot, SkillService.SkillDataSync, DBSkillDataIncrement.Parser,
                m => m.CurrentAtkSkillId == walkingAttack);

        await Loop.InvokeAsync(async () =>
        {
            using var held = await CharacterService.Lock(source);
            source.Mounts!.MountIds[2].IsRiding = true;
            Assert.Equal(mountedAttack, skills.NormalAttack(source));
            var savedShape = skills.Build(source);
            Assert.Equal(walkingAttack, savedShape.CurrentAtkSkillId);
            Assert.Equal(walkingAttack, savedShape.OriginSkillWheel.AtkSkillId);
            await skills.AttackInputChangedAsync(source);
            Assert.Equal(mountedAttack, source.WheelAttack);
            Assert.Equal(originalPlan, source.SkillPlans!.ToByteArray());
        });
        if (mountedAttack == walkingAttack) return;
        var riding = await Next(bot, SkillService.SkillDataSync, DBSkillDataIncrement.Parser,
            m => m.CurrentAtkSkillId == mountedAttack);
        Assert.Equal(mountedAttack, riding.SkillWheel.Inc.AtkSkillId);
        Assert.Contains(riding.SkillWheel.Inc.WhellSkills.Values, slot => slot.Inc?.SkillConfigId == mountedAttack);
        Assert.Null(riding.OriginSkillWheel); // 騎乘只改顯示副本，原始快捷列保持步行技能。

        await Loop.InvokeAsync(async () =>
        {
            using var held = await CharacterService.Lock(source);
            source.Mounts!.MountIds[2].IsRiding = false;
            await skills.AttackInputChangedAsync(source);
            Assert.Equal(walkingAttack, skills.NormalAttack(source));
        });
        var walking = await Next(bot, SkillService.SkillDataSync, DBSkillDataIncrement.Parser,
            m => m.CurrentAtkSkillId == walkingAttack);
        Assert.Equal(walkingAttack, walking.SkillWheel.Inc.AtkSkillId);
    }
}
