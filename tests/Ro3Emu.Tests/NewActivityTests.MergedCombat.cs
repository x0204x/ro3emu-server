using Microsoft.Data.Sqlite;
using Romsg;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class NewActivityTests
{
    [ClientDataFact]
    public async Task LethalTrainingHitRestoresDummyWithoutDeathOrOrdinaryReward()
    {
        await using var bot = await InWorld("TrainingLethal");
        await Next(bot, TrainingService.Sync, DBTrainingGroundInfoIncrement.Parser);
        await Gm(bot, "!lvl 99");
        await Gm(bot, "!job 1100");
        await bot.SendGameAsync(DungeonService.Enter, new EnterDungeonReq { SceneEntry = 2100009 });
        Assert.Equal(0, (await Next(bot, DungeonService.Enter, EnterDungeonRsp.Parser)).Result);
        long room = await LoadPrivate(bot, 2100009);
        var actor = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(Service<World>()
            .AllUnits().OfType<MonsterUnit>().Single(m => m.InstanceId == room)));
        await OnLoop(() => { actor.Hp = 1; return Task.CompletedTask; });
        await bot.SendGameAsync(MsgId.Walk, new WalkReq { Pos = actor.Pos, BStop = true, SpeedRate = 1 });
        int normal = await Service<GameLoop>().InvokeAsync(() =>
            Task.FromResult(Service<SkillService>().NormalAttack(Session(bot))));
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = normal, TargetId = actor.Uid });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.Dsts.Any(d => d.DstID == actor.Uid && d.Damage > 0));
        Assert.False(Assert.Single(hit.Dsts, d => d.DstID == actor.Uid).IsDead);
        Assert.False(actor.Dead);
        Assert.Equal(actor.Template.MaxHp, actor.Hp);
        using var db = new SqliteConnection($"Data Source={Path.Combine(dataDir, "ro3emu.db")}");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT count(*) FROM operation_receipt WHERE command_kind='monster-kill'";
        Assert.Equal(0, (long)command.ExecuteScalar()!);
    }
}
