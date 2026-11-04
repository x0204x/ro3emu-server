using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private void IsolateDirectHealing() => Character.DynamicProperties.Add(_ =>
        [(107, 0L), (108, 0L), (1071, 0L), (1081, 0L)]);

    private async Task HealingStat(Ro3Emu.Bot.BotClient bot, int attr, long value)
    {
        await bot.SendGameAsync(GmCommands.GeneralProcGm, new GeneralGMProcReq
        { GmType = (GmOpEnum)33, ParamString = $"{bot.Self!.Uuid},{attr},{value}" });
        Assert.Equal(0, (await Next(bot, GmCommands.GeneralProcGm, GeneralGMProcRsp.Parser)).Result);
    }

    [ClientDataTheory]
    [InlineData(1, 50, 5550, 370)]
    [InlineData(10, 150, 10100, 1000)]
    public async Task HealingColuseoRealLevelsUseDexCapAndProvideAnExpiringShield(int level, int dex, long healing, long shield)
    {
        await using var caster = await InWorld("HealingColuseo");
        IsolateDirectHealing();
        await Gm("healingcoluseo", "!job 4300");
        await Teach("healingcoluseo", 14303, level);
        await HealingStat(caster, 5, dex);
        await HealingStat(caster, 104, 1000);
        await HealingStat(caster, 101, 100_000);
        await HealingStat(caster, 102, 10_000);
        var source = Session("healingcoluseo");
        var buffs = _app.Services.GetRequiredService<BuffService>();
        await Loop.InvokeAsync(() => Character.SetHpSpAsync(source, 1, 10_000));
        Assert.Equal(dex, Character.HealingStats(source)[5]);
        await caster.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1430300 + level });
        var accepted = await Next(caster, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var hit = await Next(caster, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        Assert.Equal(healing, Assert.Single(hit.Dsts).Damage);
        Assert.Equal(1 + healing, source.CurrentHp);
        Assert.Equal(shield, buffs.ShieldAmount(source));
        await Loop.InvokeAsync(async () =>
        {
            Assert.Equal(0, await buffs.AbsorbAsync(source, 100));
            Assert.Equal(shield - 100, buffs.ShieldAmount(source));
            int index = source.Buffs.FindIndex(b => b.ConfigId == 1430300 + level);
            source.Buffs[index] = source.Buffs[index] with { ExpireMs = MonsterService.NowMs - 1 };
            Assert.Equal(0, buffs.ShieldAmount(source));
            Assert.Equal(40, await buffs.AbsorbAsync(source, 40));
            Assert.DoesNotContain(source.Buffs, b => b.ConfigId == 1430300 + level);
        });
    }

    [ClientDataTheory]
    [InlineData(1, 1)]
    [InlineData(10, 10)]
    public async Task HealingRealLevelsApplyOrdinaryReceivedBuffAndBaseLevelToActualHp(int healLevel, int buffLevel)
    {
        await using var caster = await InWorld("HealingSource");
        await using var target = await InWorld("HealingTarget");
        IsolateDirectHealing();
        await Gm("healingsource", "!job 4100");
        await Gm("healingsource", "!lvl 50");
        await Teach("healingsource", 14007, healLevel);
        await HealingStat(caster, 104, 1000);
        await HealingStat(caster, 207, 1000);
        await HealingStat(caster, 1207, 500);
        await HealingStat(target, 101, 100_000);
        await HealingStat(target, 208, 2000);
        var source = Session("healingsource"); var recipient = Session("healingtarget");
        await Loop.InvokeAsync(async () =>
        {
            await Character.SetHpSpAsync(recipient, 1, null);
            await _app.Services.GetRequiredService<BuffService>().AddAsync(recipient, 1420100 + buffLevel, 120000, source.Role!.Id);
        });
        Assert.Equal(1000, Character.HealingStats(source)[104]);
        Assert.Equal(buffLevel * 100, Character.HealingStats(recipient)[1208]);
        long baseAmount = healLevel == 1 ? 1035 : 1180;
        long levelAmount = (long)Math.Floor(baseAmount * 1.05);
        long expected = (long)Math.Floor(levelAmount * 1.15 * (1.2 + buffLevel / 100d));
        await caster.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        { SkillId = 1400700 + healLevel, TargetId = recipient.Role!.Id });
        var accepted = await Next(caster, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var hit = await Next(caster, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        Assert.Equal(expected, Assert.Single(hit.Dsts).Damage);
        Assert.Equal(1 + expected, recipient.CurrentHp);
    }

    [ClientDataFact]
    public async Task HealingExpiredOrdinaryBuffAndTotemPercentAreNotAppliedTwice()
    {
        await using var caster = await InWorld("HealingTotem");
        await using var target = await InWorld("HealingExpiry");
        IsolateDirectHealing();
        await Gm("healingtotem", "!job 4100");
        await Teach("healingtotem", 14007);
        await HealingStat(caster, 104, 1000);
        await HealingStat(target, 101, 100_000);
        var source = Session("healingtotem"); var recipient = Session("healingexpiry");
        var effects = _app.Services.GetRequiredService<TotemEffects>();
        await Loop.InvokeAsync(async () =>
        {
            var item = new InvItem
            {
                Guid = 990991, Entry = 17300001, Stack = 1, Pack = 4,
                Attrs = new Dictionary<int, long> { [3] = 1, [240] = 10053012 },
            };
            source.Items.Add(item);
            source.Totems!.TotemPlans[0].SetUpTotem[1] = item.Guid;
            await effects.RefreshAsync(source);
            await Character.SetHpSpAsync(recipient, 1, null);
            await _app.Services.GetRequiredService<BuffService>().AddAsync(recipient, 1420110, 120000, source.Role!.Id);
            int index = recipient.Buffs.FindIndex(b => b.ConfigId == 1420110);
            recipient.Buffs[index] = recipient.Buffs[index] with { ExpireMs = MonsterService.NowMs - 1 };
            Assert.Equal(0, Character.HealingStats(recipient)[1208]);
        });
        Assert.Equal(1.01, effects.HealingMultiplier(source), 3);
        Assert.Equal(100, Character.HealingStats(source)[207]);
        long levelAmount = (long)Math.Floor(1035 * (1 + source.Progress!.Level / 1000d));
        long expected = (long)Math.Floor(levelAmount * 1.01);
        await caster.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1400701, TargetId = recipient.Role!.Id });
        var accepted = await Next(caster, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var hit = await Next(caster, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        Assert.Equal(expected, Assert.Single(hit.Dsts).Damage);
        Assert.Equal(1 + expected, recipient.CurrentHp);
    }

    [ClientDataFact]
    public async Task HealingPeriodicUsesTheAppliersOutputAndTargetsInputThenClampsHp()
    {
        await using var caster = await InWorld("HealTickSrc");
        await using var target = await InWorld("HealTickTarget");
        IsolateDirectHealing();
        await HealingStat(caster, 207, 5000);
        await HealingStat(target, 207, 9000); // 目標自己的輸出不能混成來源輸出。
        await HealingStat(target, 208, 2000);
        var source = Session("healticksrc"); var recipient = Session("healticktarget");
        await Loop.InvokeAsync(async () =>
        {
            await Character.SetHpSpAsync(recipient, 1, null);
            await _app.Services.GetRequiredService<BuffService>().AddAsync(recipient, 980101, 1100, source.Role!.Id);
        });
        var heal = await Next(target, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SrcID == source.Role!.Id && h.Dsts.Any(d => d.DamageType == DamageType.Heal));
        Assert.Equal(54, Assert.Single(heal.Dsts).Damage); // 30 × 1.5 × 1.2
        Assert.Equal(55, recipient.CurrentHp);
        await Loop.InvokeAsync(async () =>
        {
            await Character.SetHpSpAsync(recipient, Character.Stats(recipient)[101] - 5, null);
            await _app.Services.GetRequiredService<BuffService>().AddAsync(recipient, 980101, 1100, source.Role!.Id);
        });
        var capped = await Next(target, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SessionId != heal.SessionId && h.Dsts.Any(d => d.DamageType == DamageType.Heal));
        Assert.Equal(5, Assert.Single(capped.Dsts).Damage);
        Assert.Equal(Character.Stats(recipient)[101], recipient.CurrentHp);
    }

    private const int HealSkill = 1400701, HalfHealingTaken = 1333503; // BuffConfig 1333503: 1208 -5000

    private sealed class CritRolls(double crit) : CombatRolls
    {
        public override double Hit() => 0;
        public override double Crit() => crit;
    }

    private async Task<(long Amount, DamageHitResult Result)> HealBob(Ro3Emu.Bot.BotClient alice, Ro3Emu.Bot.BotClient bob)
    {
        await Loop.InvokeAsync(() => Character.SetHpSpAsync(Session("bob"), 1, null));
        Session("alice").SkillCds.Clear();
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = HealSkill, TargetId = (long)bob.Self!.Uuid });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser, r => r.Result == 0);
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SessionId == accepted.SessionId);
        var heal = Assert.Single(hit.Dsts, d => d.DstID == (long)bob.Self.Uuid);
        Assert.Equal(DamageType.Heal, heal.DamageType);
        await Task.Delay(1500);
        return (heal.Damage, heal.HitResult);
    }

    [ClientDataFact]
    public async Task AnOrdinaryBuffChangesHowMuchAHealGives()
    {
        await using var alice = await InWorld("Alice"); await using var bob = await InWorld("Bob");
        await Gm("alice", "!job 4100"); await Teach("alice", 14007);
        await Gm("bob", "!lvl 60 40");
        var combat = _app.Services.GetRequiredService<CombatService>();
        combat.Rolls = new CritRolls(1);
        var before = await HealBob(alice, bob);
        await Loop.InvokeAsync(() => _app.Services.GetRequiredService<BuffService>().AddAsync(Session("bob"), HalfHealingTaken, 60000, Session("alice").Role!.Id));
        var after = await HealBob(alice, bob);
        Assert.Equal(DamageHitResult.Normal, before.Result);
        Assert.InRange(after.Amount, before.Amount / 2 - 1, before.Amount / 2 + 1);
    }

    [ClientDataFact]
    public async Task AHealCritsByTheCharactersCritAndItsRow()
    {
        await using var alice = await InWorld("Alice"); await using var bob = await InWorld("Bob");
        await Gm("alice", "!job 4100"); await Teach("alice", 14007);
        await Gm("bob", "!lvl 60 40");
        var combat = _app.Services.GetRequiredService<CombatService>();
        combat.Rolls = new CritRolls(1);
        var plain = await HealBob(alice, bob);
        combat.Rolls = new CritRolls(-1);
        var crit = await HealBob(alice, bob);
        Assert.Equal(DamageHitResult.Critical, crit.Result);
        Assert.InRange(crit.Amount, (long)(plain.Amount * 1.5) - 1, (long)(plain.Amount * 1.5) + 1);
    }

    [Theory]
    [InlineData(1000, 0, 10000, 0.05)] // CRI * 0.5 / 10000
    [InlineData(1000, 500, 10000, 0.10)] // crit chance % adds
    [InlineData(1000, 0, 0, 0.0)] // a row that cannot crit
    [InlineData(0, -500, 10000, 0.0)] // not below zero
    public void AHealsCritChanceFollowsTheClientsFormula(long cri, long chance, long skillCri, double rate) =>
        Assert.Equal(rate, CombatService.HealCritRate(cri, chance, skillCri), 6);

    [Theory]
    [InlineData(0, 1.5)]
    [InlineData(2000, 1.6)] // half of Crit Damage counts, FormulaParamHealCriC 0.5
    public void AHealsCritAmountFollowsTheClientsFormula(long critDamage, double multiplier) =>
        Assert.Equal(multiplier, CombatService.HealCritMultiplier(critDamage), 6);

    /// <summary>BuffConfig 1601101: STR +5. AttributeConversionConfig 1 gives a melee job ATK and DEF per point, as a point spent does.</summary>
    [ClientDataFact]
    public async Task StrengthFromABuffRaisesWhatStrengthRaises()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1100");
        var a = Session("alice");
        var before = Character.Stats(a);
        await Loop.InvokeAsync(() => _app.Services.GetRequiredService<BuffService>().AddAsync(a, 1601101, 60000, a.Role!.Id));
        var after = Character.Stats(a);
        Assert.Equal(before[1] + 5, after[1]);
        Assert.True(after[AttributeId.Atk] >= before[AttributeId.Atk] + 5, $"{before[AttributeId.Atk]} -> {after[AttributeId.Atk]}");
        Assert.True(after[AttributeId.Def] > before[AttributeId.Def], $"{before[AttributeId.Def]} -> {after[AttributeId.Def]}");
    }

    /// <summary>BuffConfig 15998100: 205 +3000. Skill 1109901 has a 7 s cooldown.</summary>
    [ClientDataFact]
    public async Task ACooldownAttributeShortensTheCooldownTheClientIsTold()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1100"); await Teach("alice", 11099);
        var target = await Target("alice", stationary: true);
        var a = Session("alice");
        await Loop.InvokeAsync(() => _app.Services.GetRequiredService<BuffService>().AddAsync(a, 15998100, 60000, a.Role!.Id));
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1109901, TargetId = target.Uid, TargetPos = target.Pos.Clone() });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var cooldown = await Next(alice, CombatService.SkillCdUpdate, SkillCdNotify.Parser, n => n.Infos.Any(i => i.SkillId == 11099));
        var info = cooldown.Infos.First(i => i.SkillId == 11099);
        Assert.Equal(4900, info.CdEndTimestampMs - info.CdStartTimestampMs);
    }

    [Theory]
    [InlineData(7000, 3000, 4900)]
    [InlineData(7000, -9900, 13930)] // a debuff lengthens it
    [InlineData(7000, 20000, 0)] // never below zero
    public void ACooldownShareFollowsTheAttribute(long ms, long percent, long reduced) =>
        Assert.Equal(reduced, CombatService.ReducedCooldown(ms, percent));

    /// <summary>BuffConfig 1420410: 1232 -1000, a talent's SP cost cut, from an ordinary buff.</summary>
    [ClientDataFact]
    public async Task AnOrdinaryBuffChangesASkillsSpCost()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 4100"); await Teach("alice", 14001);
        var a = Session("alice");
        a.NoRegenUntilMs = long.MaxValue;
        a.GmCombat |= GmCombatSwitches.NoCooldown;
        async Task<long> SpSpent()
        {
            long before = Character.Stats(a)[AttributeId.Sp];
            await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1400101 });
            Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            await Task.Delay(2500);
            return before - Character.Stats(a)[AttributeId.Sp];
        }
        long plain = await SpSpent();
        await Loop.InvokeAsync(() => _app.Services.GetRequiredService<BuffService>().AddAsync(a, 1420410, 60000, a.Role!.Id));
        long cut = await SpSpent();
        Assert.True(plain > 0);
        Assert.Equal((long)Math.Ceiling(plain * 0.9), cut);
    }
}
