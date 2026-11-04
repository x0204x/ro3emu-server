using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private const int Dagger = 2010120, Bow = 2110120;

    private Task<DBSkillData> ShownSkills(string account) => Loop.InvokeAsync(() =>
        Task.FromResult(_app.Services.GetRequiredService<SkillService>().Shown(Session(account))));

    private static int ShownLevel(DBSkillData data, int group) =>
        data.SkillAssignPlans[data.CurrentPlanIndex].SkillAssigns.TryGetValue(group, out var skill) ? skill.SkillLevel : 0;

    /// <summary>The fixture way to hold a weapon, without the equip message.</summary>
    private Task HoldInMainHand(string account, int entry) => Loop.InvokeAsync(async () =>
    {
        // Under the character lock: a commit still running (a pendant lease) replaces the bag with its own staged copy.
        var session = Session(account);
        using (await CharacterService.Lock(session))
            session.Items.Add(new InvItem { Guid = 990002, Entry = entry, Stack = 1, Grid = 1 });
    });

    private async Task<long> Appraised(string account, int entry)
    {
        await Gm(account, $"!item {entry} 1");
        return await Loop.InvokeAsync(() =>
        {
            var item = Session(account).Items.Single(i => i.Entry == entry);
            (item.Attrs ??= [])[CharacterService.AttrAppraise] = 1;
            return Task.FromResult(item.Guid);
        });
    }

    private static async Task WearInMainHand(Ro3Emu.Bot.BotClient bot, long guid, int expectedAttack)
    {
        await bot.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq { ItemGUID = guid, Pos = (EquipGridType)1, Opr = (EquipItemOprType)0 });
        Assert.Equal(0, (await Next(bot, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);
        await Next(bot, SkillService.SkillDataSync, DBSkillDataIncrement.Parser, m => m.HasCurrentAtkSkillId && m.CurrentAtkSkillId == expectedAttack);
    }

    /// <summary>A Thief's attack follows the dagger, the plan shows one learned normal attack, and the other attack is refused.</summary>
    [ClientDataFact]
    public async Task AThiefWithADaggerIsToldItsDaggerAttackAndCannotCastTheOther()
    {
        const int BareHands = 1500001, DaggerAttack = 1005001;
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 5100");
        long dagger = await Appraised("alice", Dagger);
        Assert.Equal(BareHands, (await ShownSkills("alice")).CurrentAtkSkillId);

        await WearInMainHand(alice, dagger, DaggerAttack);
        var held = await ShownSkills("alice");
        Assert.Equal((DaggerAttack, DaggerAttack), (held.CurrentAtkSkillId, held.SkillWheel.AtkSkillId));
        Assert.Equal(1, ShownLevel(held, 10050));
        Assert.Equal(0, ShownLevel(held, 10000));
        Assert.Equal(0, ShownLevel(held, 15000));

        var target = await Target("alice", stationary: true);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = BareHands, TargetId = target.Uid });
        Assert.NotEqual(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = DaggerAttack, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
    }

    /// <summary>A bow's Arrow Attack is not _iIsNormalAtk: GetNormalSkill needs Normal Attack, which stays learned and castable.</summary>
    [ClientDataFact]
    public async Task ABowArchersNormalAttackStaysLearned()
    {
        const int ArrowAttack = 1300001, NormalAttackGroup = 10000, NormalAttack = 1000001;
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 3100");
        long bow = await Appraised("alice", Bow);
        await WearInMainHand(alice, bow, ArrowAttack);
        var held = await ShownSkills("alice");
        Assert.Equal(ArrowAttack, held.CurrentAtkSkillId);
        Assert.Equal(1, ShownLevel(held, NormalAttackGroup));
        var target = await Target("alice", stationary: true);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = NormalAttack, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
    }

    /// <summary>JobConfig names 1200001 for the Magician careers, which has no SkillConfig row, and 2210 has no JobAttackConfig row.</summary>
    [ClientDataTheory]
    [InlineData(2100)]
    [InlineData(2210)]
    public async Task AMagicianCareerHasANormalAttackWithARow(int career)
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", $"!job {career}");
        var skills = _app.Services.GetRequiredService<SkillService>();
        Assert.Equal(1000001, skills.NormalAttack(career));
        var shown = await ShownSkills("alice");
        Assert.Equal(1000001, shown.CurrentAtkSkillId);
        var target = await Target("alice", stationary: true);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = shown.CurrentAtkSkillId, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
    }
}
