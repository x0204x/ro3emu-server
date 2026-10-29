using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// The client's skill ray (RoSpace, SceneSpaceDynamic.CheckInDirectPassFromTo) is stopped only by active air walls; a
/// tree or a wall corner on an open map never stops it. A normal attack (_iPassBlock 2) across such a cell must not be
/// refused, or auto-combat keeps re-casting at a target the client sees as free.
/// </summary>
public sealed partial class SkillCombatTests
{
    [ClientDataFact]
    public async Task ANormalAttackAcrossAnUnwalkableCellIsNotRefusedOnAnOpenMap()
    {
        await using var alice = await InWorld("Alice");
        var s = Session("alice");
        var nav = _app.Services.GetRequiredService<WorldData>().Map(s.SceneEntry)!.Nav!;
        var (from, to) = AcrossOneUnwalkableCell(nav, s.Position);
        Assert.False(nav.CanWalk(from.X, from.Y, from.Z, to.X, to.Z));

        var target = await Loop.InvokeAsync(async () =>
        {
            await _app.Services.GetRequiredService<SceneService>().TeleportInMapAsync(s, from);
            var monsters = _app.Services.GetRequiredService<MonsterService>();
            var template = monsters.Template(5000001)! with { RoamDistance = 0 };
            var spawned = await monsters.SpawnSingleAsync(s.SceneEntry, 5000001, to, s.InstanceId, customTemplate: template);
            spawned!.Hp = 1_000_000;
            return spawned;
        });

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = NoviceAttack, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
    }

    /// <summary>Two walkable cell centres 2 m apart on one row with an unwalkable cell between them, near <paramref name="near"/>.</summary>
    private static (MapPoint From, MapPoint To) AcrossOneUnwalkableCell(NavGrid nav, Vec3 near)
    {
        int cx = (int)near.X, cz = (int)near.Z;
        for (int r = 0; r < 200; r++)
            for (int x = cx - r; x <= cx + r; x++)
                for (int z = cz - r; z <= cz + r; z++)
                {
                    if (Math.Max(Math.Abs(x - cx), Math.Abs(z - cz)) != r) continue;
                    if (nav.WalkHeight(x + 0.5f, z + 0.5f, near.Y) is not { } a || nav.WalkHeight(x + 2.5f, z + 0.5f, a) is not { } b
                        || Math.Abs(a - b) > 0.5f || nav.Layers(x + 1.5f, z + 0.5f).Any(l => l.Walkable)) continue;
                    return (new MapPoint(x + 0.5f, a, z + 0.5f, 0), new MapPoint(x + 2.5f, b, z + 0.5f, 0));
                }
        throw new InvalidOperationException("no unwalkable cell between two walkable ones near the spawn");
    }
}
