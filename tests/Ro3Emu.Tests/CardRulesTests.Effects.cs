using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Card special effects played on the real cards, socketed through 31406 and driven by real casts.</summary>
public sealed partial class CardRulesTests
{
    /// <summary>A card goes in the grid of its _iCardPart; the gear that opens that grid's sockets.</summary>
    private const int Pauldrons = 2600120, Sandals = 2650120;

    private async Task<(BotClient Bot, GameSession Session)> SocketedOn(int card, int grid, int gear)
    {
        var alice = await InWorld("Alice");
        var session = Session("Alice");
        await Gm("alice", "!lvl 80");
        await Gm("alice", $"!item {card} 1");
        await Gm("alice", $"!item {gear} 1");
        await Eventually(() => session.Items.Any(i => i.Entry == gear), "the gear");
        await alice.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq { Opr = 0, Pos = (EquipGridType)grid, ItemGUID = session.Items.Single(i => i.Entry == gear).Guid });
        Assert.Equal(0, (await Next(alice, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);
        Assert.Equal(0, await Insert(alice, grid, 1, session.Items.Single(i => i.Entry == card).Guid));
        return (alice, session);
    }

    /// <summary>Card part 7 goes in the head costume grid 8, open from base level 20 and active with no gear.</summary>
    private async Task<(BotClient Bot, GameSession Session)> OnHeadCostume(int card)
    {
        var alice = await InWorld("Alice");
        var session = Session("Alice");
        await Gm("alice", "!lvl 80");
        await Gm("alice", $"!item {card} 1");
        Assert.Equal(0, await Insert(alice, 8, 1, session.Items.Single(i => i.Entry == card).Guid));
        return (alice, session);
    }

    /// <summary>A socketed copy at a star (CardConfig._iTier), as a star-up leaves it.</summary>
    private Task AtStar(GameSession session, int card, int star) => Service<GameLoop>().InvokeAsync(async () =>
    {
        (session.Items.Single(i => i.Entry == card).Attrs ??= [])[502] = star;
        await Service<CardEffects>().RefreshAsync(session);
        await Service<CharacterService>().RefreshStatsAsync(session);
    });

    /// <summary>The attributes as the owner's stat window reads them (31008).</summary>
    private static async Task<Dictionary<long, long>> Sheet31008(BotClient bot, Func<Dictionary<long, long>, bool> match)
    {
        Dictionary<long, long> Values(OnePropertyChange change) => change.Property.Propertys.ToDictionary(p => (long)p.PropertyId, p => (long)p.Vd64);
        return Values(await Next(bot, CharacterService.OnePropertyChange, OnePropertyChange.Parser, m => match(Values(m))));
    }

    // Shadow Card: Eddga, part 5, T2 [[31, 290]]. AttributeConversionConfig 31: "Converted to other crowd-control debuff
    // attributes", 702, 704, 706, 708, 714, 716, 720, 724, 726, 728 at 1 each. The sheet is what the tag rules read
    // (CombatService.TagAttribute).
    [ClientDataFact]
    public async Task ShadowEddgasCrowdControlResistanceReachesEveryControlsOwnResistance()
    {
        const int card = 1450500;
        var (alice, session) = await SocketedOn(card, 5, Sandals);
        await using (alice)
        {
            long[] members = [702, 704, 706, 708, 714, 716, 720, 724, 726, 728];
            var socketed = await Sheet31008(alice, values => values.GetValueOrDefault(31) == 200);
            Assert.All(members, id => Assert.Equal(200, socketed[id]));

            await AtStar(session, card, 2);
            var starred = await Sheet31008(alice, values => values.GetValueOrDefault(31) == 290);
            Assert.All(members, id => Assert.Equal(290, starred[id]));
            Assert.Equal(0, starred.GetValueOrDefault(710)); // not a member
        }
    }

    // Shadow Card: Deviling, part 4, T1 [[311, 800], [30, -200]]: Neutral resist 311 keeps its own line, every other element
    // (AttributeConversionConfig 30: 312-320) comes out 2% worse.
    [ClientDataFact]
    public async Task ShadowDevilingsOtherElementPenaltyCountsOnEveryOtherElementOnly()
    {
        const int card = 1450400;
        var (alice, session) = await SocketedOn(card, 4, Pauldrons);
        await using (alice)
        {
            await AtStar(session, card, 1);
            var sheet = await Sheet31008(alice, values => values.GetValueOrDefault(311) == 800);
            Assert.Equal(-200, sheet[30]);
            foreach (long element in Enumerable.Range(312, 9).Select(id => (long)id)) Assert.Equal(-200, sheet[element]);
        }
    }

    /// <summary>Every roll fixed: hits land, DamageFloat at exactly 1, a crit by choice.</summary>
    private sealed class FixedRolls(bool crit) : CombatRolls
    {
        public override double Hit() => 0;
        public override double DamageFloat() => 0.5;
        public override double Crit() => crit ? -1 : 1;
    }

    /// <summary>Monsters only of this test's, out of battle and hard to kill, 1 m from the player.</summary>
    private async Task<MonsterUnit[]> Dummies(GameSession session, params int[] npcs)
    {
        var monsters = Service<MonsterService>();
        var units = new MonsterUnit[npcs.Length];
        for (int i = 0; i < npcs.Length; i++)
        {
            int npc = npcs[i];
            var template = monsters.Template(npc)! with { RoamDistance = 0, SurroundDistance = 0 };
            units[i] = await Service<GameLoop>().InvokeAsync(async () => (await monsters.SpawnSingleAsync(session.SceneEntry, npc,
                new MapPoint(session.Position.X, session.Position.Y, session.Position.Z, 0), session.InstanceId, customTemplate: template))!);
        }
        await Service<GameLoop>().InvokeAsync(() =>
        {
            foreach (var other in Service<World>().Map(session.WorldScene).Units.OfType<MonsterUnit>().Where(m => !units.Contains(m)))
            { other.X += 200; other.Z += 200; }
            foreach (var unit in units)
            {
                unit.StaysOutOfBattle = true;
                unit.Hp = 100_000_000;
                unit.X = session.Position.X + 1;
                unit.Z = session.Position.Z;
            }
            Service<CombatService>().Rolls = new FixedRolls(crit: false);
            return Task.CompletedTask;
        });
        return units;
    }

    private async Task<long> SwingDamage(BotClient bot, GameSession session, MonsterUnit target) =>
        Assert.Single((await Swing(bot, session, target)).Dsts).Damage;

    private static long Raised(long damage, long basisPoints) => damage + (long)Math.Floor(damage * basisPoints / 10000d);

    // Card: Orc Lady, no stat line at any star, so the card's whole effect is the buff: 2015001 "Damage against all Orcs +1.8%
    // (excluding Orc Lord and Orc Hero)", T2 2015201 +4.6%. Orc Lady 5000099 holds _kNpcTag 1000; Orc Hero 5020004 holds no tag.
    [ClientDataFact]
    public async Task OrcLadyCardRaisesDamageOnOrcsByItsStarAndLeavesOrcHeroAlone()
    {
        const int card = 1430108;
        var (alice, session) = await Socketed(card);
        await using (alice)
        {
            var units = await Dummies(session, 5000099, 5020004, 5000002);
            var (orc, hero, other) = (units[0], units[1], units[2]);
            await Eventually(() => session.Buffs.Any(b => b.IsPassive && b.ConfigId == 2015001), "the card's passive");
            long orcT0 = await SwingDamage(alice, session, orc), heroWith = await SwingDamage(alice, session, hero), otherWith = await SwingDamage(alice, session, other);

            await AtStar(session, card, 2);
            Assert.Contains(session.Buffs, b => b.IsPassive && b.ConfigId == 2015201);
            long orcT2 = await SwingDamage(alice, session, orc);

            await alice.SendGameAsync(CardService.SlotRemove, new EquipmentCardRemoveReq { EquipPos = (EquipGridType)1, SlotIndex = 1 });
            Assert.Equal(0, (await Next(alice, CardService.SlotRemove, EquipmentCardRemoveResp.Parser)).Result);
            await Eventually(() => !session.Buffs.Any(b => b.IsPassive && !b.IsTotemOwned), "the passive gone");
            long orcPlain = await SwingDamage(alice, session, orc), heroPlain = await SwingDamage(alice, session, hero), otherPlain = await SwingDamage(alice, session, other);

            Assert.True(orcPlain >= 56, "too little damage to see 1.8% in: " + orcPlain);
            Assert.Equal(Raised(orcPlain, 180), orcT0);
            Assert.Equal(Raised(orcPlain, 460), orcT2);
            Assert.Equal(heroPlain, heroWith);
            Assert.Equal(otherPlain, otherWith);
        }
    }

    private Task Learn(GameSession session, int group) => Service<GameLoop>().InvokeAsync(async () =>
    {
        using var held = await CharacterService.Lock(session);
        session.Progress!.Skills[group] = 1;
    });

    /// <summary>A real cast on the wire (31102), waiting out what still refuses it; its hit (31101).</summary>
    private static async Task<SkillHitInfo> Cast(BotClient bot, int skill, MonsterUnit target)
    {
        for (int attempt = 0; ; attempt++)
        {
            await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skill, TargetId = target.Uid });
            var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser);
            if (accepted.Result == 0)
                return await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId && h.SkillId == skill);
            Assert.True(attempt < 40, $"skill {skill} was refused {accepted.Result}");
            await Task.Delay(100);
        }
    }

    private const int Bongun = 1430604, WindGhost = 1430612, CardBash = 2002801, CardJupitel = 2002901;

    private static async Task NoHitOf(BotClient bot, int row)
    {
        await Task.Delay(300);
        Assert.Empty(SplashHits(bot, row));
    }

    /// <summary>A swing starts the auto-attack loop, whose own swings would set off a proc while a test waits.</summary>
    private async Task SwingOnce(BotClient bot, GameSession session, MonsterUnit target)
    {
        await Swing(bot, session, target);
        await OnLoop(() => { session.Attack = null; return 0; });
    }

    // Card: Bongun T2: 2019301 "When making a Physical attack, has a 4% Chance to autocast Bash Lv.1. Internal Cooldown: 2 sec."
    // A basic attack on the wire sets it off at a roll under 4.00%; Bash 2002801 lands in its own 31101; then 2 s of nothing.
    [ClientDataFact]
    public async Task BongunAutocastsBashAtItsStarsChanceAndThenWaitsItsCooldown()
    {
        var (alice, session) = await SocketedOn(Bongun, 6, Ring);
        await using (alice)
        {
            await AtStar(session, Bongun, 2);
            var target = (await Dummies(session, 5000002))[0];
            var effects = Service<CardEffects>();
            effects.Roll = _ => 400;
            await SwingOnce(alice, session, target);
            await NoHitOf(alice, CardBash);

            effects.Roll = _ => 399;
            await SwingOnce(alice, session, target);
            var bash = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == CardBash);
            Assert.True(Assert.Single(bash.Dsts).Damage > 0);
            Assert.Equal(target.Uid, bash.Dsts[0].DstID);
            Assert.False(session.SkillCds.ContainsKey(20028)); // an extra cast starts no cooldown

            await SwingOnce(alice, session, target); // inside the 2 s
            await Task.Delay(300);
            Assert.Single(SplashHits(alice, CardBash));

            await Task.Delay(2000);
            await SwingOnce(alice, session, target);
            await Eventually(() => SplashHits(alice, CardBash).Count == 2, "the second Bash");
        }
    }

    // Card: Wind Ghost T3: 2020201, 2% and 5 s; the row is magic (Jupitel Thunder), the attack that sets it off physical.
    [ClientDataFact]
    public async Task WindGhostAutocastsJupitelThunderOnAPhysicalAttack()
    {
        var (alice, session) = await SocketedOn(WindGhost, 6, Ring);
        await using (alice)
        {
            await AtStar(session, WindGhost, 3);
            var target = (await Dummies(session, 5000002))[0];
            var effects = Service<CardEffects>();
            effects.Roll = _ => 200;
            await SwingOnce(alice, session, target);
            await NoHitOf(alice, CardJupitel);

            effects.Roll = _ => 199;
            await SwingOnce(alice, session, target);
            var thunder = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == CardJupitel);
            Assert.True(Assert.Single(thunder.Dsts).Damage > 0);
            await SwingOnce(alice, session, target);
            await Task.Delay(300);
            Assert.Single(SplashHits(alice, CardJupitel)); // the 5 s
        }
    }

    // "When making a Physical attack": Sword Smash (_iDmgType 1) is one, Lex Divina (2) is not.
    [ClientDataFact]
    public async Task BongunAnswersAPhysicalSkillButNotAMagicOne()
    {
        const int swordSmash = 1109901, lexDivina = 1412101;
        var (alice, session) = await SocketedOn(Bongun, 6, Ring);
        await using (alice)
        {
            await Gm("alice", "!job 4200");
            await Learn(session, 14121);
            await Learn(session, 11099);
            var target = (await Dummies(session, 5000002))[0];
            Service<CardEffects>().Roll = _ => 0;

            Assert.True(Assert.Single((await Cast(alice, lexDivina, target)).Dsts).Damage > 0);
            await NoHitOf(alice, CardBash);

            await Gm("alice", "!job 1100");
            await Learn(session, 11099);
            await Cast(alice, swordSmash, target);
            await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == CardBash);
        }
    }

    // A proc's own cast sets off nothing: an extra cast of a physical row with the chance certain gives no Bash.
    [ClientDataFact]
    public async Task AnExtraCastStartsNoAutocast()
    {
        var (alice, session) = await SocketedOn(Bongun, 6, Ring);
        await using (alice)
        {
            var target = (await Dummies(session, 5000002))[0];
            Service<CardEffects>().Roll = _ => 0;
            var row = Service<GameData>().Row("SkillConfig", 1109901)!.Value;
            await Service<GameLoop>().InvokeAsync(() => Service<CombatService>().CastExtraAsync(session, row, [target]));
            await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1109901);
            await NoHitOf(alice, CardBash);
        }
    }

    // Card: Side Winder T1: 2021201 "Basic Attack has a 8% Chance to trigger Double Attack, dealing Extra Neutral melee Physical
    // damage equal to 50%. Internal Cooldown: 1 sec." Row 2003001 has the 50% of ATK the Novice's Basic Attack has, less its flat 10.
    [ClientDataFact]
    public async Task SideWinderDoubleAttacksOnABasicAttackAtItsStarsChanceAndThenWaitsItsCooldown()
    {
        const int card = 1440107, doubleAttack = 2003001;
        var (alice, session) = await Socketed(card);
        await using (alice)
        {
            await AtStar(session, card, 1);
            var target = (await Dummies(session, 5000002))[0];
            var effects = Service<CardEffects>();
            effects.Roll = _ => 800;
            await SwingOnce(alice, session, target);
            await NoHitOf(alice, doubleAttack);

            effects.Roll = _ => 799;
            long swing = await SwingDamage(alice, session, target);
            await OnLoop(() => { session.Attack = null; return 0; });
            var extra = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == doubleAttack);
            var sinceProc = System.Diagnostics.Stopwatch.StartNew();
            Assert.Equal(target.Uid, Assert.Single(extra.Dsts).DstID);
            Assert.InRange(extra.Dsts[0].Damage, swing - 11, swing - 1);

            // Inside the 1 s, which is about a swing's own length at this ASPD: the swing lock let go early.
            await OnLoop(() => { session.SwingLockUntilMs = 0; session.SkillCast = null; return 0; });
            await SwingOnce(alice, session, target);
            await Task.Delay(100);
            if (sinceProc.ElapsedMilliseconds < 900) Assert.Single(SplashHits(alice, doubleAttack));

            await Task.Delay(1000);
            await SwingOnce(alice, session, target);
            await Eventually(() => SplashHits(alice, doubleAttack).Count >= 2, "the second Double Attack");
        }
    }

    // "Basic Attack": Sword Smash is a Physical skill, and sets Side Winder off as little as a proc's own hit would.
    [ClientDataFact]
    public async Task SideWinderLeavesAPhysicalSkillAlone()
    {
        const int card = 1440107, doubleAttack = 2003001;
        var (alice, session) = await Socketed(card);
        await using (alice)
        {
            await Gm("alice", "!job 1100");
            await Learn(session, 11099);
            var target = (await Dummies(session, 5000002))[0];
            Service<CardEffects>().Roll = _ => 0;
            await Cast(alice, 1109901, target);
            await NoHitOf(alice, doubleAttack);
        }
    }

    private const int Pharaoh = 1460702, ClassHeal = 1400701, DivineLight = 2003901;

    private static async Task<SkillHitInfo> Heal(BotClient bot, long target)
    {
        for (int attempt = 0; ; attempt++)
        {
            await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = ClassHeal, TargetId = target });
            var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser);
            if (accepted.Result == 0)
                return await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId && h.SkillId == ClassHeal);
            Assert.True(attempt < 60, "the heal was refused " + accepted.Result);
            await Task.Delay(50);
        }
    }

    // Card: Pharaoh, T2 here (its star only raises the stat lines): 2053101 "When applying a healing skill effect, has a 10% Chance
    // to trigger Divine Light Lv1. Internal Cooldown: 2 seconds." Divine Light 2003901 "for you and a designated ST ally".
    [ClientDataFact]
    public async Task PharaohTriggersDivineLightOnTheHealedAllyAndTheCasterAndThenWaitsItsCooldown()
    {
        var (alice, session) = await OnHeadCostume(Pharaoh);
        await using (alice)
        {
            await using var bob = await InWorld("Bob");
            var ally = Session("Bob");
            await Gm("bob", "!lvl 80");
            await Gm("alice", "!job 4100");
            await Learn(session, 14007);
            await AtStar(session, Pharaoh, 2);
            Assert.Contains(session.Buffs, b => b.IsPassive && b.ConfigId == 2053101);
            var character = Service<CharacterService>();
            var effects = Service<CardEffects>();
            await Service<GameLoop>().InvokeAsync(() => character.SetHpSpAsync(ally, 1, null));

            effects.Roll = _ => 1000;
            Assert.Contains((await Heal(alice, ally.Role!.Id)).Dsts, d => d.DstID == ally.Role!.Id); // Heal reaches the caster too
            await NoHitOf(alice, DivineLight);

            await Service<GameLoop>().InvokeAsync(async () =>
            {
                await character.SetHpSpAsync(ally, 1, null);
                await character.SetHpSpAsync(session, 1, null);
            });
            effects.Roll = _ => 999;
            await Heal(alice, ally.Role!.Id);
            var light = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == DivineLight);
            var started = System.Diagnostics.Stopwatch.StartNew();
            Assert.Equal(new[] { ally.Role!.Id, session.Role!.Id }.Order(), light.Dsts.Select(d => d.DstID).Order());
            Assert.All(light.Dsts, d => { Assert.Equal(DamageType.Heal, d.DamageType); Assert.True(d.Damage > 0); });
            Assert.True(await OnLoop(() => character.Stats(session)[2000]) > 1);

            await Heal(alice, ally.Role!.Id); // a heal's chant is about the cooldown's length: only a heal inside it says anything
            if (started.ElapsedMilliseconds < 1800)
            {
                await Task.Delay(100);
                Assert.Single(SplashHits(alice, DivineLight));
            }
            await Task.Delay(2100);
            await Heal(alice, ally.Role!.Id);
            await Eventually(() => SplashHits(alice, DivineLight).Count >= 2, "the second Divine Light");
        }
    }

    // Healing yourself: the row's second target is you, so the one target is you.
    [ClientDataFact]
    public async Task PharaohsDivineLightOnASelfHealHealsOnlyTheCaster()
    {
        var (alice, session) = await OnHeadCostume(Pharaoh);
        await using (alice)
        {
            await Gm("alice", "!job 4100");
            await Learn(session, 14007);
            Service<CardEffects>().Roll = _ => 0;
            await Service<GameLoop>().InvokeAsync(() => Service<CharacterService>().SetHpSpAsync(session, 1, null));
            await Heal(alice, session.Role!.Id);
            var light = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == DivineLight);
            Assert.Equal(session.Role!.Id, Assert.Single(light.Dsts).DstID);
        }
    }

    // A swing heals no one.
    [ClientDataFact]
    public async Task PharaohLeavesAHitAlone()
    {
        var (alice, session) = await OnHeadCostume(Pharaoh);
        await using (alice)
        {
            var target = (await Dummies(session, 5000002))[0];
            Service<CardEffects>().Roll = _ => 0;
            await Swing(alice, session, target);
            await NoHitOf(alice, DivineLight);
        }
    }

    private const int Mistress = 1460701;

    /// <summary>The 31113 notices that ended a skill group's cooldown: a start equal to the end.</summary>
    private static int Resets(BotClient bot, int group) => bot.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillCdUpdate))
        .SelectMany(e => SkillCdNotify.Parser.ParseFrom(e.Body).Infos).Count(i => i.SkillId == group && i.CdStartTimestampMs == i.CdEndTimestampMs);

    // Card: Mistress, T2 here (its star only raises the stat lines): 2052901 "After using a skill, has a 6% Chance to reset that
    // skill's Cooldown ... The internal Cooldown is 3 times that skill's Cooldown." Shield Reflect Auto Counter: _iCD 1000, no chant.
    [ClientDataFact]
    public async Task MistressResetsASkillsCooldownAtItsChanceAndThenLocksThatSkillForThreeTimesIt()
    {
        const int skill = 1141901, group = 11419;
        var (alice, session) = await OnHeadCostume(Mistress);
        await using (alice)
        {
            await Gm("alice", "!job 1210");
            await Learn(session, group);
            await AtStar(session, Mistress, 2);
            Assert.Contains(session.Buffs, b => b.IsPassive && b.ConfigId == 2052901);
            var target = (await Dummies(session, 5000002))[0];
            var effects = Service<CardEffects>();
            effects.Roll = _ => 600;
            await Cast(alice, skill, target);
            await Task.Delay(100);
            Assert.Equal(0, Resets(alice, group));
            Assert.True(await OnLoop(() => session.SkillModeCds.Any(c => c.Key.Group == group && c.Value > MonsterService.NowMs)));
            await Task.Delay(1000);

            effects.Roll = _ => 599;
            await Cast(alice, skill, target);
            await Eventually(() => Resets(alice, group) == 1, "the reset");
            var reset = System.Diagnostics.Stopwatch.StartNew();
            Assert.False(await OnLoop(() => session.SkillModeCds.Any(c => c.Key.Group == group && c.Value > MonsterService.NowMs)));

            await Cast(alice, skill, target); // refused by nothing: the cooldown is gone. But it is locked now.
            await Task.Delay(100);
            Assert.True(reset.ElapsedMilliseconds < 3000, "too slow to see the lockout: " + reset.ElapsedMilliseconds);
            Assert.Equal(1, Resets(alice, group));

            await Task.Delay(Math.Max(0, 3100 - (int)reset.ElapsedMilliseconds));
            await Cast(alice, skill, target);
            await Eventually(() => Resets(alice, group) == 2, "the reset after 3 times its cooldown");
        }
    }

    // Sword Smash: _iCD 7000, _iPubCD [500] on 4100101. Only its own group ends. A Basic Attack rolls nothing: it has no cooldown.
    [ClientDataFact]
    public async Task MistressLeavesTheGlobalCooldownAndABasicAttackAlone()
    {
        const int swordSmash = 1109901, group = 11099;
        var (alice, session) = await OnHeadCostume(Mistress);
        await using (alice)
        {
            await Gm("alice", "!job 1100");
            await Learn(session, group);
            var target = (await Dummies(session, 5000002))[0];
            var effects = Service<CardEffects>();
            List<int> rolled = [];
            effects.Roll = bound => { rolled.Add(bound); return 0; };
            await Swing(alice, session, target);
            await Task.Delay(100);
            Assert.Empty(rolled);

            await Cast(alice, swordSmash, target);
            await Eventually(() => Resets(alice, group) == 1, "the reset");
            var ended = alice.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillCdUpdate))
                .SelectMany(e => SkillCdNotify.Parser.ParseFrom(e.Body).Infos).Where(i => i.CdStartTimestampMs == i.CdEndTimestampMs).ToList();
            Assert.Equal(group, Assert.Single(ended).SkillId);
        }
    }

    [ClientDataFact]
    public void MistressRowsMultipleIsTheTextsThreeTimes()
    {
        var buff = Ro3Emu.Server.Data.Config.BuffConfig.Get(Service<GameData>(), 2052901)!.Value;
        Assert.Equal(["6", "3"], buff.DescData);
        Assert.Equal([600, 0, 30000], buff.LogicData);
    }

    private const int Doppelganger = 1460103, NextHitBuff = 2091101;

    private async Task SetCrit(bool crit) => await OnLoop(() => { Service<CombatService>().Rolls = new FixedRolls(crit); return 0; });

    /// <summary>A hit right after the last one: the swing lock would otherwise spend most of the 2 s.</summary>
    private async Task<DamageInfo> QuickSwing(BotClient bot, GameSession session, MonsterUnit target)
    {
        await OnLoop(() => { session.SwingLockUntilMs = 0; session.SkillCast = null; session.Attack = null; return 0; });
        var hit = await Swing(bot, session, target);
        await OnLoop(() => { session.Attack = null; return 0; });
        return Assert.Single(hit.Dsts);
    }

    // Card: Doppelganger T2 (its star raises only stat lines): 2050701 "When you land a critical hit, your next instance of damage
    // is increased by 5%." A crit shows buff 2091101 for 2000 ms in 31104; the next hit is 5% up and takes it off (31105).
    [ClientDataFact]
    public async Task DoppelgangerRaisesTheHitAfterACritOnceAndWithinItsTwoSeconds()
    {
        var (alice, session) = await Socketed(Doppelganger);
        await using (alice)
        {
            await AtStar(session, Doppelganger, 2);
            var target = (await Dummies(session, 5000002))[0];
            long plain = (await QuickSwing(alice, session, target)).Damage;
            Assert.DoesNotContain(session.Buffs, b => b.ConfigId == NextHitBuff);

            await SetCrit(true);
            var crit = await QuickSwing(alice, session, target);
            Assert.Equal(DamageHitResult.Critical, crit.HitResult);
            var shown = await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser, n => n.Buffs.Any(b => b.ConfigId == NextHitBuff));
            Assert.Equal(2000, shown.Buffs.Single(b => b.ConfigId == NextHitBuff).Duration);

            await SetCrit(false);
            Assert.Equal(Raised(plain, 500), (await QuickSwing(alice, session, target)).Damage);
            await Next(alice, BuffService.BuffRemove, BuffRemoveNotify.Parser);
            Assert.DoesNotContain(session.Buffs, b => b.ConfigId == NextHitBuff);
            Assert.Equal(plain, (await QuickSwing(alice, session, target)).Damage);

            // A crit that spends it arms it again, and the raise counts on that crit too.
            await SetCrit(true);
            long critDamage = (await QuickSwing(alice, session, target)).Damage;
            await Eventually(() => session.Buffs.Any(b => b.ConfigId == NextHitBuff), "armed");
            Assert.Equal(Raised(critDamage, 500), (await QuickSwing(alice, session, target)).Damage);
            await Eventually(() => session.Buffs.Any(b => b.ConfigId == NextHitBuff), "armed again");

            // Past the 2 s it is gone unspent.
            await SetCrit(false);
            await Task.Delay(2200);
            Assert.DoesNotContain(session.Buffs, b => b.ConfigId == NextHitBuff);
            Assert.Equal(plain, (await QuickSwing(alice, session, target)).Damage);
        }
    }

    private const int Osiris = 1460600, OsirisReduction = 2090401;

    /// <summary>The lethal-hit offer for a socketed copy, then the player's answer on the wire (31135).</summary>
    private async Task AnswerRevive(BotClient bot, GameSession session, int expectedRow, bool take)
    {
        Assert.True(await Service<GameLoop>().InvokeAsync(() => Service<BuffService>().TryPreventLethalAsync(session)));
        Assert.Equal(expectedRow, session.ImmuneDeathOffer!.BuffConfigId);
        await bot.SendGameAsync(BuffService.ImmuneDeathConfirm, new PlayerConfirmImmuneDeadReq { IsConfirm = take });
        Assert.Equal(0, (await Next(bot, BuffService.ImmuneDeathConfirm, PlayerConfirmImmuneDeadRsp.Parser)).Result);
    }

    // Card: Osiris T3: 2052104 "... Life/MP is immediately restored to 100%. Internal Cooldown: 2.5 minutes. After the
    // death-prevention effect triggers, Physical/Magic damage taken is reduced by 30% for 5 seconds." Row 2090401 [216, 3000].
    [ClientDataFact]
    public async Task OsirisAtTheTopTierReducesDamageTakenForFiveSecondsAfterTheRevive()
    {
        var (alice, session) = await SocketedOn(Osiris, 6, Ring);
        await using (alice)
        {
            await AtStar(session, Osiris, 3);
            Assert.Contains(session.Buffs, b => b.IsPassive && b.ConfigId == 2052104);
            long before = await OnLoop(() => Service<CharacterService>().Stats(session)[216]);
            await AnswerRevive(alice, session, 2052104, take: true);

            var shown = await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser, n => n.Buffs.Any(b => b.ConfigId == OsirisReduction));
            Assert.Equal(5000, shown.Buffs.Single(b => b.ConfigId == OsirisReduction).Duration);
            Assert.Equal(before + 3000, await OnLoop(() => Service<CharacterService>().Stats(session)[216]));
            Assert.Equal(150_000, await OnLoop(() => session.BuffEffectCds.Values.Max() - MonsterService.NowMs + 500) / 1000 * 1000);
        }
    }

    // T1 2052102 has the plain text and no category 5006; a declined revive at T2 is a death, with no reduction.
    [ClientDataFact]
    public async Task OsirisReducesNothingBelowItsTierOrOnADeclinedRevive()
    {
        var (alice, session) = await SocketedOn(Osiris, 6, Ring);
        await using (alice)
        {
            await AtStar(session, Osiris, 1);
            await AnswerRevive(alice, session, 2052102, take: true);
            await Task.Delay(200);
            Assert.DoesNotContain(session.Buffs, b => b.ConfigId == OsirisReduction);

            await OnLoop(() => { session.BuffEffectCds.Clear(); return 0; });
            await AtStar(session, Osiris, 2);
            await AnswerRevive(alice, session, 2052103, take: false);
            await Task.Delay(200);
            Assert.DoesNotContain(session.Buffs, b => b.ConfigId == OsirisReduction);
        }
    }

    // T2 2052103 has the reduction too; Shadow Card: Osiris's revive has no category 5006 and no such line.
    [ClientDataTheory]
    [InlineData(2052101, false)]
    [InlineData(2052102, false)]
    [InlineData(2052103, true)]
    [InlineData(2052104, true)]
    [InlineData(2052001, false)]
    public void TheCategoryMarksExactlyTheRevivesWhoseTextNamesTheReduction(int id, bool reduces)
    {
        var data = Service<GameData>();
        var buff = Ro3Emu.Server.Data.Config.BuffConfig.Get(data, id)!.Value;
        Assert.Equal(reduces, buff.BuffCategory.Contains(5006));
        if (reduces) Assert.Equal(["30", "5"], buff.DescData[2..]);
        var reduction = Ro3Emu.Server.Data.Config.BuffConfig.Get(data, OsirisReduction)!.Value;
        Assert.Equal(["30"], reduction.DescData);
        Assert.Equal([216, 3000], reduction.ChangeAttri[0]);
    }

    // Every family row says what it adds, and the tag it counts on sits on a monster the text names.
    [ClientDataTheory]
    [InlineData(2015001, 180, 5000099)] // "Damage against all Orcs +1.8%": Orc Lady
    [InlineData(2015301, 600, 5000139)] // +6%: Orc Archer
    [InlineData(2022001, 360, 5000160)] // "Damage against all Kobolds +3.6%": Mace Kobold
    [InlineData(2022301, 900, 5000165)] // +9%: Kobold Archer
    [InlineData(2027201, 460, 5000141)] // "Damage against Kara Crabs +4.6%": Crab
    [InlineData(2028101, 320, 5000144)] // "Damage against Shellfish +3.2%"
    [InlineData(2029301, 600, 5000145)] // "Damage against Aster +6%"
    [InlineData(2053201, 500, 5000092)] // "Damage dealt to Goblins +5%": Goblin (Axe)
    [InlineData(2053301, 1000, 5020007)] // +10%: Goblin Leader
    public void EveryFamilyRowCountsItsNumberOnItsMonsters(int id, long basisPoints, int npc)
    {
        var data = Service<GameData>();
        var buff = Ro3Emu.Server.Data.Config.BuffConfig.Get(data, id)!.Value;
        Assert.Equal([basisPoints], buff.LogicData);
        Assert.Equal(basisPoints, (long)Math.Round(double.Parse(buff.DescData[0], System.Globalization.CultureInfo.InvariantCulture) * 100));
        Assert.Contains(CardEffects.FamilyTag(buff.BuffId), Ro3Emu.Server.Data.Config.NPCConfig.Get(data, npc)!.Value.NpcTag);
    }
}
