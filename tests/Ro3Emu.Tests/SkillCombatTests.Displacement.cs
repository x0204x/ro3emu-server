using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// SkillConfig._bMovement is enum_SkillLockMoveType (Lua_GameDefine.lua: 0 Lock, 1 UnLock, 2 WalkUnLock), "may the
/// caster walk while casting" (LC_Skill_RunTimeContext CheckCanSkillMove), never a displacement. Displacements are the
/// m_kPoint.m_bMovement EffectData of the skill's show, which 31112 must name (SkillStartMove ignores effectId 0).
/// </summary>
public sealed partial class SkillCombatTests
{
    private CombatService Combat => _app.Services.GetRequiredService<CombatService>();
    private static string Script(System.Text.Json.JsonElement row) =>
        row.TryGetProperty("_kScriptLogic", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";

    [ClientDataFact]
    public void DisplacementEffectsAreTheShowsMovementEffectData()
    {
        Assert.Equal((1510403, 180), Combat.MovementEffect(15104));    // BackSlide
        Assert.Equal((1300504, 100), Combat.MovementEffect(13005));    // ArrowRepel
        Assert.Equal((1332606, 160), Combat.MovementEffect(13326));    // WolvesDash
        Assert.Equal((1631405, 200), Combat.MovementEffect(16314));    // PileBunker knockback, not 1631406
        Assert.Equal((11308105, 250), Combat.MovementEffect(113081));  // PhantomThrust pull (8-digit show)
        Assert.Equal((1542903, 180), Combat.MovementEffect(15429));    // RetreatingShadows
        Assert.Equal((0, 0), Combat.MovementEffect(15112));            // Disguise: _bMovement 1, no displacement
        var data = _app.Services.GetRequiredService<GameData>();
        var skills = data.Table("SkillConfig").Values.ToList();
        Assert.Equal((1210210, 70), Combat.MovementEffect(12102));     // Jupitel Thunder knockback
        Assert.Equal((1231665, 200), Combat.MovementEffect(12316));    // Tetra Vortex pull
        Assert.Equal((1234212, 200), Combat.MovementEffect(12342));    // Gravitational Impact pull
        Assert.Equal((1201004, 160), Combat.MovementEffect(12010));    // Fire Wall knockback
        Assert.Equal((1162411, 1100), Combat.MovementEffect(11624));   // Fearless Charge's first one is the caster's charge...
        Assert.Equal((1162412, 450), CombatService.MovementEffectById(data, 1162412)); // ...its knockback the override
        // Every rule meets a skill and has a displacement effect, its own show's or the named one.
        foreach (var (script, _) in CombatService.Displacements)
            Assert.Contains(skills, r => Script(r) == script
                && (CombatService.MovementEffectOverrides.TryGetValue(script, out int named)
                    ? CombatService.MovementEffectById(data, named).Id : Combat.MovementEffect(GameData.Int(r, "_iScriptShow")).Id) != 0);
        // A named effect is a real displacement EffectData of a rule.
        foreach (var (script, effect) in CombatService.MovementEffectOverrides)
        {
            Assert.True(CombatService.Displacements.ContainsKey(script), script);
            Assert.Equal(effect, CombatService.MovementEffectById(data, effect).Id);
        }
        // The skills the old reading moved (Stasis, Choral, VolleyArrows, ShieldsProtection...) are no rules. The rules
        // among the walk-while-casting ones are there for their descriptions: Crescent Moon "pulls the targets closer",
        // Earthshaker's final hit knocks back, Axe Tornado and the Madogear slam pull to their centre, Gravitational Impact pulls in.
        var unlocked = skills.Where(r => GameData.Int(r, "_bMovement") == 1).Select(r => Script(r)).ToHashSet();
        Assert.True(unlocked.Count > 40);
        Assert.Equal(["AxeLeapAttackS1BD2", "AxeTornadoProS1BD1", "BackSlide", "MoonSlasher2", "MoonSlasher2s1", "PinpointAttack3",
            "RetreatingShadows", "RetreatingShadowsS1", "VectorShock", "VectorShock1"],
            unlocked.Where(CombatService.Displacements.ContainsKey).OrderBy(s => s, StringComparer.Ordinal).ToArray());
    }

    /// <summary>A knockback or pull on a monster for each kind of rule: away from / toward the caster, toward the area's
    /// centre (Tetra Vortex's point, Gravitational Impact's selected enemy) and away from a field (Fire Wall).</summary>
    [ClientDataTheory]
    [InlineData(11624, 1162401, 1162412, "push-caster")]  // Fearless Charge, its knockback override (inferred 3 m)
    [InlineData(11628, 1162801, 1234212, "pull-caster")]  // Spear Formation - Crescent Moon, borrowed pull
    [InlineData(12316, 1231601, 1231665, "pull-point")]   // Tetra Vortex, its own pull toward the target point
    [InlineData(12342, 1234201, 1234212, "pull-enemy")]   // Gravitational Impact, toward the selected enemy
    [InlineData(12010, 1201001, 1201004, "push-field")]   // Fire Wall, 2 m off the wall when touched
    public async Task KnockbacksAndPullsMoveTheMonstersHit(int group, int skill, int effect, string kind)
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Teach("alice", group);
        var session = Session("alice");
        var nav = _app.Services.GetRequiredService<WorldData>().Map(session.SceneEntry)!.Nav;
        var start = session.Position.Clone();
        var (ux, uz) = new[] { (1f, 0f), (-1f, 0f), (0f, 1f), (0f, -1f) }.First(o => nav == null
            || nav.CanWalk(start.X, start.Y, start.Z, start.X + o.Item1 * 8, start.Z + o.Item2 * 8));
        // The point the skill aims at (4 m out for an area, on the target for the rest) and where the monster stands.
        float aim = kind is "pull-point" or "push-field" or "pull-enemy" ? 4f : 3f;
        float stand = kind switch { "pull-point" => 6.5f, "push-field" => 4.5f, "pull-enemy" => 6f, "push-caster" => 1.5f, _ => 3f };
        var target = await Target("alice", stationary: true, npcId: 5000002);
        var anchor = kind == "pull-enemy" ? await Target("alice", stationary: true, npcId: 5000002) : null;
        await Loop.InvokeAsync(() =>
        {
            target.X = start.X + ux * stand; target.Z = start.Z + uz * stand; target.Hp = 1000000;
            if (anchor != null) { anchor.X = start.X + ux * aim; anchor.Z = start.Z + uz * aim; anchor.Hp = 1000000; }
            // The map's own monsters stay out of the area: the skill takes the nearest targets.
            foreach (var other in _app.Services.GetRequiredService<World>().Map(session.WorldScene).Units.OfType<MonsterUnit>())
                if (other != target && other != anchor) { other.X += 200; other.Z += 200; }
            return Task.CompletedTask;
        });
        var point = new Vec3 { X = start.X + ux * aim, Y = start.Y, Z = start.Z + uz * aim };
        var before = target.Pos;
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        {
            SkillId = skill, TargetId = anchor?.Uid ?? target.Uid, TargetPos = kind == "pull-enemy" ? anchor!.Pos : point,
            Face = MonsterService.Angle(start.X, start.Z, point.X, point.Z),
        });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var moved = await Next(alice, CombatService.SkillMove, SkillStartMoveNotify.Parser, m => m.EntityID == (ulong)target.Uid);
        Assert.Equal(effect, moved.EffectId);
        Assert.Equal(2, moved.MoveType);
        var from = kind is "push-caster" or "pull-caster" ? start : point;
        float was = MonsterService.Distance(from.X, from.Z, before.X, before.Z), now = MonsterService.Distance(from.X, from.Z, moved.EndPos.X, moved.EndPos.Z);
        if (kind.StartsWith("push", StringComparison.Ordinal)) Assert.True(now > was + 0.1f, $"{was} -> {now}");
        else Assert.InRange(now, 0.9f, 1.1f); // a pull stops 1 m from the caster or the centre
        await Loop.InvokeAsync(() => { Assert.Equal(moved.EndPos, target.Pos); return Task.CompletedTask; });
    }

    [ClientDataFact]
    public async Task AnUnlockedMovementSkillDoesNotMoveItsCaster()
    {
        // Disguise 1511201: _bMovement 1 and _kRange [100,...]: the old reading moved the caster 1 m.
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 5200"); await Teach("alice", 15112);
        var session = Session("alice");
        var start = session.Position.Clone();
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1511201, Face = 90 });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var deadline = DateTime.UtcNow + Wait;
        while (await Loop.InvokeAsync(() => Task.FromResult(session.SkillCast is { Resolved: false })) && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        await Task.Delay(100);
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillMove));
        await Loop.InvokeAsync(() => { Assert.Equal(start, session.Position); return Task.CompletedTask; });
    }

    [ClientDataFact]
    public async Task ArrowRepelKnocksTheTargetBackWithItsEffect()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 3100"); await Teach("alice", 13005);
        var session = Session("alice");
        var nav = _app.Services.GetRequiredService<WorldData>().Map(session.SceneEntry)!.Nav;
        var start = session.Position.Clone();
        // A stationary target 2 m away with walkable ground behind it.
        var (dx, dz) = new[] { (2f, 0f), (-2f, 0f), (0f, 2f), (0f, -2f) }.First(o => nav == null
            || nav.CanWalk(start.X, start.Y, start.Z, start.X + o.Item1 * 3, start.Z + o.Item2 * 3));
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() =>
        {
            target.X = start.X + dx; target.Z = start.Z + dz; target.Hp = 1000000;
            return Task.CompletedTask;
        });
        var before = target.Pos;
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1300501, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var moved = await Next(alice, CombatService.SkillMove, SkillStartMoveNotify.Parser, m => m.EntityID == (ulong)target.Uid);
        Assert.Equal(1300504, moved.EffectId);
        Assert.Equal(2, moved.MoveType); // enum_SkillMoveType.Repel
        Assert.Equal(100, moved.MoveTimeMs);
        Assert.Equal(before, moved.StartPos);
        float pushed = MonsterService.Distance(before.X, before.Z, moved.EndPos.X, moved.EndPos.Z);
        Assert.InRange(pushed, 0.1f, 6.01f); // _kDescData[3] = 6 m knockback
        Assert.True(MonsterService.Distance(start.X, start.Z, moved.EndPos.X, moved.EndPos.Z) > 2f);
        await Loop.InvokeAsync(() => { Assert.Equal(moved.EndPos, target.Pos); return Task.CompletedTask; });
    }

    /// <summary>A PvP hit moves the player it struck as it moves a monster: 31112 MoveType 2 to the target and its
    /// viewers, and the server position follows. Sightrasher has no effect of its own and borrows Pile Bunker's; Tetra Vortex pulls toward its target point.</summary>
    [ClientDataTheory]
    [InlineData(3100, 13005, 1300501, 1300504, 6f)] // Arrow Repel: its own 1300504, _kDescData[3] = 6 m
    [InlineData(2200, 12101, 1210101, 1631405, 2f)] // Sightrasher: borrowed 1631405, _kDescData[4] = 2 m
    [InlineData(2200, 12316, 1231601, 1231665, -1f)] // Tetra Vortex: pulled to 1 m from the target point 4 m out
    public async Task APvpHitKnocksThePlayerBack(int job, int group, int skill, int effect, float metres)
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Gm("alice", $"!job {job}"); await Teach("alice", group);
        await Gm("bob", "!lvl 99"); // a full HP pool that a low-level hit leaves standing
        if (metres < 0) await Gm("alice", "!lvl 99"); // Tetra Vortex costs more SP than a level 1 pool
        var source = Session("alice");
        var victim = Session("bob");
        var nav = _app.Services.GetRequiredService<WorldData>().Map(source.SceneEntry)!.Nav;
        var start = source.Position.Clone();
        // The victim 2 m away with walkable ground behind it (a pull: 6.5 m out, past the point 4 m out it is pulled to).
        bool pull = metres < 0;
        var (ux, uz) = new[] { (1f, 0f), (-1f, 0f), (0f, 1f), (0f, -1f) }.First(o => nav == null
            || nav.CanWalk(start.X, start.Y, start.Z, start.X + o.Item1 * 8, start.Z + o.Item2 * 8));
        float dx = ux * (pull ? 6.5f : 2f), dz = uz * (pull ? 6.5f : 2f);
        var point = new Vec3 { X = start.X + ux * 4, Y = start.Y, Z = start.Z + uz * 4 };
        await Loop.InvokeAsync(() =>
        {
            victim.Position = new Vec3 { X = start.X + dx, Y = start.Y, Z = start.Z + dz };
            Combat.PlayerHostile = (a, b) => a == source && b == victim;
            return Task.CompletedTask;
        });
        var before = victim.Position.Clone();
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        {
            SkillId = skill, TargetId = victim.Role!.Id, TargetPos = pull ? point : before,
            Face = MonsterService.Angle(start.X, start.Z, before.X, before.Z),
        });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var moved = await Next(bob, CombatService.SkillMove, SkillStartMoveNotify.Parser, m => m.EntityID == (ulong)victim.Role.Id);
        Assert.Equal(effect, moved.EffectId);
        Assert.Equal(2, moved.MoveType); // enum_SkillMoveType.Repel
        Assert.Equal(skill, moved.SkillConfigId);
        Assert.Equal(before, moved.StartPos);
        if (pull) Assert.InRange(MonsterService.Distance(point.X, point.Z, moved.EndPos.X, moved.EndPos.Z), 0.9f, 1.1f);
        else
        {
            Assert.InRange(MonsterService.Distance(before.X, before.Z, moved.EndPos.X, moved.EndPos.Z), 0.1f, metres + 0.01f);
            Assert.True(MonsterService.Distance(start.X, start.Z, moved.EndPos.X, moved.EndPos.Z) > 2f); // away from the caster
        }
        // The caster, a viewer of the target, sees the same move.
        await Next(alice, CombatService.SkillMove, SkillStartMoveNotify.Parser, m => m.EntityID == (ulong)victim.Role.Id);
        await Loop.InvokeAsync(() => { Assert.Equal(moved.EndPos, victim.Position); return Task.CompletedTask; });
    }
}
