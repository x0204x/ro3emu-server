using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private async Task<(BotClient Bot, GameSession Source, MonsterUnit Target)> NoteLeapFixtureAsync(int level, int marks = 10)
    {
        var bot = await InWorld("NoteLeap"); IsolateDirectHealing();
        await Gm("noteleap", "!lvl 99"); await Gm("noteleap", "!job 1400");
        await Teach("noteleap", 11310, level); await Teach("noteleap", 11316, level);
        var source = Session("noteleap"); await EquipPixelSpearAsync(bot, source);
        await EmblemMarksAsync(source, 1003, marks);
        var target = await Target("noteleap", stationary: true, immobile: true);
        await CardOffenceDummyAsync(target); await IsolateAuthoredStageTargetAsync(source, target);
        await Loop.InvokeAsync(async () =>
        {
            target.X = source.Position.X; target.Y = source.Position.Y; target.Z = source.Position.Z + 5;
            _app.Services.GetRequiredService<World>().Map(source.WorldScene).Moved(target);
            var buffs = _app.Services.GetRequiredService<BuffService>(); await buffs.ReconcileEnergyAsync(source);
            source.Energy[140002].NextRecoverMs = MonsterService.NowMs + 60000;
        });
        _app.Services.GetRequiredService<CombatService>().Rolls = new CardOffenceRolls();
        return (bot, source, target);
    }

    private async Task<SkillHitInfo> NoteLeapCastAsync(BotClient bot, GameSession source, long targetId, Vec3 targetPos,
        int group, int level, int mode)
    {
        var combat = _app.Services.GetRequiredService<CombatService>();
        await Loop.InvokeAsync(async () =>
        {
            await combat.InterruptAsync(source);
            source.SkillCds.Clear(); source.SkillModeCds.Clear(); source.SkillPublicCds.Clear();
        });
        var row = new SkillConfig(combat.EffectiveSkillRow(source, group * 100 + level)
            ?? throw new InvalidOperationException("Note Leap effective row required."));
        Assert.Equal(mode, row.Mode);
        var done = CastPassiveCompletion(combat, source);
        int energy = mode is 1 or 3 or 4 or 5 ? 140003 : 140002;
        long before = source.Energy[energy].Value;
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        { SkillId = group * 100 + level, TargetId = targetId, TargetPos = targetPos.Clone(), Face = 0 });
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser); Assert.Equal(0, accepted.Result);
        var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        await done.Task.WaitAsync(Wait);
        if (group == 11310) Assert.Equal(before - 1, source.Energy[energy].Value);
        return hit;
    }

    private async Task ArmNoteLeapAsync(BotClient bot, GameSession source, MonsterUnit target, int level)
    {
        await NoteLeapCastAsync(bot, source, target.Uid, target.Pos, 11316, level, 0);
        Assert.Equal(5, source.Energy[140003].Value);
        Assert.Equal(1, Assert.Single(source.Buffs, b => b.ConfigId == 1134301).PileCount);
        for (int index = 1; index <= 4; index++)
        {
            long priorExpiry = source.Buffs.Single(b => b.ConfigId == 1134301).ExpireMs;
            await NoteLeapCastAsync(bot, source, target.Uid, target.Pos, 11310, level, 1);
            var notes = Assert.Single(source.Buffs, b => b.ConfigId == 1134301);
            Assert.Equal(index + 1, notes.PileCount); Assert.Equal(3000, notes.DurationMs);
            Assert.True(notes.ExpireMs > priorExpiry); Assert.Equal(5 - index, source.Energy[140003].Value);
        }
    }

    [ClientDataTheory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task NoteLeapLowAndHighestArmOnFourHitsAndUltimatePaysTheFifthChargeOnce(int level)
    {
        var f = await NoteLeapFixtureAsync(level); await using var bot = f.Bot;
        var combat = _app.Services.GetRequiredService<CombatService>();
        using var modeWire = bot.Inbox.Expect(e => e.IsGame(CombatService.SkillAffectedEx)
            && SkillBeAffectedParamExNotify.Parser.ParseFrom(e.Body).AffectParamDict.TryGetValue(11310, out var group)
            && group.AffectParam.TryGetValue(15, out var mode) && mode.AffectValue == 4);
        await ArmNoteLeapAsync(bot, f.Source, f.Target, level); await modeWire.WaitAsync(Wait);
        var row = new SkillConfig(combat.EffectiveSkillRow(f.Source, 1131000 + level)!.Value);
        Assert.Equal(4, row.Mode); Assert.Equal(level == 1 ? 103000 : 111000, row.DamageParam1[0]);
        Assert.Null(combat.EffectiveSkillRow(f.Source, 1131050 + level));
        long expected = (long)Math.Round(Character.Stats(f.Source)[103] * row.DamageParam1[0] / 10000d);
        long hp = f.Target.Hp;
        var hit = await NoteLeapCastAsync(bot, f.Source, f.Target.Uid, f.Target.Pos, 11310, level, 4);
        Assert.Equal(expected, Assert.Single(hit.Dsts, d => d.DstID == f.Target.Uid).Damage);
        Assert.Equal(hp - expected, f.Target.Hp); Assert.Equal(0, f.Source.Energy[140003].Value);
        Assert.DoesNotContain(f.Source.Buffs, b => b.ConfigId is 1134301 or 1132401);
        Assert.Equal(0, new SkillConfig(combat.EffectiveSkillRow(f.Source, 1131000 + level)!.Value).Mode);
        Assert.DoesNotContain(111306, f.Source.Progress!.Skills.Keys);
    }

    [ClientDataTheory]
    [InlineData(1, false)]
    [InlineData(5, false)]
    [InlineData(1, true)]
    [InlineData(5, true)]
    public async Task NoteLeapPurePvpAlsoCountsOnceAndDealsTheAuthoredUltimateDamage(int level, bool fullShield)
    {
        await using var bot = await InWorld("NotePvp"); await using var enemy = await InWorld("NoteEnemy");
        await Gm("notepvp", "!job 1400"); await Gm("noteenemy", "!job 1400");
        await Teach("notepvp", 11310, level); await Teach("notepvp", 11316, level);
        var source = Session("notepvp"); var target = Session("noteenemy");
        var combat = await CastPassiveRoomAsync(bot, source, enemy, target);
        await EquipPixelSpearAsync(bot, source); await EmblemMarksAsync(source, 1003, 10);
        await Loop.InvokeAsync(async () =>
        {
            target.Position = source.Position.Clone(); target.Position.Z += 5;
            await _app.Services.GetRequiredService<BuffService>().ReconcileEnergyAsync(source);
            source.Energy[140002].NextRecoverMs = MonsterService.NowMs + 60000;
            if (fullShield) target.Buffs.Add(new ActiveBuff(9873751, 0, MonsterService.NowMs + 60000, 60000, [],
                target.Role!.Id, ShieldRemaining: 1_000_000));
        });
        await NoteLeapCastAsync(bot, source, target.Role!.Id, target.Position, 11316, level, 0);
        for (int index = 1; index <= 4; index++)
        {
            await NoteLeapCastAsync(bot, source, target.Role.Id, target.Position, 11310, level, 1);
            Assert.Equal(index + 1, Assert.Single(source.Buffs, b => b.ConfigId == 1134301).PileCount);
        }
        long hp = target.CurrentHp ?? throw new InvalidOperationException("PvP HP required.");
        var row = new SkillConfig(combat.EffectiveSkillRow(source, 1131000 + level)!.Value);
        long expected = (long)Math.Round(Character.Stats(source)[103] * row.DamageParam1[0] / 10000d);
        // JobResonanceActiveConfig[2]要求1131005+1131605：本測試滿級來源啟用3，未學目標仍為1。
        // JobResonanceRestraintConfig[3]._iJobProfess1=11000；同職業不表示同一流派。
        Assert.Equal(level == 5 ? 3 : 1, combat.ResonanceOf(source));
        Assert.Equal(1, combat.ResonanceOf(target));
        long resonanceRatio = combat.Resonance.Multiplier(combat.ResonanceOf(source), combat.ResonanceOf(target));
        Assert.Equal(level == 5 ? 11000 : 10000, resonanceRatio);
        Assert.Equal(0, combat.TotemRestraints.Bonus(source.Progress!.Career, target.Progress!.Career));
        expected = expected * resonanceRatio / 10000;
        var hit = await NoteLeapCastAsync(bot, source, target.Role.Id, target.Position, 11310, level, 4);
        var dst = Assert.Single(hit.Dsts, d => d.DstID == target.Role.Id);
        Assert.Equal(fullShield ? 0 : expected, dst.Damage);
        if (fullShield) Assert.Equal(expected, dst.SheildDamage);
        Assert.Equal(hp - (fullShield ? 0 : expected), target.CurrentHp); Assert.Equal(0, source.Energy[140003].Value);
        Assert.DoesNotContain(source.Buffs, b => b.ConfigId == 1134301);
    }

    [ClientDataTheory]
    [InlineData("stage")]
    [InlineData("plan")]
    [InlineData("job")]
    [InlineData("generation")]
    [InlineData("epoch")]
    [InlineData("expiry")]
    public async Task NoteLeapRevocationOrExpiredLifetimeCannotAuthorizeUltimate(string boundary)
    {
        var f = await NoteLeapFixtureAsync(1); await using var bot = f.Bot;
        var combat = _app.Services.GetRequiredService<CombatService>();
        await ArmNoteLeapAsync(bot, f.Source, f.Target, 1);
        if (boundary == "stage") await EmblemMarksAsync(f.Source, 1003, 9);
        else if (boundary == "plan") await EmblemOwnedAsync(f.Source, async () =>
        {
            f.Source.Emblems.CurrentPlan = 1;
            await _app.Services.GetRequiredService<EmblemService>().SyncGenrePlanAsync(f.Source, f.Source.EmblemPower);
        });
        else if (boundary == "job") await Gm("noteleap", "!job 1000");
        else if (boundary == "expiry") await Task.Delay(3100);
        else await Loop.InvokeAsync(() =>
        {
            if (boundary == "generation") f.Source.SceneGeneration++;
            else f.Source.SkillProtocol = new CombatService.ProtocolState();
            return Task.CompletedTask;
        });
        Assert.NotEqual(4, new SkillConfig(combat.EffectiveSkillRow(f.Source, 1131001)!.Value).Mode);
        Assert.Null(combat.EffectiveSkillRow(f.Source, 1131041));
        if (boundary is "stage" or "plan" or "job")
            Assert.DoesNotContain(f.Source.Buffs, b => b.ConfigId == 1134301);
        if (boundary == "stage")
        {
            await EmblemMarksAsync(f.Source, 1003, 10);
            Assert.NotEqual(4, new SkillConfig(combat.EffectiveSkillRow(f.Source, 1131001)!.Value).Mode);
        }
    }

    [ClientDataFact]
    public async Task NoteLeapMissingStageDoesNotStartAndExtraHitCannotAddNotes()
    {
        var f = await NoteLeapFixtureAsync(1, 9); await using var bot = f.Bot;
        var combat = _app.Services.GetRequiredService<CombatService>();
        await NoteLeapCastAsync(bot, f.Source, f.Target.Uid, f.Target.Pos, 11316, 1, 0);
        Assert.DoesNotContain(f.Source.Buffs, b => b.ConfigId == 1134301);
        await EmblemMarksAsync(f.Source, 1003, 10);
        await NoteLeapCastAsync(bot, f.Source, f.Target.Uid, f.Target.Pos, 11316, 1, 0);
        await Loop.InvokeAsync(() => combat.CastExtraAsync(f.Source,
            combat.EffectiveSkillRow(f.Source, 1131001)!.Value, [f.Target], parametersApplied: true));
        Assert.Equal(1, Assert.Single(f.Source.Buffs, b => b.ConfigId == 1134301).PileCount);
        Assert.Equal(5, f.Source.Energy[140003].Value);
    }

    [ClientDataFact]
    public async Task NoteLeapMissDoesNotCountAndOlderCallbackCannotCountAgainAfterANewerCast()
    {
        var f = await NoteLeapFixtureAsync(1); await using var bot = f.Bot;
        var combat = _app.Services.GetRequiredService<CombatService>();
        await NoteLeapCastAsync(bot, f.Source, f.Target.Uid, f.Target.Pos, 11316, 1, 0);
        combat.CastDamageOverrides.Add((s, _, _, _) => s == f.Source ? (0, DamageHitResult.Miss) : null);
        var miss = await NoteLeapCastAsync(bot, f.Source, f.Target.Uid, f.Target.Pos, 11310, 1, 1);
        Assert.Equal(DamageHitResult.Miss, Assert.Single(miss.Dsts).HitResult);
        Assert.Equal(1, Assert.Single(f.Source.Buffs, b => b.ConfigId == 1134301).PileCount);
        combat.CastDamageOverrides.Clear();
        var hit = await NoteLeapCastAsync(bot, f.Source, f.Target.Uid, f.Target.Pos, 11310, 1, 1);
        ActiveSkillCast Cast(long id) => new()
        {
            Id = id, OwnerId = f.Source.Role!.Id, WorldScene = f.Source.WorldScene, SceneGeneration = f.Source.SceneGeneration,
            Attachment = f.Source.Attachment, PassiveSourceEpoch = f.Source.SkillProtocol,
            Request = new CastSkillReq { SkillId = 1131011, TargetId = f.Target.Uid },
            Row = _app.Services.GetRequiredService<GameData>().Row("SkillConfig", 1131011)!.Value,
            Origin = f.Source.Position.Clone(), StartMs = MonsterService.NowMs, ChantMs = 0, ActionMs = 1200,
        };
        var first = Cast(90001); var second = Cast(90002);
        var passives = _app.Services.GetRequiredService<PassiveSkills>();
        await Loop.InvokeAsync(async () =>
        {
            await passives.NoteLeapHitAsync(f.Source, first, [f.Target], hit);
            await passives.NoteLeapHitAsync(f.Source, second, [f.Target], hit);
            Assert.Equal(4, Assert.Single(f.Source.Buffs, b => b.ConfigId == 1134301).PileCount);
            long expiry = f.Source.Buffs.Single(b => b.ConfigId == 1134301).ExpireMs;
            await passives.NoteLeapHitAsync(f.Source, first, [f.Target], hit);
            Assert.Equal(4, Assert.Single(f.Source.Buffs, b => b.ConfigId == 1134301).PileCount);
            Assert.Equal(expiry, f.Source.Buffs.Single(b => b.ConfigId == 1134301).ExpireMs);
        });
    }

    [ClientDataTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoteLeapDeathCleanupCannotTouchANewerEpochOrNoteState(bool invalidate)
    {
        var f = await NoteLeapFixtureAsync(1); await using var bot = f.Bot;
        var combat = _app.Services.GetRequiredService<CombatService>();
        await NoteLeapCastAsync(bot, f.Source, f.Target.Uid, f.Target.Pos, 11316, 1, 0);
        var death = combat.CaptureNoteLeapDeath?.Invoke(f.Source)
            ?? throw new InvalidOperationException("Note death capture hook required.");
        if (invalidate)
        {
            await Loop.InvokeAsync(() => { f.Source.SkillProtocol = new(); return Task.CompletedTask; });
            await NoteLeapCastAsync(bot, f.Source, f.Target.Uid, f.Target.Pos, 11316, 1, 0);
        }
        await Loop.InvokeAsync(async () =>
        {
            f.Source.Dead = true; await death(); await death(); f.Source.Dead = false;
        });
        Assert.Equal(invalidate ? 1 : 0, f.Source.Buffs.Count(b => b.ConfigId == 1134301));
        if (!invalidate)
        {
            await NoteLeapCastAsync(bot, f.Source, f.Target.Uid, f.Target.Pos, 11316, 1, 0);
            await Loop.InvokeAsync(() => combat.EnvironmentDamageAsync(f.Source, 10_000_000));
            Assert.True(f.Source.Dead); Assert.DoesNotContain(f.Source.Buffs, b => b.ConfigId == 1134301);
        }
    }

    [ClientDataFact]
    public async Task NoteLeapFullShieldHitCountsAndMixedTargetsShareTheAuthoredFiveAndTenLimits()
    {
        var f = await NoteLeapFixtureAsync(1); await using var bot = f.Bot;
        await using var enemy = await InWorld("NoteShield"); var player = Session("noteshield");
        await Gm("noteshield", "!job 1400");
        await CardOffenceRoom((bot, f.Source), (enemy, player));
        var combat = _app.Services.GetRequiredService<CombatService>(); var world = _app.Services.GetRequiredService<World>();
        var monsters = new List<MonsterUnit>();
        for (int i = 0; i < 11; i++)
        {
            var monster = await Target("noteleap", stationary: true, immobile: true);
            await CardOffenceDummyAsync(monster); monsters.Add(monster);
        }
        await Loop.InvokeAsync(async () =>
        {
            foreach (var other in world.Map(f.Source.WorldScene).Units.OfType<MonsterUnit>().Except(monsters).ToArray())
                await world.RemoveUnitAsync(other);
            player.Position = f.Source.Position.Clone(); player.Position.Z += 5;
            player.Buffs.Add(new ActiveBuff(9873751, 0, MonsterService.NowMs + 60000, 60000, [], player.Role!.Id,
                ShieldRemaining: 1_000_000));
            combat.SetEventPvPHandlers((s, t) => s == f.Source && t == player, (_, _) => Task.CompletedTask);
            for (int index = 0; index < monsters.Count; index++)
            {
                monsters[index].X = f.Source.Position.X + (index == 9 ? 5.1f : 0);
                monsters[index].Z = f.Source.Position.Z + (index == 10 ? -.1f : 5 + index * .1f);
                monsters[index].Y = f.Source.Position.Y; world.Map(f.Source.WorldScene).Moved(monsters[index]);
            }
            await _app.Services.GetRequiredService<BuffService>().ReconcileEnergyAsync(f.Source);
            f.Source.Energy[140002].NextRecoverMs = MonsterService.NowMs + 60000;
        });
        await NoteLeapCastAsync(bot, f.Source, player.Role!.Id, player.Position, 11316, 1, 0);
        for (int index = 1; index <= 5; index++)
        {
            var hit = await NoteLeapCastAsync(bot, f.Source, player.Role.Id, player.Position, 11310, 1, index == 5 ? 4 : 1);
            int expectedCount = index == 5 ? 10 : 5;
            var packets = new List<SkillHitInfo> { hit };
            while (packets.Sum(h => h.Dsts.Count) < expectedCount)
                packets.Add(await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == hit.SessionId));
            var all = packets.SelectMany(h => h.Dsts).ToArray();
            Assert.Equal(expectedCount, all.Select(d => d.DstID).Distinct().Count());
            Assert.Equal(all.Length, all.Select(d => d.DstID).Distinct().Count());
            var shield = Assert.Single(all, d => d.DstID == player.Role.Id);
            Assert.Equal(0, shield.Damage); Assert.True(shield.SheildDamage > 0);
            if (index < 5) Assert.Equal(index + 1, Assert.Single(f.Source.Buffs, b => b.ConfigId == 1134301).PileCount);
        }
        Assert.Equal(1_000_000, monsters[9].Hp); Assert.Equal(1_000_000, monsters[10].Hp);
        Assert.DoesNotContain(f.Source.Buffs, b => b.ConfigId == 1134301);
    }

    [ClientDataFact]
    public async Task NoteLeapRejectedAndInterruptedUltimateDoNotConsumeNotesOrCreateEnergy()
    {
        var f = await NoteLeapFixtureAsync(1); await using var bot = f.Bot;
        var combat = _app.Services.GetRequiredService<CombatService>();
        await ArmNoteLeapAsync(bot, f.Source, f.Target, 1);
        await Loop.InvokeAsync(async () =>
        {
            await combat.InterruptAsync(f.Source);
            f.Source.SkillCds[11310] = MonsterService.NowMs + 10000;
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1131001, TargetId = f.Target.Uid });
        Assert.NotEqual(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.Equal(5, Assert.Single(f.Source.Buffs, b => b.ConfigId == 1134301).PileCount);
        Assert.Equal(1, f.Source.Energy[140003].Value);
        await Loop.InvokeAsync(() =>
        {
            f.Source.SkillCds.Clear(); f.Source.SkillModeCds.Clear(); f.Source.SkillPublicCds.Clear();
            return Task.CompletedTask;
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        { SkillId = 1131001, TargetId = f.Target.Uid, TargetPos = f.Target.Pos.Clone(), Face = 0 });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Loop.InvokeAsync(() => combat.InterruptAsync(f.Source));
        await Task.Delay(400);
        Assert.Equal(5, Assert.Single(f.Source.Buffs, b => b.ConfigId == 1134301).PileCount);
        Assert.Equal(0, f.Source.Energy[140003].Value); // 已接受的原技能成本不因中斷退款。
    }

    [ClientDataTheory]
    [InlineData(1131001, 5, 10)]
    [InlineData(1131011, 10, 20)]
    [InlineData(1131041, 10, 20)]
    [InlineData(1131601, 6, 15)]
    public void NoteLeapSpearFamilyUsesForwardDescriptionRectangleRatherThanZeroRadius(int id, int width, int length)
    {
        var row = _app.Services.GetRequiredService<GameData>().Row("SkillConfig", id)!.Value;
        bool Contains(float x, float z) => CombatService.SpearAreaContains(row, new Vec3(), 0, new Vec3 { X = x, Z = z })
            ?? throw new InvalidOperationException("Spear shape owner required.");
        Assert.True(Contains(0, 5)); Assert.True(Contains(width / 2f, length));
        Assert.False(Contains(0, -.1f)); Assert.False(Contains(0, length + .1f));
        Assert.False(Contains(width / 2f + .1f, 5));
    }

    [ClientDataTheory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task NoteLeapNormalAndEnhancedSpearsReallyHitAheadAndExcludeTheRearAndOutsideWidth(int mode)
    {
        var f = await NoteLeapFixtureAsync(1, 9); await using var bot = f.Bot;
        if (mode == 1) await NoteLeapCastAsync(bot, f.Source, f.Target.Uid, f.Target.Pos, 11316, 1, 0);
        else await Loop.InvokeAsync(async () =>
        {
            Assert.True(await _app.Services.GetRequiredService<BuffService>().SetExistingEnergyValueAsync(f.Source, 140002, 5));
        });
        var excluded = new List<MonsterUnit>();
        foreach (var (forward, side) in new[] { (-.1f, 0f), (mode == 0 ? 10.1f : 20.1f, 0f), (5f, mode == 0 ? 2.6f : 5.1f) })
        {
            var target = await Target("noteleap", stationary: true, immobile: true);
            await CardOffenceDummyAsync(target);
            await Loop.InvokeAsync(() =>
            {
                target.X = f.Source.Position.X + side; target.Y = f.Source.Position.Y; target.Z = f.Source.Position.Z + forward;
                _app.Services.GetRequiredService<World>().Map(f.Source.WorldScene).Moved(target);
                return Task.CompletedTask;
            });
            excluded.Add(target);
        }
        var hit = await NoteLeapCastAsync(bot, f.Source, f.Target.Uid, f.Target.Pos, 11310, 1, mode);
        Assert.Equal(f.Target.Uid, Assert.Single(hit.Dsts).DstID);
        Assert.True(hit.Dsts[0].Damage > 0); Assert.All(excluded, m => Assert.Equal(1_000_000, m.Hp));
    }
}
