using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// Blitz Beat's own BuffConfig row [1000, 13110]: "When using a Basic Attack, the Falcon has a ${8}% Chance to cast [Blitz
/// Beat] on the enemy". Talent tree 10102 on the Hunter line: 10102114 Falcon Strike and 10102115 Rending Lightning Strike
/// under Lightning Strike Training, 10102126 Thunder Shockwave and 10102127 Lightning Blitz under Enhanced Lightning Strike.
/// </summary>
public sealed partial class SkillCombatTests
{
    private const int FalconLease = 1500100, AutoBlitzRow = 1311005, AutoBlitzRendingRow = 1311015, FalconStrikeRow = 1321401;

    private async Task<MonsterUnit> FalconHunter(string name, bool falcon = true)
    {
        await Gm(name, "!lvl 99"); await Gm(name, "!job 3400"); await Teach(name, 13101);
        await Loop.InvokeAsync(async () =>
        {
            var s = Session(name);
            using (await CharacterService.Lock(s)) s.Progress!.Skills[13102] = 5;
            if (falcon) await _app.Services.GetRequiredService<CareerSpecialService>().GmLeaseAsync(s, FalconLease, mount: false, ride: false);
        });
        if (falcon) Assert.Equal(FalconLease, Session(name).CareerSpecial!.PendantSlot.CartRemouldCfg);
        var monster = await Target(name, stationary: true);
        await Loop.InvokeAsync(() => { monster.Hp = 1_000_000; return Task.CompletedTask; });
        return monster;
    }

    private void RollAtChance(int roll) =>
        _app.Services.GetRequiredService<TalentProcs>().Roll = bound => bound == 10000 ? roll : bound - 1;

