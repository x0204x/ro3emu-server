using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataTheory]
    [InlineData(1200401, 12004, 117.5f, 6.19f, 163.5f)]
    [InlineData(1201201, 12012, 117.5f, 6.16f, 164.5f)]
    public async Task AntHellFireSkillCastIsNotRejectedByWalkability(int skillId, int group,
        float casterX, float casterY, float casterZ)
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 2100");
        await Teach("alice", group);
        await Gm("alice", FormattableString.Invariant($"!warp 10540 {casterX} {casterZ}"));
        var change = await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        Assert.Equal(10540, change.SceneEntry);
        await alice.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = change.SceneEntry });
        Assert.Equal(0, (await Next(alice, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        await Next(alice, MsgId.EntitySelf, EntitySelf.Parser);
        await Loop.InvokeAsync(() => _app.Services.GetRequiredService<SceneService>()
            .TeleportInMapAsync(Session("alice"), new MapPoint(casterX, casterY, casterZ, 0)));

        var target = await Loop.InvokeAsync(() => _app.Services.GetRequiredService<MonsterService>()
            .SpawnSingleAsync(10540, 5000055, new MapPoint(126.2496f, 6.19f, 170.8085f, 0)));
        Assert.NotNull(target);
        await alice.SendGameAsync(CombatService.UseSkill,
            new CastSkillReq { SkillId = skillId, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
    }
}
