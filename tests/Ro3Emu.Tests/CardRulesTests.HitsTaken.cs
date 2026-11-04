using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Card effects around a hit on a player: socketed through 31406, read in the 31101 the hit sends.</summary>
public sealed partial class CardRulesTests
{
    /// <summary>A role with the card in its part's grid, through the real equip and socket messages. Off-hand cards need a career
    /// whose form has an off hand. A new role already carries the armor and the dagger.</summary>
    private async Task<(BotClient Bot, GameSession Session)> Holder(string name, int card, int grid)
    {
        var bot = await InWorld(name);
        var session = Session(name);
        string account = name.ToLowerInvariant();
        await Gm(account, "!lvl 80");
        if (grid == 2) await Gm(account, "!job 1100");
        await Gm(account, $"!item {card} 1");
        await Eventually(() => session.Items.Any(i => i.Entry == card), "the card");
        int gear = grid switch { 1 => Dagger, 2 => OffHand, 3 => Armor, 4 => Pauldrons, 5 => Sandals, 6 => Ring, _ => 0 };
        if (gear != 0)
        {
            if (!session.Items.Any(i => i.Entry == gear)) await Gm(account, $"!item {gear} 1");
            await Eventually(() => session.Items.Any(i => i.Entry == gear), "the gear");
            await bot.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq { Opr = 0, Pos = (EquipGridType)grid, ItemGUID = session.Items.First(i => i.Entry == gear).Guid });
            Assert.Equal(0, (await Next(bot, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);
        }
        Assert.Equal(0, await Insert(bot, grid, 1, session.Items.Single(i => i.Entry == card).Guid));
        return (bot, session);
    }

    private long _strikerAtk = 400;

    /// <summary>Monsters only of this test's, one of each element, 1 m from the player, who has no recovery tick between two hits.</summary>
    private async Task<MonsterUnit[]> Strikers(GameSession session, params int[] elements)
    {
        var monsters = Service<MonsterService>();
        var units = new MonsterUnit[elements.Length];
        for (int i = 0; i < elements.Length; i++)
        {
            var template = monsters.Template(5000002)! with { RoamDistance = 0, SurroundDistance = 0, Element = elements[i], Atk = _strikerAtk, Matk = _strikerAtk };
            units[i] = await Service<GameLoop>().InvokeAsync(async () => (await monsters.SpawnSingleAsync(session.SceneEntry, 5000002,
                new MapPoint(session.Position.X, session.Position.Y, session.Position.Z, 0), session.InstanceId, customTemplate: template))!);
        }
        await Service<GameLoop>().InvokeAsync(() =>
        {
            foreach (var other in Service<World>().Map(session.WorldScene).Units.OfType<MonsterUnit>().Where(m => !units.Contains(m)))
            { other.X += 200; other.Z += 200; }
            foreach (var unit in units)
            {
                unit.StaysOutOfBattle = true;
                unit.X = session.Position.X + 1;
                unit.Z = session.Position.Z;
            }
            session.NoRegenUntilMs = long.MaxValue;
            Service<CombatService>().Rolls = new FixedRolls(crit: false);
            return Task.CompletedTask;
        });
        return units;
    }

    private static async Task<DamageInfo> HitOn(BotClient bot, GameSession session, long source)
    {
        var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SrcID == source && h.Dsts.Any(d => d.DstID == session.Role!.Id));
        return hit.Dsts.Single(d => d.DstID == session.Role!.Id);
    }

    /// <summary>A monster's real swing on the player, at full HP; what its 31101 says of the player.</summary>
    private async Task<DamageInfo> SwungAt(BotClient bot, GameSession session, MonsterUnit monster)
    {
        await Service<GameLoop>().InvokeAsync(async () =>
        {
            await Service<CharacterService>().SetHpSpAsync(session, long.MaxValue, null);
            await Service<MonsterService>().Attack!(monster, session);
        });
        return await HitOn(bot, session, monster.Uid);
    }

    /// <summary>A monster's skill row landing on the player, at full HP.</summary>
    private async Task<DamageInfo> SkillAt(BotClient bot, GameSession session, MonsterUnit monster, int row, int maxHpBasisPoints = 0)
    {
        await Service<GameLoop>().InvokeAsync(async () =>
        {
            await Service<CharacterService>().SetHpSpAsync(session, long.MaxValue, null);
            await Service<CombatService>().MonsterSkillHitAsync(monster, Service<GameData>().Row("SkillConfig", row)!.Value, [session], monster.Uid, maxHpBasisPoints: maxHpBasisPoints);
        });
        return await HitOn(bot, session, monster.Uid);
    }

