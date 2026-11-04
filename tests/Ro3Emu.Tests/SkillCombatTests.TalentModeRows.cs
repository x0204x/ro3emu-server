using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Talents that switch a skill to a mode row: the row's script and second damage pair still do what the text says.</summary>
public sealed partial class SkillCombatTests
{
    private sealed record Strike(long Row, long Target, long Amount, long[] Ratios, long[] Flats);

    /// <summary>A level 99 caster of the job with the group learned at the level and the talent node bought, costs off,
    /// damage without its float and crits, no other monster alive in the scene, and every strike it deals on record.</summary>
    private async Task<(BotClient Bot, GameSession Me, List<Strike> Strikes)> Caster(string name, int job, int group, int level, int talent = 0)
    {
        var bot = await InWorld(name);
        string account = name.ToLowerInvariant();
        await Gm(account, "!lvl 99"); await Gm(account, $"!job {job}"); await Teach(account, group);
        if (talent != 0) await ChooseProfessionTalent(bot, account, talent);
        var me = Session(account);
        var world = _app.Services.GetRequiredService<World>();
        await Loop.InvokeAsync(async () =>
        {
            using var held = await CharacterService.Lock(me);
            me.Progress!.Skills[group] = level;
            foreach (var other in world.Map(me.WorldScene).Units.OfType<MonsterUnit>()) other.Hp = 0;
        });
        me.GmCombat |= GmCombatSwitches.Unlimited | GmCombatSwitches.FixedDamage;
        var combat = _app.Services.GetRequiredService<CombatService>();
        combat.Rolls = new CritRolls(1);
        var strikes = new List<Strike>();
        combat.DealtAsync.Add((source, monster, row, amount) =>
        {
            var skill = new SkillConfig(row);
            if (source == me) strikes.Add(new Strike(skill.Id, monster.Uid, amount, skill.DamageParam1, skill.DamageParam2));
            return Task.CompletedTask;
        });
        return (bot, me, strikes);
    }

    /// <summary>A stationary monster with HP to spare, the given metres ahead of the caster.</summary>
    private async Task<MonsterUnit> Mark(GameSession me, float metres, int race = 1, int element = 1)
    {
        var mark = await Target(me.Account!, stationary: true);
        await Loop.InvokeAsync(() =>
        {
            mark.Template = mark.Template with { Race = race, Element = element, MaxHp = 100_000_000 };
            mark.Hp = 100_000_000;
            mark.X = me.Position.X + metres;
            mark.Z = me.Position.Z;
            return Task.CompletedTask;
        });
        return mark;
    }

    /// <summary>Waits for what a timed cast does (a loaded machine can run late), then for anything it should not have done.</summary>
    private static async Task Eventually(Func<bool> done)
    {
        for (int i = 0; i < 80 && !done(); i++) await Task.Delay(100);
        await Task.Delay(300);
    }