    private static async Task BasicAttack(BotClient bot, MonsterUnit monster)
    {
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = ArcherAttack, TargetId = monster.Uid });
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser);
        // The swing before it may still be playing.
        for (int tries = 0; accepted.Result == ErrorCodes.SkillBusy && tries < 30; tries++)
        {
            await Task.Delay(200);
            await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = ArcherAttack, TargetId = monster.Uid });
            accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser);
        }
        Assert.Equal(0, accepted.Result);
        await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SessionId == accepted.SessionId);
    }

    private static int HitsOf(BotClient bot, int skill) => bot.Inbox.Snapshot().Count(e => e.IsGame(CombatService.SkillHit)
        && SkillHitInfo.Parser.ParseFrom(e.Body).SkillId == skill);

    [ClientDataFact]
    public async Task AutoBlitzBeatFiresOnABasicAttackAtItsOwnRowsChance()
    {
        await using var alice = await InWorld("Alice");
        var monster = await FalconHunter("alice");

        RollAtChance(999);
        await BasicAttack(alice, monster);
        var auto = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SkillId == AutoBlitzRow);
        Assert.Contains(auto.Dsts, d => d.DstID == monster.Uid && d.Damage > 0);

        RollAtChance(1000);
        await BasicAttack(alice, monster);
        await Task.Delay(300);
        Assert.Equal(1, HitsOf(alice, AutoBlitzRow));
    }

    // Thunder Shockwave's ["0", "13102", "4", "1", "3000", "0"]: "Increases the Chance for [Arrow Attack] to trigger [Blitz
    // Beat] by ${1}%", 10% + 30%. Its branch, Enhanced Lightning Strike, adds its 13110 damage tuples.
    [ClientDataFact]
    public async Task ThunderShockwaveRaisesAutoBlitzBeatToFortyPercent()
    {
        await using var alice = await InWorld("Alice");
        var monster = await FalconHunter("alice");
        await ChooseProfessionTalent(alice, "alice", 10102126);

        RollAtChance(3999);
        await BasicAttack(alice, monster);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SkillId == AutoBlitzRow);

        RollAtChance(4000);
        await BasicAttack(alice, monster);
        await Task.Delay(300);
        Assert.Equal(1, HitsOf(alice, AutoBlitzRow));

        var row = SkillConfig.Get(_app.Services.GetRequiredService<GameData>(), AutoBlitzRow)!.Value.Row;
        var effective = _app.Services.GetRequiredService<CombatService>().ExtraRow(Session("alice"), row);
        Assert.Equal(550, new SkillConfig(effective).DamageParam1[0]); // 5% of 100%, plus Stat Boost's 50
    }

    [ClientDataFact]
    public async Task AutoBlitzBeatNeedsTheFalcon()
    {
        await using var alice = await InWorld("Alice");
        var monster = await FalconHunter("alice", falcon: false);

        RollAtChance(0);
        await BasicAttack(alice, monster);
        await Task.Delay(300);
        Assert.Equal(0, HitsOf(alice, AutoBlitzRow));
    }

    // "... to the designated ST enemy ... and up to ${3} Other enemies within ${2} meters of the target."
    [ClientDataFact]
    public async Task AutoBlitzBeatHitsItsDescribedArea()
    {
        await using var alice = await InWorld("Alice");
        var monster = await FalconHunter("alice");
        for (int i = 0; i < 3; i++)
        {
            var other = await Target("alice", stationary: true);
            await Loop.InvokeAsync(() => { other.Hp = 1_000_000; return Task.CompletedTask; });
        }

        RollAtChance(0);
        await BasicAttack(alice, monster);
        var auto = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SkillId == AutoBlitzRow);
        Assert.Equal(3, auto.Dsts.Select(d => d.DstID).Distinct().Count());
        Assert.Contains(auto.Dsts, d => d.DstID == monster.Uid);
    }

    // Rending Lightning Strike's [13110, 15, add, 1] selects the automatic row's mode 1: radius 4 m, 3 others.
    [ClientDataFact]
    public async Task RendingLightningStrikeSwitchesAutoBlitzBeatToItsModeRow()
    {
        await using var alice = await InWorld("Alice");
        var monster = await FalconHunter("alice");
        await ChooseProfessionTalent(alice, "alice", 10102115);
        for (int i = 0; i < 4; i++)
        {
            var other = await Target("alice", stationary: true);
            await Loop.InvokeAsync(() => { other.Hp = 1_000_000; return Task.CompletedTask; });
        }

        RollAtChance(0);
        await BasicAttack(alice, monster);
        var auto = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SkillId == AutoBlitzRendingRow);
        Assert.Equal(4, auto.Dsts.Select(d => d.DstID).Distinct().Count());
    }

    // "When [Blitz Beat] hits an enemy ... Internal Cooldown: ${4} seconds" (0.2 s) on a skill with a 10 s cooldown:
    // the automatic casts count.
    [ClientDataFact]
    public async Task FalconStrikeFollowsAnAutomaticBlitzBeat()
    {
        await using var alice = await InWorld("Alice");
        var monster = await FalconHunter("alice");
        await ChooseProfessionTalent(alice, "alice", 10102114);

        RollAtChance(0);
        await BasicAttack(alice, monster);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SkillId == AutoBlitzRow);
        var strike = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SkillId == FalconStrikeRow);
        Assert.Contains(strike.Dsts, d => d.DstID == monster.Uid && d.Damage > 0);
    }

    // "After casting [Blitz Beat], ASPD increases by ${1}% for ${2} seconds. Internal Cooldown: ${3} seconds." (1 s)
    [ClientDataFact]
    public async Task LightningBlitzFollowsAnAutomaticBlitzBeat()
    {
        await using var alice = await InWorld("Alice");
        var monster = await FalconHunter("alice");
        await ChooseProfessionTalent(alice, "alice", 10102127);

        RollAtChance(0);
        await BasicAttack(alice, monster);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SkillId == AutoBlitzRow);
        await Task.Delay(300);
        Assert.True(await Loop.InvokeAsync(() => Task.FromResult(Session("alice").Buffs.Any(b => b.ConfigId == 1322501))));
    }

    // Lightning Strike Training's SpecialEffect [19, 13102, 5, 5, 5]: "At [Blitz Beat] Lv. ${1}, DEX +${2}."
    [ClientDataFact]
    public async Task LightningStrikeTrainingAddsItsDexterityAtBlitzBeatLevelFive()
    {
        await using var alice = await InWorld("Alice");
        await FalconHunter("alice");
        await Loop.InvokeAsync(() => { Character.Refresh(Session("alice")); return Task.CompletedTask; });
        long before = await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(Session("alice"))[5]));
        await ChooseProfessionTalent(alice, "alice", 10102114);
        Assert.Equal(before + 5, await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(Session("alice"))[5])));
    }
}