    /// <summary>Two players in one room, hostile both ways, 2 m apart, at full HP with no recovery tick and the rolls fixed.</summary>
    private async Task Duel(BotClient alice, GameSession a, BotClient bob, GameSession b)
    {
        var instances = Service<InstanceService>();
        var room = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(instances.Create(a.SceneEntry, [a, b])));
        foreach (var (bot, session) in new[] { (alice, a), (bob, b) })
        {
            Assert.True(await Service<GameLoop>().InvokeAsync(() => instances.EnterAsync(room, session, reason: 3)));
            await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, m => m.SceneID == room.Id);
            await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = room.SceneEntry });
            await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
        }
        await Service<GameLoop>().InvokeAsync(async () =>
        {
            Service<CombatService>().SetEventPvPHandlers((source, target) => source != target, (_, _) => Task.CompletedTask);
            Service<CombatService>().Rolls = new FixedRolls(crit: false);
            await Service<SceneService>().TeleportInMapAsync(b, new MapPoint(a.Position.X + 2, a.Position.Y, a.Position.Z, b.Face));
            foreach (var p in new[] { a, b })
            {
                p.NoRegenUntilMs = long.MaxValue;
                await Service<CharacterService>().SetHpSpAsync(p, long.MaxValue, long.MaxValue);
            }
        });
    }

    /// <summary>A real cast at a player on the wire, its cooldowns cleared; the hit it lands on them.</summary>
    private async Task<DamageInfo> CastAt(BotClient bot, GameSession caster, int skill, GameSession target, bool refillCaster = true)
    {
        await Service<GameLoop>().InvokeAsync(async () =>
        {
            caster.SkillCds.Clear(); caster.SkillModeCds.Clear(); caster.SwingLockUntilMs = 0; caster.SkillCast = null;
            await Service<CharacterService>().SetHpSpAsync(target, long.MaxValue, null);
            if (refillCaster) await Service<CharacterService>().SetHpSpAsync(caster, long.MaxValue, long.MaxValue);
        });
        for (int attempt = 0; ; attempt++)
        {
            await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skill, TargetId = target.Role!.Id });
            var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser);
            if (accepted.Result == 0)
            {
                var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId && h.Dsts.Any(d => d.DstID == target.Role!.Id));
                await OnLoop(() => { caster.Attack = null; return 0; });
                return hit.Dsts.Single(d => d.DstID == target.Role!.Id);
            }
            Assert.True(attempt < 40, $"skill {skill} was refused {accepted.Result}");
            await Task.Delay(100);
        }
    }

    private const int ShadowGoldenThiefBug = 1450200, GoldenThiefBug = 1460200, FireBolt = 1200401;

    // Card: Golden Thief Bug T2: 2051103 "When hit by a magic attack, has a 28% Chance to nullify its damage. Internal Cooldown: 5
    // seconds." The 31101 of the nullified hit says 0; inside the 5 s the certain roll nullifies nothing.
    [ClientDataFact]
    public async Task GoldenThiefBugNullifiesAMagicHitAtItsStarsChanceAndThenWaitsItsCooldown()
    {
        var (alice, session) = await Holder("Alice", GoldenThiefBug, 2);
        await using (alice)
        {
            await AtStar(session, GoldenThiefBug, 2);
            var striker = (await Strikers(session, 1))[0];
            var effects = Service<CardEffects>();
            effects.Roll = _ => 2800;
            Assert.True((await SkillAt(alice, session, striker, FireBolt)).Damage > 0);

            effects.Roll = _ => 2799;
            var nullified = await SkillAt(alice, session, striker, FireBolt);
            var since = System.Diagnostics.Stopwatch.StartNew();
            Assert.Equal(0, nullified.Damage);
            Assert.Equal(await OnLoop(() => Service<CharacterService>().Stats(session)[101]), await OnLoop(() => Service<CharacterService>().Stats(session)[2000]));

            effects.Roll = _ => 0;
            var inCooldown = await SkillAt(alice, session, striker, FireBolt);
            if (since.ElapsedMilliseconds < 4800) Assert.True(inCooldown.Damage > 0);

            await Task.Delay(Math.Max(0, 5100 - (int)since.ElapsedMilliseconds));
            Assert.Equal(0, (await SkillAt(alice, session, striker, FireBolt)).Damage);
        }
    }

    // Shadow Card: Golden Thief Bug T3: its stat lines rise, 2051001 stays 10% and 5 s.
    [ClientDataFact]
    public async Task ShadowGoldenThiefBugKeepsItsChanceAtEveryStar()
    {
        var (alice, session) = await Holder("Alice", ShadowGoldenThiefBug, 2);
        await using (alice)
        {
            await AtStar(session, ShadowGoldenThiefBug, 3);
            var striker = (await Strikers(session, 1))[0];
            var effects = Service<CardEffects>();
            effects.Roll = _ => 1000;
            Assert.True((await SkillAt(alice, session, striker, FireBolt)).Damage > 0);
            effects.Roll = _ => 999;
            Assert.Equal(0, (await SkillAt(alice, session, striker, FireBolt)).Damage);
        }
    }

    // "a magic attack": a swing of _iDmgType 1 lands whatever the roll. "(Boss instant-kill skills can bypass this card's effect.)":
    // a raid mechanic's share of maximum HP lands too, and spends no cooldown. Nor does a tick count as an attack.
    [ClientDataFact]
    public async Task GoldenThiefBugLeavesAPhysicalHitAMechanicAndATickAlone()
    {
        var (alice, session) = await Holder("Alice", GoldenThiefBug, 2);
        await using (alice)
        {
            var striker = (await Strikers(session, 1))[0];
            var effects = Service<CardEffects>();
            effects.Roll = _ => 0;
            Assert.True((await SwungAt(alice, session, striker)).Damage > 0);
            Assert.True((await SkillAt(alice, session, striker, FireBolt, maxHpBasisPoints: 1000)).Damage > 0);
            var magic = Service<GameData>().Row("SkillConfig", FireBolt)!.Value;
            Assert.Equal(1000, await OnLoop(() => effects.AdjustDamageTaken(session, magic, directHit: false, 1000)));
            Assert.Equal(0, (await SkillAt(alice, session, striker, FireBolt)).Damage);
        }
    }

    // "When hit by a magic attack" holds for a player's Fire Bolt on the wire too.
    [ClientDataFact]
    public async Task GoldenThiefBugNullifiesAPlayersMagicHit()
    {
        var (bob, b) = await Holder("Bob", GoldenThiefBug, 2);
        await using (bob)
        {
            await using var alice = await InWorld("Alice");
            var a = Session("Alice");
            await Gm("alice", "!lvl 80");
            await Gm("alice", "!job 2100");
            await Learn(a, 12004);
            await Duel(alice, a, bob, b);
            Service<CardEffects>().Roll = _ => 0;
            Assert.Equal(0, (await CastAt(alice, a, FireBolt, b)).Damage);
        }
    }

    private const int Ladybug = 1430702, Silence = 902301;

    // Card: Ladybug T2: 2026001 "Damage taken while Silenced -3%." [300]; its star raises only HP, and the damage only has to be
    // read against a hit at the same star. A real swing and a Fire Bolt, silenced and not.
    [ClientDataFact]
    public async Task LadybugTakesThreePercentOffWhileSilencedOnly()
    {
        var (alice, session) = await Holder("Alice", Ladybug, 8);
        await using (alice)
        {
            await AtStar(session, Ladybug, 2);
            var striker = (await Strikers(session, 1))[0];
            long swing = (await SwungAt(alice, session, striker)).Damage;
            long skill = (await SkillAt(alice, session, striker, FireBolt)).Damage;
            Assert.True(swing >= 34, "too little damage to see 3% in: " + swing);

            await Service<GameLoop>().InvokeAsync(() => Service<BuffService>().AddAsync(session, Silence, 60_000, striker.Uid));
            Assert.Equal(swing - (long)Math.Floor(swing * 0.03), (await SwungAt(alice, session, striker)).Damage);
            Assert.Equal(skill - (long)Math.Floor(skill * 0.03), (await SkillAt(alice, session, striker, FireBolt)).Damage);

            await Service<GameLoop>().InvokeAsync(() => Service<BuffService>().RemoveAsync(session, session.Buffs.Single(b => b.ConfigId == Silence)));
            Assert.Equal(swing, (await SwungAt(alice, session, striker)).Damage);
        }
    }

    private const int OrcLord = 1460301, ShadowOrcLord = 1450301, Maya = 1460201, ShadowMaya = 1450201, SwordSmash = 1109901, MagnumBreak = 1100201;

    /// <summary>Alice the attacker of a career, with the skill learned, and Bob holding the card, in one duel.</summary>
    private async Task<(BotClient Alice, GameSession A, BotClient Bob, GameSession B)> ReflectDuel(int card, int grid, int star, int job, params int[] groups)
    {
        var (bob, b) = await Holder("Bob", card, grid);
        await AtStar(b, card, star);
        var alice = await InWorld("Alice");
        var a = Session("Alice");
        await Gm("alice", "!lvl 80");
        await Gm("alice", $"!job {job}");
        foreach (int group in groups) await Learn(a, group);
        await Duel(alice, a, bob, b);
        return (alice, a, bob, b);
    }

    private async Task<long> Attribute(GameSession session, int id) => await OnLoop(() => Service<CharacterService>().Stats(session)[id]);

    // Card: Orc Lord T2: 2051703 "When hit by a player's ST Physical skill, reflects 48% of the damage taken, up to 35% of the attacker's
    // Max Life. For every 1 VIT, reflects an Extra 0.1% of the damage taken, up to 60%. Internal Cooldown: 3 seconds." A second 31101 goes
    // to the attacker, as row 2051703, from Bob. Sword Smash is ST and Physical.
    [ClientDataFact]
    public async Task OrcLordReflectsAPlayersSingleTargetPhysicalSkillAtItsStarAndThenWaitsItsCooldown()
    {
        var (alice, a, bob, b) = await ReflectDuel(OrcLord, 3, 2, 1100, 11099);
        await using (alice)
        await using (bob)
        {
            const int row = 2051703;
            long vit = await Attribute(b, 3), maxHp = await Attribute(a, 101);
            var taken = await CastAt(alice, a, SwordSmash, b);
            var since = System.Diagnostics.Stopwatch.StartNew();
            Assert.True(taken.Damage >= 100, "too little damage to read a share of: " + taken.Damage);
            var reflected = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == row && h.SrcID == b.Role!.Id);
            long share = Math.Min(4800 + 10 * vit, 6000);
            Assert.Equal(Math.Min((long)Math.Floor(taken.Damage * share / 10000d), maxHp * 3500 / 10000), Assert.Single(reflected.Dsts).Damage);
            Assert.Equal(a.Role!.Id, reflected.Dsts[0].DstID);
            await Task.Delay(100);
            Assert.Equal(maxHp - reflected.Dsts[0].Damage, await Attribute(a, 2000));

            await CastAt(alice, a, SwordSmash, b);
            await Task.Delay(200);
            if (since.ElapsedMilliseconds < 2800) Assert.Single(SplashHits(alice, row));

            await Task.Delay(Math.Max(0, 3100 - (int)since.ElapsedMilliseconds));
            await CastAt(alice, a, SwordSmash, b);
            await Eventually(() => SplashHits(alice, row).Count == 2, "the second reflect");
        }
    }

    // Shadow Card: Maya T3: 2051204 "When hit by a player's ST magic skill, reflects 50% ... up to 25% of the attacker's Max Life."
    // Fire Bolt is ST and magic. The Shadow card has no VIT line.
    [ClientDataFact]
    public async Task ShadowMayaReflectsAPlayersSingleTargetMagicSkillWithNoVitLine()
    {
        var (alice, a, bob, b) = await ReflectDuel(ShadowMaya, 2, 3, 2100, 12004);
        await using (alice)
        await using (bob)
        {
            long maxHp = await Attribute(a, 101);
            var taken = await CastAt(alice, a, FireBolt, b);
            var reflected = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 2051204 && h.SrcID == b.Role!.Id);
            Assert.Equal(Math.Min(taken.Damage / 2, maxHp * 2500 / 10000), Assert.Single(reflected.Dsts).Damage);
        }
    }

    // Each kind answers its own: Orc Lord leaves a magic hit alone, Maya a Physical one.
    [ClientDataTheory]
    [InlineData(1460301, 3, 2100, 12004, 1200401)] // Orc Lord, Fire Bolt
    [InlineData(1450301, 3, 2100, 12004, 1200401)] // Shadow Orc Lord, Fire Bolt
    [InlineData(1460201, 2, 1100, 11099, 1109901)] // Maya, Sword Smash
    [InlineData(1450201, 2, 1100, 11099, 1109901)] // Shadow Maya, Sword Smash
    [InlineData(1460301, 3, 1100, 11002, 1100201)] // Orc Lord, Magnum Break: an AoE of the same kind
    public async Task AReflectAnswersOnlyASingleTargetSkillOfItsKind(int card, int grid, int job, int group, int skill)
    {
        var (alice, a, bob, b) = await ReflectDuel(card, grid, 0, job, group);
        await using (alice)
        await using (bob)
        {
            Assert.True((await CastAt(alice, a, skill, b)).Damage > 0);
            await Task.Delay(300);
            Assert.DoesNotContain(alice.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillHit)).Select(e => SkillHitInfo.Parser.ParseFrom(e.Body)), h => h.SrcID == b.Role!.Id);
        }
    }

    // "a player's ST Physical skill": a Basic Attack is no skill (inferred), and a monster's hit is no player's.
    [ClientDataFact]
    public async Task OrcLordLeavesABasicAttackAndAMonstersHitAlone()
    {
        var (alice, a, bob, b) = await ReflectDuel(OrcLord, 3, 0, 1100);
        await using (alice)
        await using (bob)
        {
            int attack = await OnLoop(() => Service<SkillService>().CurrentAttack(a));
            Assert.True((await CastAt(alice, a, attack, b)).Damage > 0);
            await OnLoop(() => { Service<CombatService>().SetEventPvPHandlers((_, _) => false, (_, _) => Task.CompletedTask); return 0; });
            await Task.Delay(300);
            Assert.DoesNotContain(alice.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillHit)).Select(e => SkillHitInfo.Parser.ParseFrom(e.Body)), h => h.SrcID == b.Role!.Id);

            var striker = (await Strikers(b, 1))[0];
            await SwungAt(bob, b, striker);
            await SkillAt(bob, b, striker, SwordSmash);
            await Task.Delay(300);
            Assert.DoesNotContain(bob.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillHit)).Select(e => SkillHitInfo.Parser.ParseFrom(e.Body)), h => h.SkillId == 2051701);
        }
    }

    // Two holders: Alice's Sword Smash on Bob comes back as a reflect, and Alice's own Orc Lord does not answer that, even off cooldown.
    [ClientDataFact]
    public async Task AReflectIsNotReflected()
    {
        var (bob, b) = await Holder("Bob", OrcLord, 3);
        await using (bob)
        {
            var (alice, a) = await Holder("Alice", ShadowOrcLord, 3);
            await using (alice)
            {
                await Gm("alice", "!job 1100");
                await Learn(a, 11099);
                await Duel(alice, a, bob, b);
                await CastAt(alice, a, SwordSmash, b);
                await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 2051701);
                await Task.Delay(300);
                Assert.Empty(SplashHits(bob, 2051601));
            }
        }
    }

    /// <summary>Alice with the card, a Swordman, Sword Smash learned, against a plain Bob.</summary>
    private async Task<(BotClient Alice, GameSession A, BotClient Bob, GameSession B)> AttackerDuel(int card, int grid, int star)
    {
        var (alice, a) = await Holder("Alice", card, grid);
        await AtStar(a, card, star);
        await Gm("alice", "!job 1100");
        await Learn(a, 11099);
        var bob = await InWorld("Bob");
        var b = Session("Bob");
        await Gm("bob", "!lvl 80");
        await Duel(alice, a, bob, b);
        return (alice, a, bob, b);
    }

    private const int Stun = 901601;

    private static List<BuffInfo> StatusShown(BotClient bot, int buff) =>
    [
        .. bot.Inbox.Snapshot().Where(e => e.IsGame(BuffService.BuffUpdate)).SelectMany(e => BuffUpdateNotify.Parser.ParseFrom(e.Body).Buffs).Where(b => b.ConfigId == buff),
    ];

    // Card: Savage Babe T3: 2000201 "When dealing damage, has a 3% Chance to inflict Stun on the enemy for 0.5 sec. Internal Cooldown: 15
    // sec." A player's Sword Smash on a player: Stun shows on Bob, 500 ms, once in the 15 s.
    [ClientDataFact]
    public async Task SavageBabeStunsAPlayerAtItsStarsChanceAndThenWaitsItsCooldown()
    {
        var (alice, a, bob, b) = await AttackerDuel(1420101, 1, 3);
        await using (alice)
        await using (bob)
        {
            var effects = Service<CardEffects>();
            effects.Roll = _ => 300;
            await CastAt(alice, a, SwordSmash, b);
            await Task.Delay(200);
            Assert.Empty(StatusShown(bob, Stun));

            effects.Roll = _ => 299;
            await CastAt(alice, a, SwordSmash, b);
            var shown = await Next(bob, BuffService.BuffUpdate, BuffUpdateNotify.Parser, n => n.Buffs.Any(x => x.ConfigId == Stun));
            Assert.Equal(500, shown.Buffs.Single(x => x.ConfigId == Stun).Duration);

            effects.Roll = _ => 0;
            await CastAt(alice, a, SwordSmash, b);
            await Task.Delay(200);
            Assert.Single(StatusShown(bob, Stun));
        }
    }

    // A hit that kills leaves no one to hold the status, and no proc of the attacker runs on a fixed hit, which a reflect is: Bob's Orc
    // Lord answers Alice, and Bob's Savage Babe does not take that for his own damage.
    [ClientDataFact]
    public async Task AReflectSetsOffNoneOfTheReflectersOnHitProcs()
    {
        var (bob, b) = await Holder("Bob", OrcLord, 3);
        await using (bob)
        {
            await Gm("bob", "!item 1420101 1");
            await Eventually(() => b.Items.Any(i => i.Entry == 1420101), "the card");
            await bob.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq { Opr = 0, Pos = (EquipGridType)1, ItemGUID = b.Items.First(i => i.Entry == Dagger).Guid });
            Assert.Equal(0, (await Next(bob, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);
            Assert.Equal(0, await Insert(bob, 1, 1, b.Items.Single(i => i.Entry == 1420101).Guid));
            await using var alice = await InWorld("Alice");
            var a = Session("Alice");
            await Gm("alice", "!lvl 80");
            await Gm("alice", "!job 1100");
            await Learn(a, 11099);
            await Duel(alice, a, bob, b);
            Service<CardEffects>().Roll = _ => 0;
            await CastAt(alice, a, SwordSmash, b);
            await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 2051701);
            await Task.Delay(200);
            Assert.Empty(StatusShown(alice, Stun));
        }
    }

    // Card: Hunter Fly T3: 2017201 "has a 4% Chance to restore Life equal to 10% of the damage dealt to the enemy, up to 2000. Internal
    // Cooldown: 2 sec."
    [ClientDataFact]
    public async Task HunterFlyStealsLifeFromAPlayerAtItsStarsChance()
    {
        var (alice, a, bob, b) = await AttackerDuel(1430118, 1, 3);
        await using (alice)
        await using (bob)
        {
            await Service<GameLoop>().InvokeAsync(() => Service<CharacterService>().SetHpSpAsync(a, 100, long.MaxValue));
            var effects = Service<CardEffects>();
            effects.Roll = _ => 400;
            await CastAt(alice, a, SwordSmash, b, refillCaster: false);
            await Task.Delay(100);
            Assert.Equal(100, await Attribute(a, 2000));

            effects.Roll = _ => 399;
            long dealt = (await CastAt(alice, a, SwordSmash, b, refillCaster: false)).Damage;
            await Eventually(() => Service<CharacterService>().Stats(a)[2000] != 100, "the Life restored");
            Assert.Equal(100 + dealt / 10, await Attribute(a, 2000));
        }
    }

    // Card: Dracula T2: 2050401 "has a 10% Chance to absorb 5% of the damage dealt to the enemy as SP."
    [ClientDataFact]
    public async Task DraculaAbsorbsSpFromAPlayerAtItsChance()
    {
        var (alice, a, bob, b) = await AttackerDuel(1460102, 1, 2);
        await using (alice)
        await using (bob)
        {
            async Task<(long Sp, long Dealt)> After(int roll)
            {
                Service<CardEffects>().Roll = _ => roll;
                await Service<GameLoop>().InvokeAsync(() => Service<CharacterService>().SetHpSpAsync(a, long.MaxValue, 500));
                long dealt = (await CastAt(alice, a, SwordSmash, b, refillCaster: false)).Damage;
                await Task.Delay(100);
                return (await Attribute(a, 2001), dealt);
            }
            var (spent, _) = await After(1000);
            var (absorbed, dealt) = await After(999);
            Assert.True(dealt >= 20, "too little damage to read 5% of: " + dealt);
            Assert.Equal(spent + dealt / 20, absorbed);
        }
    }

    // Card: Familiar T3: 2001201, 3%, Blind 902501 for 1 s. "Reduces field of view": a Blinded monster no longer sees a player 6 m off,
    // and still does at 4 m (BuffService.BlindRadiusCm), as with a guardian's Blind.
    [ClientDataFact]
    public async Task FamiliarsBlindLimitsAMonstersSight()
    {
        const int card = 1420104, blind = 902501;
        var (alice, session) = await Socketed(card);
        await using (alice)
        {
            await AtStar(session, card, 3);
            var target = (await Dummies(session, 5000002))[0];
            var effects = Service<CardEffects>();
            var monsters = Service<MonsterService>();
            bool SeesAt(float metres)
            {
                target.X = session.Position.X + metres;
                return monsters.TargetVisible!(target, session);
            }
            effects.Roll = _ => 300;
            await SwingOnce(alice, session, target);
            await Task.Delay(100);
            Assert.True(await OnLoop(() => SeesAt(6)));
            await OnLoop(() => SeesAt(1));

            effects.Roll = _ => 299;
            await SwingOnce(alice, session, target);
            await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser, n => n.Buffs.Any(x => x.ConfigId == blind));
            Assert.False(await OnLoop(() => SeesAt(6)));
            Assert.True(await OnLoop(() => SeesAt(4)));
        }
    }

    // Card: Bongun T2: 2019301, 4%, 2 s: "When making a Physical attack" holds for a hit on a player, and Bash lands on him.
    [ClientDataFact]
    public async Task BongunAutocastsBashOnAPlayer()
    {
        var (alice, a, bob, b) = await AttackerDuel(Bongun, 6, 2);
        await using (alice)
        await using (bob)
        {
            var effects = Service<CardEffects>();
            effects.Roll = _ => 400;
            await CastAt(alice, a, SwordSmash, b);
            await NoHitOf(alice, CardBash);
            effects.Roll = _ => 399;
            await CastAt(alice, a, SwordSmash, b);
            var bash = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == CardBash);
            Assert.Equal(b.Role!.Id, Assert.Single(bash.Dsts).DstID);
            Assert.True(bash.Dsts[0].Damage > 0);
        }
    }

    // Card: Side Winder T1: 2021201, 8%: a Basic Attack on a player.
    [ClientDataFact]
    public async Task SideWinderDoubleAttacksAPlayer()
    {
        const int doubleAttack = 2003001;
        var (alice, a, bob, b) = await AttackerDuel(1440107, 1, 1);
        await using (alice)
        await using (bob)
        {
            int attack = await OnLoop(() => Service<SkillService>().CurrentAttack(a));
            Service<CardEffects>().Roll = _ => 799;
            await CastAt(alice, a, attack, b);
            var extra = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == doubleAttack);
            Assert.Equal(b.Role!.Id, Assert.Single(extra.Dsts).DstID);
        }
    }

    // Card: Doppelganger T2: 2050701 "your next instance of damage is increased by 5%", on a player too.
    [ClientDataFact]
    public async Task DoppelgangerRaisesTheNextHitOnAPlayer()
    {
        var (alice, a, bob, b) = await AttackerDuel(Doppelganger, 1, 2);
        await using (alice)
        await using (bob)
        {
            long plain = (await CastAt(alice, a, SwordSmash, b)).Damage;
            await SetCrit(true);
            var crit = await CastAt(alice, a, SwordSmash, b);
            Assert.Equal(DamageHitResult.Critical, crit.HitResult);
            await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser, n => n.Buffs.Any(x => x.ConfigId == NextHitBuff));
            await SetCrit(false);
            Assert.Equal(Raised(plain, 500), (await CastAt(alice, a, SwordSmash, b)).Damage);
            Assert.Equal(plain, (await CastAt(alice, a, SwordSmash, b)).Damage);
        }
    }

    // Card: Dark Priest T3: 2023201 "When dealing DMG, there is a 12% Chance to reduce the target's current SP by 20%, up to 400.
    // Internal Cooldown: 15 sec." Row 2003101 [20, 400]. Bob's SP as his sheet holds it.
    [ClientDataFact]
    public async Task DarkPriestBurnsAPlayersSpAtItsStarsChanceAndThenWaitsItsCooldown()
    {
        var (alice, a, bob, b) = await AttackerDuel(1440112, 1, 3);
        await using (alice)
        await using (bob)
        {
            long full = await Attribute(b, 2001);
            var effects = Service<CardEffects>();
            effects.Roll = _ => 1200;
            await CastAt(alice, a, SwordSmash, b);
            await Task.Delay(100);
            Assert.Equal(full, await Attribute(b, 2001));

            effects.Roll = _ => 1199;
            await CastAt(alice, a, SwordSmash, b);
            await Eventually(() => Service<CharacterService>().Stats(b)[2001] != full, "the SP burned");
            long burned = full - await Attribute(b, 2001);
            Assert.Equal(Math.Min(400, full * 20 / 100), burned);

            effects.Roll = _ => 0;
            await CastAt(alice, a, SwordSmash, b);
            await Task.Delay(100);
            Assert.Equal(full - burned, await Attribute(b, 2001));
        }
    }

    // A monster whose current SP is zero cannot lose SP, so the card does not roll.
    [ClientDataFact]
    public async Task DarkPriestRollsNothingOnAMonsterWithoutSp()
    {
        const int card = 1440112;
        var (alice, session) = await Socketed(card);
        await using (alice)
        {
            var target = (await Dummies(session, 5000002))[0];
            await OnLoop(() => { target.CurrentSp = 0; return 0; });
            List<int> rolled = [];
            Service<CardEffects>().Roll = bound => { rolled.Add(bound); return 0; };
            await SwingOnce(alice, session, target);
            await Task.Delay(100);
            Assert.Empty(rolled);
        }
    }

    private const int MarineSphere = 1430602, CardMagnumBreak = 2004101, CardMagnumBuff = 2092101, PoisonSpore = 1420605, CardEnvenom = 2004201;

    // Card: Marine Sphere T2 (its star raises ATK and MATK): grants Magnum Break 2004101, "increases your Fire DMG by 5%, and changes
    // Normal Attacks to Fire for 8 sec." 2092101 [[304,500]] for 8000 ms. A Basic Attack's 31101 says Fire while it holds.
    [ClientDataFact]
    public async Task MarineSpheresMagnumBreakMakesBasicAttacksFireForItsSeconds()
    {
        var (alice, session) = await Holder("Alice", MarineSphere, 6);
        await using (alice)
        {
            await AtStar(session, MarineSphere, 2);
            var target = (await Dummies(session, 5000002))[0];
            var plain = await Swing(alice, session, target);
            await OnLoop(() => { session.Attack = null; return 0; });
            Assert.Equal(1, plain.SrcElement);

            await Cast(alice, CardMagnumBreak, target);
            await OnLoop(() => { session.Attack = null; return 0; });
            var shown = await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser, n => n.Buffs.Any(x => x.ConfigId == CardMagnumBuff));
            Assert.Equal(8000, shown.Buffs.Single(x => x.ConfigId == CardMagnumBuff).Duration);

            var fire = await QuickSwing(alice, session, target);
            Assert.InRange(fire.Damage, Raised(plain.Dsts[0].Damage, 500) - 1, Raised(plain.Dsts[0].Damage, 500) + 1); // Neutral target, EleConfig row 1 _iFire 0
            var fireHit = SplashHits(alice, await OnLoop(() => Service<SkillService>().CurrentAttack(session)))[^1];
            Assert.Equal(4, fireHit.SrcElement);

            await Service<GameLoop>().InvokeAsync(() => Service<BuffService>().RemoveAsync(session, session.Buffs.Single(b => b.ConfigId == CardMagnumBuff)));
            Assert.Equal(plain.Dsts[0].Damage, (await QuickSwing(alice, session, target)).Damage);
        }
    }

    // Card: Poison Spore grants Envenom 2004201, _kLogicData [1511601]: 31101 for the Envenom, then 31101 for "Poisoned", Poison, on the same monster.
    [ClientDataFact]
    public async Task PoisonSporesEnvenomFollowsWithItsPoisonedHit()
    {
        var (alice, session) = await Holder("Alice", PoisonSpore, 6);
        await using (alice)
        {
            await AtStar(session, PoisonSpore, 1);
            var target = (await Dummies(session, 5000002))[0];
            var envenom = await Cast(alice, CardEnvenom, target);
            var poisoned = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1511601);
            Assert.Equal(target.Uid, Assert.Single(poisoned.Dsts).DstID);
            Assert.True(poisoned.Dsts[0].Damage > 0);
            Assert.True(poisoned.Dsts[0].Damage < envenom.Dsts[0].Damage); // 20% of ATK against 72% + 32
            Assert.Equal(6, poisoned.SrcElement);
            await Task.Delay(200);
            Assert.Single(SplashHits(alice, 1511601));
        }
    }

    // Class Envenom at level 3: 1500503 [1511603].
    [ClientDataFact]
    public async Task ClassEnvenomFollowsWithThePoisonedRowOfItsLevel()
    {
        await using var alice = await InWorld("Alice");
        var session = Session("Alice");
        await Gm("alice", "!lvl 80");
        await Gm("alice", "!job 5100");
        await Service<GameLoop>().InvokeAsync(async () =>
        {
            using var held = await CharacterService.Lock(session);
            session.Progress!.Skills[15005] = 3;
        });
        var target = (await Dummies(session, 5000002))[0];
        await Cast(alice, 1500503, target);
        var poisoned = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1511603);
        Assert.Equal(target.Uid, Assert.Single(poisoned.Dsts).DstID);
    }

    // On a player too.
    [ClientDataFact]
    public async Task EnvenomsPoisonedHitLandsOnAPlayer()
    {
        var (alice, a, bob, b) = await AttackerDuel(PoisonSpore, 6, 0);
        await using (alice)
        await using (bob)
        {
            await CastAt(alice, a, CardEnvenom, b);
            var poisoned = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1511601);
            Assert.Equal(b.Role!.Id, Assert.Single(poisoned.Dsts).DstID);
        }
    }

    // Maya Purple's text reveals hidden monsters, and no monster hides: no NPC or AI row holds a hiding buff or a skill that gives one.
    [ClientDataFact]
    public void NoMonsterHoldsAHidingBuffOrSkill()
    {
        var data = Service<GameData>();
        var hiding = data.Table("BuffConfig").Values.Select(r => new Ro3Emu.Server.Data.Config.BuffConfig(r))
            .Where(CombatService.StealthBuff).Select(b => b.Id).ToHashSet();
        Assert.Contains(5000901L, hiding);
        var hides = data.Table("SkillConfig").Values.Select(r => new Ro3Emu.Server.Data.Config.SkillConfig(r))
            .Where(s => s.Buff1.Concat(s.Buff2).Any(tuple => tuple.Length > 0 && hiding.Contains(tuple[0]))).Select(s => s.Id).ToHashSet();
        foreach (var npc in data.Table("NPCConfig").Values.Select(r => new Ro3Emu.Server.Data.Config.NPCConfig(r)))
        {
            Assert.DoesNotContain(npc.Skills, hides.Contains);
            Assert.DoesNotContain(npc.BornEventBuff, b => b.Length > 0 && hiding.Contains(b[0]));
        }
        foreach (var ai in data.Table("AIConfig").Values.Select(r => new Ro3Emu.Server.Data.Config.AIConfig(r)))
            Assert.DoesNotContain(ai.Skills, hides.Contains);
    }

    private const int Phen = 1440604, Sleep = 902201;

    /// <summary>A Mage's Fire Bolt on the wire (31102): the chant its 31100 says.</summary>
    private async Task<(long Session, int ChantMs)> StartFireBolt(BotClient bot, GameSession session, MonsterUnit target)
    {
        await OnLoop(() => { session.SkillCds.Clear(); session.SkillModeCds.Clear(); session.SkillCast = null; return 0; });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = FireBolt, TargetId = target.Uid });
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var use = await Next(bot, CombatService.UseInSkill, UseInSkillInfo.Parser, m => m.SessionId == accepted.SessionId);
        return (accepted.SessionId, use.ChantMs);
    }

    // Card: Phen: 2024001 "Variable CT +25%" at T0, 2024004 +10% at T3, on a base of 1000 ms (Fire Bolt _iChantVar).
    [ClientDataFact]
    public async Task PhenLengthensAVariableChantByItsStar()
    {
        var (alice, session) = await Holder("Alice", Phen, 6);
        await using (alice)
        {
            await Gm("alice", "!job 2100");
            await Learn(session, 12004);
            var target = (await Dummies(session, 5000002))[0];
            int top = 0, bottom = 0;
            await AtStar(session, Phen, 3);
            top = (await StartFireBolt(alice, session, target)).ChantMs;
            await AtStar(session, Phen, 0);
            bottom = (await StartFireBolt(alice, session, target)).ChantMs;
            Assert.InRange(bottom - top, 149, 151);

            await alice.SendGameAsync(CardService.SlotRemove, new EquipmentCardRemoveReq { EquipPos = (EquipGridType)6, SlotIndex = 1 });
            Assert.Equal(0, (await Next(alice, CardService.SlotRemove, EquipmentCardRemoveResp.Parser)).Result);
            await Eventually(() => !session.Buffs.Any(b => b.IsPassive && !b.IsTotemOwned), "the passive gone");
            Assert.InRange(bottom - (await StartFireBolt(alice, session, target)).ChantMs, 249, 251);
        }
    }

    // "Control effects cannot interrupt casting": Silence and Sleep land in the middle of a chant and the hit still comes. Without the card
    // the same Silence ends the chant in a 31103 Interrupted.
    [ClientDataTheory]
    [InlineData(Silence)]
    [InlineData(Sleep)]
    public async Task PhenKeepsAChantThroughAControl(int control)
    {
        var (alice, session) = await Holder("Alice", Phen, 6);
        await using (alice)
        {
            await Gm("alice", "!job 2100");
            await Learn(session, 12004);
            var target = (await Dummies(session, 5000002))[0];
            var buffs = Service<BuffService>();
            var (id, _) = await StartFireBolt(alice, session, target);
            await Service<GameLoop>().InvokeAsync(() => buffs.AddAsync(session, control, 60_000, target.Uid));
            var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == id);
            Assert.True(Assert.Single(hit.Dsts).Damage > 0);
            Assert.DoesNotContain(alice.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillEnd)).Select(e => SkillEndNotify.Parser.ParseFrom(e.Body)),
                e => e.SessionId == id && e.Type == SkillEndNotify.Types.SkillEndType.Interrupted);

            await Service<GameLoop>().InvokeAsync(() => buffs.RemoveAsync(session, session.Buffs.Single(b => b.ConfigId == control)));
            await alice.SendGameAsync(CardService.SlotRemove, new EquipmentCardRemoveReq { EquipPos = (EquipGridType)6, SlotIndex = 1 });
            Assert.Equal(0, (await Next(alice, CardService.SlotRemove, EquipmentCardRemoveResp.Parser)).Result);
            await Eventually(() => !session.Buffs.Any(b => b.IsPassive && !b.IsTotemOwned), "the passive gone");
            (id, _) = await StartFireBolt(alice, session, target);
            await Service<GameLoop>().InvokeAsync(() => buffs.AddAsync(session, control, 60_000, target.Uid));
            await Next(alice, CombatService.SkillEnd, SkillEndNotify.Parser, e => e.SessionId == id && e.Type == SkillEndNotify.Types.SkillEndType.Interrupted);
        }
    }

    /// <summary>Raises the player's ATK on the sheet, for a hit big enough to read a small share in.</summary>
    private async Task AddAtk(GameSession session, double atk)
    {
        await Service<GameLoop>().InvokeAsync(async () =>
        {
            Service<CharacterService>().ExtraAttributes.Add(s => s == session ? [(103L, atk)] : []);
            await Service<CharacterService>().RefreshStatsAsync(session);
        });
    }

    // Portrait Card: Drake T3: [[231, 70]], "Hunting Mastery: Affects your damage against monsters". Formula x / (x + 10000), 0.695%.
    [ClientDataFact]
    public async Task HuntingMasteryRaisesAHitOnAMonsterByItsStar()
    {
        const int card = 1440903;
        var (alice, session) = await Holder("Alice", card, 10);
        await using (alice)
        {
            await AddAtk(session, 100_000);
            var target = (await Dummies(session, 5000002))[0];
            await AtStar(session, card, 3);
            Assert.Equal(70, await Attribute(session, 231));
            long raised = await SwingDamage(alice, session, target);
            await OnLoop(() => { session.Attack = null; return 0; });

            await alice.SendGameAsync(CardService.SlotRemove, new EquipmentCardRemoveReq { EquipPos = (EquipGridType)10, SlotIndex = 1 });
            Assert.Equal(0, (await Next(alice, CardService.SlotRemove, EquipmentCardRemoveResp.Parser)).Result);
            await Eventually(() => Service<CharacterService>().Stats(session)[231] == 0, "the attribute gone");
            long plain = (await QuickSwing(alice, session, target)).Damage;
            Assert.True(plain > 10_000, "too little damage to read 0.7% in: " + plain);
            Assert.InRange(raised, (long)(plain * (1 + 70 / 10070d)) - 1, (long)(plain * (1 + 70 / 10070d)) + 1);
        }
    }

    // Portrait Card: Angeling T2: [[232, 56]], "Hunting Protection: Affects your damage taken from monsters", 0.557%.
    [ClientDataFact]
    public async Task HuntingProtectionLowersAMonstersHitByItsStar()
    {
        const int card = 1440901;
        var (alice, session) = await Holder("Alice", card, 10);
        await using (alice)
        {
            _strikerAtk = 4000;
            var striker = (await Strikers(session, 1))[0];
            await AtStar(session, card, 2);
            Assert.Equal(56, await Attribute(session, 232));
            long lowered = (await SwungAt(alice, session, striker)).Damage;

            await alice.SendGameAsync(CardService.SlotRemove, new EquipmentCardRemoveReq { EquipPos = (EquipGridType)10, SlotIndex = 1 });
            Assert.Equal(0, (await Next(alice, CardService.SlotRemove, EquipmentCardRemoveResp.Parser)).Result);
            await Eventually(() => Service<CharacterService>().Stats(session)[232] == 0, "the attribute gone");
            long plain = (await SwungAt(alice, session, striker)).Damage;
            Assert.True(plain > 1000, "too little damage to read 0.5% in: " + plain);
            Assert.InRange(lowered, (long)(plain * (1 - 56 / 10056d)) - 1, (long)(plain * (1 - 56 / 10056d)) + 1);
        }
    }

    /// <summary>The socketed copy made a Flash one or not, as the star-up leaves it.</summary>
    private Task SetFlash(GameSession session, int card, bool flash) => Service<GameLoop>().InvokeAsync(async () =>
    {
        (session.Items.Single(i => i.Entry == card).Attrs ??= [])[530] = flash ? 1 : 0;
        await Service<CardEffects>().RefreshAsync(session);
        await Service<CharacterService>().RefreshStatsAsync(session);
    });

    // A Flash Card: Soldier Skeleton: 11001101 [[1,215,60],[1,221,10]], Bonus ATK +10 "Adds an Extra attack that ignores DEF": 10 after defense,
    // then the +0.6% of 215 on it all. ATK and MATK stay what they were: it counts once.
    [ClientDataFact]
    public async Task AFlashCardsBonusAtkAddsToTheHitAfterDefenseAndNotToAtk()
    {
        const int card = 1440100;
        var (alice, session) = await Holder("Alice", card, 1);
        await using (alice)
        {
            var target = (await Dummies(session, 5000002))[0];
            long plain = (await QuickSwing(alice, session, target)).Damage;
            long atk = await Attribute(session, 103), matk = await Attribute(session, 104);

            await SetFlash(session, card, true);
            Assert.Equal(10, await Attribute(session, 221));
            Assert.Equal(atk, await Attribute(session, 103));
            Assert.Equal(matk, await Attribute(session, 104));
            long raised = (await QuickSwing(alice, session, target)).Damage;
            Assert.InRange(raised, (long)((plain + 10) * 1.006) - 1, (long)((plain + 10) * 1.006) + 2);
        }
    }

    // A Flash Card: Marc: 11001002 [[1,216,60],[1,223,20]], Bonus DEF +20 "Reduces damage from the target's Extra attacks and attacks". DEF
    // stays what it was. A tick is no attack: a poison's number does not move by the 20.
    [ClientDataFact]
    public async Task AFlashCardsBonusDefTakesOffAHitAndNotATick()
    {
        const int card = 1440301, poison = 903001;
        var (alice, session) = await Holder("Alice", card, 3);
        await using (alice)
        {
            _strikerAtk = 4000;
            var striker = (await Strikers(session, 1))[0];
            long plain = (await SwungAt(alice, session, striker)).Damage;
            long def = await Attribute(session, 105);
            async Task<long> Tick()
            {
                await Service<GameLoop>().InvokeAsync(() => Service<BuffService>().AddAsync(session, poison, 800, striker.Uid));
                var tick = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SrcID == striker.Uid && h.SkillId == 0);
                await Eventually(() => !session.Buffs.Any(b => b.ConfigId == poison && b.ExpireMs > MonsterService.NowMs), "the poison over");
                await Task.Delay(100);
                return Assert.Single(tick.Dsts).Damage;
            }
            long tickPlain = await Tick();

            await SetFlash(session, card, true);
            Assert.Equal(20, await Attribute(session, 223));
            Assert.Equal(def, await Attribute(session, 105));
            long lowered = (await SwungAt(alice, session, striker)).Damage;
            Assert.True(plain > 100, "too little damage to read 20 in: " + plain);
            Assert.InRange(lowered, (long)((plain - 20) * 0.994) - 1, (long)((plain - 20) * 0.994) + 2);
            long tickFlash = await Tick();
            Assert.True(tickPlain > 21, "too little tick to read 20 in: " + tickPlain);
            Assert.InRange(tickFlash, (long)(tickPlain * 0.994) - 1, tickPlain);
        }
    }

    // "Changes armor property to Earth", and so on. Dokebi's text says Wind and 904403 holds 2; only its row named after the card says 3.
    [ClientDataTheory]
    [InlineData(1440300, 3)] // Sandman, Earth
    [InlineData(1440302, 5)] // Swordfish, Water
    [InlineData(1440303, 2)] // Dokebi, Wind
    [InlineData(1440304, 6)] // Argiope, Poison
    [InlineData(1440305, 4)] // Pasana, Fire
    [InlineData(1440306, 9)] // Ghostring, Ghost
    [InlineData(1460300, 7)] // Angeling, Holy
    public async Task AnArmorElementCardGivesTheElementItsTextNamesInTheHitsItTakes(int card, int element)
    {
        var (alice, session) = await Holder("Alice", card, 3);
        await using (alice)
        {
            var striker = (await Strikers(session, 1))[0];
            Assert.Equal(element, (await SwungAt(alice, session, striker)).DstElement);
            Assert.Equal(element, (await SkillAt(alice, session, striker, FireBolt)).DstElement);
        }
    }

    // A Fire monster on Earth armor: EleConfig row 3 _iFire +5000; on Water armor: row 5 _iFire -2500. A Neutral monster on Ghost
    // armor: row 9 _iNone -2500. Sandman, Swordfish and Ghostring carry the same stat lines at each star, T1 here, so only the element differs.
    [ClientDataFact]
    public async Task ArmorElementScalesTheDamageByEleConfig()
    {
        var (alice, session) = await Holder("Alice", 1440300, 3);
        await using (alice)
        {
            await Gm("alice", "!item 1440302 1");
            await Gm("alice", "!item 1440306 1");
            await Eventually(() => session.Items.Any(i => i.Entry == 1440302) && session.Items.Any(i => i.Entry == 1440306), "the cards");
            await AtStar(session, 1440300, 1);
            var strikers = await Strikers(session, 4, 1);
            var (fire, neutral) = (strikers[0], strikers[1]);

            long earth = (await SwungAt(alice, session, fire)).Damage;
            long plain = (await SwungAt(alice, session, neutral)).Damage;

            async Task Swap(int card, int element)
            {
                await alice.SendGameAsync(CardService.SlotRemove, new EquipmentCardRemoveReq { EquipPos = (EquipGridType)3, SlotIndex = 1 });
                Assert.Equal(0, (await Next(alice, CardService.SlotRemove, EquipmentCardRemoveResp.Parser)).Result);
                Assert.Equal(0, await Insert(alice, 3, 1, session.Items.Single(i => i.Entry == card).Guid));
                await AtStar(session, card, 1);
                await Eventually(() => Service<CombatService>().ArmorElement(session) == element, "the new card's element");
            }

            await Swap(1440302, 5);
            long water = (await SwungAt(alice, session, fire)).Damage;
            await Swap(1440306, 9);
            long ghost = (await SwungAt(alice, session, neutral)).Damage;

            Assert.True(plain > 100, "too little damage to see the element in: " + plain);
            Assert.InRange(earth, plain * 3 / 2 - 2, plain * 3 / 2 + 2);
            Assert.InRange(water, plain * 3 / 4 - 2, plain * 3 / 4 + 2);
            Assert.InRange(ghost, plain * 3 / 4 - 2, plain * 3 / 4 + 2);
        }
    }

    [ClientDataFact]
    public async Task WithNoArmorCardAHitTakenIsOnNeutral()
    {
        await using var alice = await InWorld("Alice");
        var session = Session("Alice");
        var striker = (await Strikers(session, 4))[0];
        Assert.Equal(1, (await SwungAt(alice, session, striker)).DstElement);
    }

    // A player's Fire Bolt on Earth armor: the same row of EleConfig, +50%, and the 31101 says Earth. The tick's 31101 too.
    [ClientDataFact]
    public async Task ArmorElementCountsAgainstAPlayersHitAndATick()
    {
        var (bob, b) = await Holder("Bob", 1440300, 3);
        await using (bob)
        {
            await using var alice = await InWorld("Alice");
            var a = Session("Alice");
            await Gm("alice", "!lvl 80");
            await Gm("alice", "!job 2100");
            await Learn(a, 12004);
            await Duel(alice, a, bob, b);
            Assert.Equal(3, (await CastAt(alice, a, FireBolt, b)).DstElement);
            await Service<GameLoop>().InvokeAsync(() => Service<BuffService>().AddAsync(b, 903001, 800, a.Role!.Id));
            var tick = await Next(bob, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SrcID == a.Role!.Id && h.SkillId == 0);
            Assert.Equal(3, Assert.Single(tick.Dsts).DstElement);
        }
    }

    private const int ShadowAngeling = 1450300, HolyArmorBuff = 904407;

    // Shadow Card: Angeling T1 (as T0): 2051401 "When taking damage, has a 60% Chance to change your armor's element to Holy for 3
    // seconds. Internal Cooldown: 5 seconds." [6000, 904407, 3000]. 31104 shows the buff; the hit after that is on Holy.
    [ClientDataFact]
    public async Task ShadowAngelingTurnsTheArmorHolyAtItsChanceForItsSecondsAndThenWaitsItsCooldown()
    {
        var (alice, session) = await Holder("Alice", ShadowAngeling, 3);
        await using (alice)
        {
            await AtStar(session, ShadowAngeling, 1);
            var striker = (await Strikers(session, 1))[0];
            var effects = Service<CardEffects>();
            effects.Roll = _ => 6000;
            Assert.Equal(1, (await SwungAt(alice, session, striker)).DstElement);
            await Task.Delay(100);
            Assert.DoesNotContain(session.Buffs, b => b.ConfigId == HolyArmorBuff);

            effects.Roll = _ => 5999;
            Assert.Equal(1, (await SwungAt(alice, session, striker)).DstElement); // the hit that sets it off
            var shown = await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser, n => n.Buffs.Any(b => b.ConfigId == HolyArmorBuff));
            var since = System.Diagnostics.Stopwatch.StartNew();
            Assert.Equal(3000, shown.Buffs.Single(b => b.ConfigId == HolyArmorBuff).Duration);
            Assert.Equal(7, (await SwungAt(alice, session, striker)).DstElement);

            // Inside the cooldown, past the buff: no new one.
            await Task.Delay(3200);
            var inCooldown = await SwungAt(alice, session, striker);
            if (since.ElapsedMilliseconds < 4800) Assert.Equal(1, inCooldown.DstElement);

            await Task.Delay(Math.Max(0, 5100 - (int)since.ElapsedMilliseconds));
            await SwungAt(alice, session, striker);
            await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser, n => n.Buffs.Any(b => b.ConfigId == HolyArmorBuff));
        }
    }

    // T3: 2051404 has no chance in its text, 10000 in the row, and 4 seconds. A player's hit sets it off too.
    [ClientDataFact]
    public async Task ShadowAngelingAtItsTopStarAlwaysTurnsTheArmorHolyForFourSecondsOnAPlayersHit()
    {
        var (bob, b) = await Holder("Bob", ShadowAngeling, 3);
        await using (bob)
        {
            await AtStar(b, ShadowAngeling, 3);
            await using var alice = await InWorld("Alice");
            var a = Session("Alice");
            await Gm("alice", "!lvl 80");
            await Gm("alice", "!job 2100");
            await Learn(a, 12004);
            await Duel(alice, a, bob, b);
            Service<CardEffects>().Roll = _ => 9999;
            await CastAt(alice, a, FireBolt, b);
            var shown = await Next(bob, BuffService.BuffUpdate, BuffUpdateNotify.Parser, n => n.Buffs.Any(x => x.ConfigId == HolyArmorBuff));
            Assert.Equal(4000, shown.Buffs.Single(x => x.ConfigId == HolyArmorBuff).Duration);
            Assert.Equal(7, (await CastAt(alice, a, FireBolt, b)).DstElement);
        }
    }

    /// <summary>Whether a status reaches the holder: its own 31104 on the wire, or nothing at all.</summary>
    private async Task<bool> Lands(BotClient bot, GameSession session, int buff, long source)
    {
        await Service<GameLoop>().InvokeAsync(() => Service<BuffService>().AddAsync(session, buff, 300, source));
        await Task.Delay(100);
        return StatusShown(bot, buff).Count > 0;
    }

    // Card: Orc Hero T1: 2052701 "Immune to Stun." Every row of category 13 is refused, from a monster or a player; Silence still lands. The
    // same Stun lands once the card is out.
    [ClientDataTheory]
    [InlineData(901601)] // Stun
    [InlineData(1200401)] // the generic Stun of skills
    [InlineData(1210701)] // Meteor Storm's
    public async Task OrcHeroRefusesEveryStun(int stun)
    {
        const int card = 1460700;
        var (alice, session) = await Holder("Alice", card, 8);
        await using (alice)
        {
            await AtStar(session, card, 1);
            var striker = (await Strikers(session, 1))[0];
            Assert.False(await Lands(alice, session, stun, striker.Uid));
            Assert.True(await Lands(alice, session, Silence, striker.Uid));
            await alice.SendGameAsync(CardService.SlotRemove, new EquipmentCardRemoveReq { EquipPos = (EquipGridType)8, SlotIndex = 1 });
            Assert.Equal(0, (await Next(alice, CardService.SlotRemove, EquipmentCardRemoveResp.Parser)).Result);
            await Eventually(() => !session.Buffs.Any(b => b.IsPassive && !b.IsTotemOwned), "the passive gone");
            Assert.True(await Lands(alice, session, stun, striker.Uid));
        }
    }

    // Card: Moonlight Flower T2: 2052501 "Immune to Slow." 902801 Sluggish carries category 33; Stun is not its business.
    [ClientDataFact]
    public async Task MoonlightFlowerRefusesASlowAndNotAStun()
    {
        const int card = 1460501, slow = 902801;
        var (alice, session) = await Holder("Alice", card, 5);
        await using (alice)
        {
            await AtStar(session, card, 2);
            var striker = (await Strikers(session, 1))[0];
            Assert.False(await Lands(alice, session, slow, striker.Uid));
            Assert.True(await Lands(alice, session, Stun, striker.Uid));
        }
    }

    // Card: Eddga T3: 2052301 "Grants permanent Endure (immune to knockback, knock-up, and hitstun effects)." A player's Knockback buff
    // does not land, and nothing moves him; a Stun still does. Out of the socket, he can be moved again.
    [ClientDataFact]
    public async Task EddgaHoldsItsHolderInPlaceAndLeavesOtherControlsAlone()
    {
        const int card = 1460500, knockBack = 901701, knockUp = 901801;
        var (alice, session) = await Holder("Alice", card, 5);
        await using (alice)
        {
            await AtStar(session, card, 3);
            var striker = (await Strikers(session, 1))[0];
            var buffs = Service<BuffService>();
            Assert.True(await OnLoop(() => buffs.ResistsDisplacement(session)));
            Assert.False(await Lands(alice, session, knockBack, striker.Uid));
            Assert.False(await Lands(alice, session, knockUp, striker.Uid));
            Assert.True(await Lands(alice, session, Stun, striker.Uid));

            await alice.SendGameAsync(CardService.SlotRemove, new EquipmentCardRemoveReq { EquipPos = (EquipGridType)5, SlotIndex = 1 });
            Assert.Equal(0, (await Next(alice, CardService.SlotRemove, EquipmentCardRemoveResp.Parser)).Result);
            await Eventually(() => !session.Buffs.Any(b => b.IsPassive && !b.IsTotemOwned), "the passive gone");
            Assert.False(await OnLoop(() => buffs.ResistsDisplacement(session)));
            Assert.True(await Lands(alice, session, knockBack, striker.Uid));
        }
    }
}