    private static async Task<CastSkillRsp> Cast(BotClient bot, GameSession me, int skill, MonsterUnit target)
    {
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skill, TargetId = target.Uid, TargetPos = target.Pos.Clone(), Face = me.Face });
        return await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser);
    }

    /// <summary>The row the caster's talents make of a row id: its mode, with the tree's other talents applied.</summary>
    private SkillConfig Effective(GameSession me, int skill) =>
        new(_app.Services.GetRequiredService<CombatService>().EffectiveSkillRow(me, skill)!.Value);

    // Thunder Shockwave (10102131 for the Wizard line) selects Lord_of_Vermilion_BD: "lasts 4 seconds and strikes 7 times".
    // "For each enemy hit ... its DMG Bonus increases by 5%, up to 25%".
    [ClientDataTheory]
    [InlineData(1)]
    [InlineData(10)]
    public async Task LordOfVermilionKeepsItsStrikesWithThunderShockwave(int level)
    {
        var (bot, me, strikes) = await Caster("Alice", 2200, 12107, level, 10102131);
        await using var _ = bot;
        var mark = await Mark(me, 5);
        var row = Effective(me, 1210700 + level);
        Assert.Equal(1210710 + level, row.Id);
        Assert.Equal(0, (await Cast(bot, me, 1210700 + level, mark)).Result);
        await Task.Delay(3500);
        Assert.InRange(strikes.Count, 6, 7); // a late tick can lose the last under load
        Assert.All(strikes, s => Assert.Equal(row.Id, s.Row));
        // One enemy: +5%, and only once.
        Assert.All(strikes, s => Assert.Equal((long)Math.Round(row.DamageParam1[0] * 1.05), s.Ratios[0]));
        Assert.All(strikes, s => Assert.Equal((long)Math.Round(row.DamageParam2[0] * 1.05), s.Flats[0]));
    }

    [ClientDataFact] // "up to 25%": six enemies in the circle, five hit
    public async Task ThunderShockwaveStopsAtItsLimit()
    {
        var (bot, me, strikes) = await Caster("Alice", 2200, 12107, 10, 10102131);
        await using var _ = bot;
        var marks = new List<MonsterUnit>();
        for (int i = 0; i < 6; i++) marks.Add(await Mark(me, 5));
        var row = Effective(me, 1210710);
        Assert.Equal(0, (await Cast(bot, me, 1210710, marks[0])).Result);
        await Task.Delay(1000);
        Assert.NotEmpty(strikes);
        Assert.All(strikes, s => Assert.Equal((long)Math.Round(row.DamageParam1[0] * 1.25), s.Ratios[0]));
    }

    // Revive [Buff Effect] (10102118 for the Priest line) selects AcolyteBD4: "Revive the designated dead ST ally".
    [ClientDataTheory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task ResurrectionKeepsWorkingWithItsTalent(int level)
    {
        var (bot, me, _) = await Caster("Alice", 4200, 14112, level, 10102118);
        await using var alice = bot;
        await using var bob = await InWorld("Bob");
        var b = Session("bob");
        await Loop.InvokeAsync(async () => { b.Dead = true; await Character.SetHpSpAsync(b, 0, 0); });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1411200 + level, TargetId = b.Role!.Id });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(bob, WorldCoreService.ReliveInvitation, EntityReliveReceiveInviteNotify.Parser);
        await bob.SendGameAsync(WorldCoreService.ReliveAccept, new EntityReliveInviteAcceptReq { SourceEntityID = me.Role!.Id, IsAccept = true });
        Assert.Equal(0, (await Next(bob, WorldCoreService.ReliveAccept, EntityReliveInviteAcceptResp.Parser)).Result);
        await Next(bob, CombatService.ReviveInfo, SyncClientPlayerReviveInfoNtf.Parser, p => !p.BDead);
        Assert.False(b.Dead);
    }

    // "When [Resurrection] Revives a target below Base 90, its HP and SP recovery are increased by 10%": the mode row's
    // _kDamageParamExtra lists the raised pair first (level 1: 4000 then 3000; level 4: 7000 then 6000). Without the talent
    // the row has one pair, whatever the target's level.
    [ClientDataTheory]
    [InlineData(1, 50, true, 4000)]
    [InlineData(1, 99, true, 3000)]
    [InlineData(4, 50, true, 7000)]
    [InlineData(4, 99, true, 6000)]
    [InlineData(1, 50, false, 3000)]
    [InlineData(4, 50, false, 6000)]
    public async Task ResurrectionRestoresWhatItsTextSays(int level, int allyLevel, bool talent, int rate)
    {
        var (bot, me, _) = await Caster("Alice", 4200, 14112, level, talent ? 10102118 : 0);
        await using var alice = bot;
        await using var bob = await InWorld("Bob");
        await Gm("bob", $"!lvl {allyLevel}");
        var b = Session("bob");
        await Loop.InvokeAsync(async () => { b.Dead = true; await Character.SetHpSpAsync(b, 0, 0); });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1411200 + level, TargetId = b.Role!.Id });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(bob, WorldCoreService.ReliveInvitation, EntityReliveReceiveInviteNotify.Parser);
        Assert.Equal(rate, b.WorldState.ReviveHpRate);
        Assert.Equal(rate, b.WorldState.ReviveSpRate);
        await bob.SendGameAsync(WorldCoreService.ReliveAccept, new EntityReliveInviteAcceptReq { SourceEntityID = me.Role!.Id, IsAccept = true });
        Assert.Equal(0, (await Next(bob, WorldCoreService.ReliveAccept, EntityReliveInviteAcceptResp.Parser)).Result);
        await Next(bob, CombatService.ReviveInfo, SyncClientPlayerReviveInfoNtf.Parser, p => !p.BDead);
        Assert.Equal(rate / 100.0, Character.Stats(b)[2000] * 100.0 / Character.Stats(b)[101], 0);
    }

    [Theory] // The pure rule on invented numbers: the first pair for a raised target, the last otherwise.
    [InlineData(true, 500, 600)]
    [InlineData(false, 300, 400)]
    public void ReviveRatesTakeTheRaisedPairFirst(bool raised, long hp, long sp)
    {
        using var row = System.Text.Json.JsonDocument.Parse("""{"_kDamageParamExtra":[[2,500,101,3],[2,600,102,3],[2,300,101,3],[2,400,102,3]]}""");
        Assert.Equal((hp, sp), WorldCoreService.ReviveRates(row.RootElement, raised));
    }

    // Holy Healing Light (10104106 for the Priest line) selects AcolyteBD11, with Heal's own text: "When used on an
    // Undead-property enemy, deals ... Magic ATK * 41% + 30" (level 10: 50% + 40), not the heal's 90% + 135.
    [ClientDataTheory]
    [InlineData(1)]
    [InlineData(10)]
    public async Task HealHurtsTheUndeadByItsEnemyBranchWithTheTalent(int level)
    {
        var (bot, me, strikes) = await Caster("Alice", 4300, 14007, level, 10104106);
        await using var _ = bot;
        var mark = await Mark(me, 3, race: 5, element: 10);
        Assert.Equal(0, (await Cast(bot, me, 1400700 + level, mark)).Result);
        await Eventually(() => strikes.Count > 0);
        Assert.Equal(1400710 + level, Assert.Single(strikes).Row);
        // The same caster on the plain row: the talent only swaps the row, so the two hits are equal.
        await Loop.InvokeAsync(() => { me.TalentSkillModifiers = []; return Task.CompletedTask; });
        Assert.Equal(0, (await Cast(bot, me, 1400700 + level, mark)).Result);
        await Eventually(() => strikes.Count > 1);
        Assert.Equal(2, strikes.Count);
        Assert.Equal(1400700 + level, strikes[1].Row);
        Assert.Equal(strikes[1].Amount, strikes[0].Amount);
    }

    // Flame Attack Enhance (10102129 for the Knight line, bought on the way to 10102130): "extends the duration of Magnum
    // Break's buff to 120 sec." Its tuple names the duration column: [21,0,0,11002,33,2,120000,0,2].
    [ClientDataTheory]
    [InlineData(1)]
    [InlineData(10)]
    public async Task FlameAttackEnhanceExtendsMagnumBreaksBuff(int level)
    {
        var (bot, me, _) = await Caster("Alice", 1200, 11002, level, 10102130);
        await using var _ = bot;
        var mark = await Mark(me, 2);
        Assert.Equal(0, (await Cast(bot, me, 1100200 + level, mark)).Result);
        await Eventually(() => me.Buffs.Any(b => b.ConfigId == 1100200 + level));
        var buff = Assert.Single(me.Buffs, b => b.ConfigId == 1100200 + level);
        Assert.InRange(buff.ExpireMs - MonsterService.NowMs, 110_000, 120_000);
    }

    // Critical Attack (10102106 for the Knight line) selects BowlingBashBD01, [ratio, 5000] / [flat, 400]: "When [Bowling
    // Bash] hits only 1 enemy, it Deals DMG to that enemy equal to an Extra PATK * 50% + 400".
    [ClientDataTheory]
    [InlineData(1, 9000, 210)]
    [InlineData(10, 18000, 300)]
    public async Task BowlingBashDealsItsExtraOnALoneEnemy(int level, int ratio, int flat)
    {
        var (bot, me, strikes) = await Caster("Alice", 1200, 11104, level, 10102106);
        await using var _ = bot;
        var mark = await Mark(me, 3);
        Assert.Equal(0, (await Cast(bot, me, 1110400 + level, mark)).Result);
        await Eventually(() => strikes.Count > 1);
        Assert.Equal(2, strikes.Count);
        Assert.Equal<long[]>([ratio, 5000], strikes[0].Ratios);
        Assert.Equal<long[]>([flat, 400], strikes[0].Flats);
        Assert.Equal<long[]>([5000], strikes[1].Ratios);
        Assert.Equal<long[]>([400], strikes[1].Flats);
        Assert.Equal(mark.Uid, strikes[1].Target);
    }

    [ClientDataFact] // "hits only 1 enemy": two in the circle take no extra
    public async Task BowlingBashDealsNoExtraOnTwoEnemies()
    {
        var (bot, me, strikes) = await Caster("Alice", 1200, 11104, 1, 10102106);
        await using var _ = bot;
        var mark = await Mark(me, 3);
        await Mark(me, 3);
        Assert.Equal(0, (await Cast(bot, me, 1110401, mark)).Result);
        await Eventually(() => strikes.Count > 1);
        Assert.Equal(2, strikes.Count);
        Assert.All(strikes, s => Assert.Equal(9000, s.Ratios[0]));
    }

    // Rapid Throw (10102114 for the Knight line) selects BoomerangBD01, [ratio, 5000] / [flat, 220]: "you throw one Extra
    // Spear that Deals DMG equal to PATK * 50% + 220".
    [ClientDataTheory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task SpearThrowThrowsItsExtraSpear(int level)
    {
        var (bot, me, strikes) = await Caster("Alice", 1200, 11110, level, 10102114);
        await using var _ = bot;
        var mark = await Mark(me, 3);
        var row = Effective(me, 1111000 + level);
        Assert.Equal(1111010 + level, row.Id);
        Assert.Equal(0, (await Cast(bot, me, 1111000 + level, mark)).Result);
        await Eventually(() => strikes.Count > 1);
        Assert.Equal(2, strikes.Count);
        Assert.Equal(row.DamageParam1, strikes[0].Ratios);
        Assert.Equal<long[]>([5000], strikes[1].Ratios);
        Assert.Equal<long[]>([220], strikes[1].Flats);
    }

    // Joint Destruction (10104102 for the Knight line) selects QiaodaBD02: "simultaneously inflicts [Break Ankle], [Break
    // Wrist], [Break Waist], and [Break Neck] on the target", 8 s by the row's _kLogicData [1122101,10000,8000].
    [ClientDataTheory]
    [InlineData(1)]
    [InlineData(10)]
    public async Task JointBeatBreaksEverythingAtOnceWithItsTalent(int level)
    {
        var (bot, me, _) = await Caster("Alice", 1300, 11221, level, 10104102);
        await using var _ = bot;
        var mark = await Mark(me, 3);
        Assert.Equal(0, (await Cast(bot, me, 1122100 + level, mark)).Result);
        var buffs = _app.Services.GetRequiredService<BuffService>();
        await Eventually(() => buffs.UnitBuffs(mark).Count > 0);
        var live = buffs.UnitBuffs(mark);
        foreach (int row in new[] { 1122201, 1122301, 1122401, 1122501 })
            Assert.InRange(Assert.Single(live, b => b.ConfigId == row).ExpireMs - MonsterService.NowMs, 5000, 8000);
        Assert.Equal(4, Assert.Single(live, b => b.ConfigId == 1122101).PileCount);
        var attributes = buffs.UnitCombatAttributes(mark).ToList();
        Assert.Contains((1052L, -500d), attributes); // "reduces PDEF by 5%"
        Assert.Contains((1216L, -500d), attributes); // "reduces DMG Reduction by 5%"
    }

    // Holy Cross Judgment (10102115 for the Priest line): "When [Magnus Exorcismus] is cast, it also unleashes ... MATK*5%+470
    // ... to up to 4 enemies within the area." Its row 1420901 holds [500] / [470].
    [ClientDataTheory]
    [InlineData(1)]
    [InlineData(10)]
    public async Task MagnusExorcismusBurstsOnceOnFourEnemies(int level)
    {
        var (bot, me, strikes) = await Caster("Alice", 4200, 14109, level, 10102115);
        await using var _ = bot;
        var marks = new List<MonsterUnit>();
        for (int i = 0; i < 5; i++) marks.Add(await Mark(me, 5));
        Assert.Equal(0, (await Cast(bot, me, 1410900 + level, marks[0])).Result);
        await Task.Delay(5500);
        var bursts = strikes.Where(s => s.Row == 1420901).ToList();
        Assert.Equal(4, bursts.Count);
        Assert.Equal(4, bursts.Select(s => s.Target).Distinct().Count());
        Assert.All(bursts, s => { Assert.Equal<long[]>([500], s.Ratios); Assert.Equal<long[]>([470], s.Flats); });
        Assert.Contains(strikes, s => s.Row != 1420901); // the field itself still strikes
    }

    [ClientDataFact] // nobody in the area, nobody to burst on, and no target left over from elsewhere
    public async Task MagnusExorcismusDoesNotBurstOutsideItsArea()
    {
        var (bot, me, strikes) = await Caster("Alice", 4200, 14109, 1, 10102115);
        await using var _ = bot;
        var far = await Mark(me, 8);
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1410901, TargetPos = me.Position.Clone(), Face = me.Face });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Task.Delay(1000);
        Assert.DoesNotContain(strikes, s => s.Target == far.Uid);
    }

    // Sonic Burst: "Clears all current [Rain of Spears] charges and grants 5 charges of [Hundred Spear - Enhance]."
    [ClientDataFact]
    public async Task SonicBurstTradesRainChargesForEnhanceCharges()
    {
        var (bot, me, strikes) = await Caster("Alice", 1400, 11316, 1);
        await using var _ = bot;
        await Teach("alice", 11310);
        me.GmCombat &= ~GmCombatSwitches.Unlimited; // what this measures is the cost
        var mark = await Mark(me, 5);
        await Loop.InvokeAsync(() => { me.Energy[140002].Value = 5; return Task.CompletedTask; });
        Assert.Equal(0, (await Cast(bot, me, 1131601, mark)).Result);
        await Eventually(() => strikes.Count > 0);
        Assert.Equal(0, me.Energy[140002].Value);
        Assert.Equal(5, me.Energy[140003].Value);
        for (int left = 4; left >= 0; left--)
        {
            Assert.Equal(0, (await Cast(bot, me, 1131001, mark)).Result);
            Assert.Equal(left, me.Energy[140003].Value);
            await Task.Delay(1500);
            Assert.Equal(1131011, strikes[^1].Row);
        }
        // (inferred) The storm ends with its charges: Rain of Spears is back on its own row and its own charges.
        Assert.DoesNotContain(me.Buffs, b => b.ConfigId == 1132401);
        Assert.Equal(ErrorCodes.SkillEnergyShort, (await Cast(bot, me, 1131001, mark)).Result);
        await Loop.InvokeAsync(() => { me.Energy[140002].Value = 1; return Task.CompletedTask; });
        Assert.Equal(0, (await Cast(bot, me, 1131001, mark)).Result);
        await Task.Delay(1500);
        Assert.Equal(1131001, strikes[^1].Row);
    }
}
