using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataFact]
    public async Task ABoltSkillFliesItsBulletsAndShowsTheActionHit()
    {
        await using var mage = await InWorld("Mage");
        await Gm("mage", "!job 2100");
        await Loop.InvokeAsync(() => { Session("mage").Progress!.Skills[12004] = 3; return Task.CompletedTask; });
        var target = await Target("mage", stationary: true);
        long roleId = Session("mage").Role!.Id;
        await mage.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1200403, TargetId = target.Uid });
        var accepted = await Next(mage, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        // Fire Bolt's bolts are EffectData 1200411 (m_kBulletData fx_bullet_magicfirearrow_new): the client draws them
        // only from 31120, one notify launching split (= level) bullets; the hit itself shows 1200404 (m_kActionDamge).
        var bullet = await Next(mage, CombatService.SkillBullet, SkillFixTimeBulletNotify.Parser, b => b.TargetId == target.Uid);
        Assert.Equal(1200411, bullet.EffectId);
        Assert.Equal(3, bullet.Split);
        Assert.Equal(1200403, bullet.SkillConfigId);
        Assert.Equal(roleId, bullet.SourceId);
        Assert.Equal(roleId, bullet.MasterId);
        Assert.Equal(400, bullet.FixedTime);
        Assert.True(bullet.StartTimestampMs > 0);
        var hit = await Next(mage, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        Assert.Equal(1200404, hit.ShowPoint);
        Assert.Equal(3, hit.Split);
    }
}
