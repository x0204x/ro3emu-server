using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>GM panel combat switches on a session; combat reads them as one field per cast or hit.</summary>
[Flags]
public enum GmCombatSwitches
{
    None = 0,
    /// <summary>No skill cooldown starts (GmNoCD 1, GmUnlimit 1,1: the preset "无CD").</summary>
    NoCooldown = 1,
    /// <summary>No DamageFloat roll on the hits the GM deals or takes (GmDamageFix 1: "伤害波动" off).</summary>
    FixedDamage = 2,
    /// <summary>Invincible, and casts cost nothing (GmUnlimit 1,2: the preset "无敌无消耗").</summary>
    Unlimited = 4,
}

public sealed partial class GmCommands
{
    /// <summary>GmOpEnum values of the combat and entity debug buttons (GMConfig _iBelong 2, and 1010 / 3006).</summary>
    private const int OpChangeHp = 23, OpAddNpc = 27, OpKillEntity = 30, OpDumpEntityAttr = 32, OpSetEntityAttr = 33,
        OpChangeEntityAttr = 34, OpAddBuff = 35, OpRemoveBuff = 36, OpNoCd = 52, OpDamageFix = 53, OpReloadSkillBlueprint = 58,
        OpShowBuff = 76, OpShowAttr = 77, OpUnlimit = 80, OpCastSkill = 81, OpTargetCastSkill = 82, OpOpenLog = 92,
        OpSetEntityCamp = 95, OpBattle = 20000;

    // AttributeConfig rows the monster template carries beyond AttributeId's: maxsp and lv.
    private const int AttrMaxSp = 102, AttrLevel = 2002;

    /// <summary>A panel "target id": an online role or a map unit; Uid is what the GM typed.</summary>
    private readonly record struct Entity(GameSession? Player, WorldUnit? Unit, long Uid);

    private bool _gmAttributesHooked;

    private CombatService Combat => services.GetRequiredService<CombatService>();
    private BuffService Buffs => services.GetRequiredService<BuffService>();
    private MonsterService Monsters => services.GetRequiredService<MonsterService>();
    private World World => services.GetRequiredService<World>();

    /// <summary>GM panel combat and entity debug ops (HP, buffs, attributes, skills, NPCs, camps); null when the op is not one of them.</summary>
    private async Task<string?> CombatOpAsync(GameSession s, int op, string[] args)
    {
        switch (op)
        {
            case OpChangeHp: // target, heal, damage
                return await ChangeHpAsync(Target(s, args, 0), NumArg(args, 1), NumArg(args, 2));
            case OpAddNpc:
                return await AddNpcAsync(s, I(args[0]));
            case OpKillEntity:
                return await KillAsync(Target(s, args, 0));
            case OpDumpEntityAttr:
                return ShowAttributes(Target(s, args, 0), 0);
            case OpShowAttr: // attribute, target
                return ShowAttributes(Target(s, args, 1), (int)NumArg(args, 0));
            case OpSetEntityAttr: // target, attribute, value
                return await SetAttributeAsync(Target(s, args, 0), I(args[1]), L(args[2]), delta: false);
            case OpChangeEntityAttr: // unit, attribute, change
                return await SetAttributeAsync(Target(s, args, 0), I(args[1]), L(args[2]), delta: true);
            case OpAddBuff: // buff, duration (0: the buff's own), target
                return await AddBuffAsync(s, Target(s, args, 2), I(args[0]), (int)NumArg(args, 1));
            case OpRemoveBuff: // buff, target, by stacks, stacks
                return await RemoveBuffAsync(Target(s, args, 1), I(args[0]), NumArg(args, 2) != 0, (int)NumArg(args, 3));
            case OpShowBuff:
                return ShowBuffs(Target(s, args, 0));
            case OpNoCd:
                return await SwitchAsync(s, GmCombatSwitches.NoCooldown, NumArg(args, 0) != 0);
            case OpDamageFix: // the panel's options: 1 = variance off, 0 = on
                return await SwitchAsync(s, GmCombatSwitches.FixedDamage, NumArg(args, 0) != 0);
            case OpUnlimit:
                // The presets pass (on, kind): 1 = no cooldown (10000 "无CD"), 2 = invincible without costs (10001
                // "无敌无消耗"). The button ("无敌") has one parameter, so a missing kind is 2 (inferred).
                return NumArg(args, 1) switch
                {
                    1 => await SwitchAsync(s, GmCombatSwitches.NoCooldown, NumArg(args, 0) != 0),
                    0 or 2 => await SwitchAsync(s, GmCombatSwitches.Unlimited, NumArg(args, 0) != 0),
                    _ => throw new ArgumentException("kind 1 (no cooldown) or 2 (invincible, no costs)"),
                };
            case OpCastSkill: // the GM casts at herself
                return await CastAsync(s, new Entity(s, null, s.Role!.Id), I(args[0]));
            case OpTargetCastSkill: // skill, caster: the target casts, at the GM
                return await CastAsync(s, Target(s, args, 1), I(args[0]));
            case OpSetEntityCamp:
                return await SetCampAsync(Target(s, args, 0), (int)NumArg(args, 1));
            case OpReloadSkillBlueprint:
                return "GmReloadSkillBlueprint: skills are read from the exported client data at start; restart the server to reload them.";
            case OpOpenLog:
                return "GmOpenLog: the emulator has no per-type battle logs to switch; combat goes to the server log (Logging settings).";
            case OpBattle:
                return "GmBattle: the emulator resolves skill areas without debug shapes, so there is no damage range snapshot to draw.";
            default:
                return null;
        }
    }

    /// <summary>The target id at <paramref name="index"/>: 0 or empty is the GM's own role, else an online role or a unit.</summary>
    private Entity Target(GameSession s, string[] args, int index)
    {
        long uid = NumArg(args, index);
        if (uid == 0 || uid == s.Role?.Id) return new(s, null, s.Role?.Id ?? 0);
        if (sessions.All().FirstOrDefault(p => p.Role?.Id == uid) is { } player) return new(player, null, uid);
        // The GM's own map first: what she clicks is beside her (another map's unit still answers, inferred).
        var unit = World.Map(s.WorldScene).Units.FirstOrDefault(u => u.Uid == uid) ?? World.AllUnits().FirstOrDefault(u => u.Uid == uid);
        return new(null, unit, uid);
    }

    private static long NumArg(string[] args, int index) => args.Length > index && args[index].Length > 0 ? L(args[index]) : 0;

    private string Name(Entity e) => e.Player?.Role?.Name
        ?? (e.Unit is MonsterUnit m ? $"{Monsters.NpcName(m.Template.NpcId)} ({m.Uid})" : $"unit {e.Uid}");

    private static string Missing(Entity e) => e.Player == null && e.Unit == null
        ? $"No entity {e.Uid} online or on a map." : $"Entity {e.Uid} is not a player or a monster.";

    /// <summary>GmChangeHp: current HP (not the maximum) up by heal and down by damage; nothing left is a death.</summary>
    private async Task<string> ChangeHpAsync(Entity e, long heal, long damage)
    {
        if (heal < 0 || damage < 0) throw new ArgumentException("heal and damage are amounts of 0 or more");
        if (e.Player is { Progress: not null } p)
        {
            if (p.Dead) return $"{Name(e)} is dead.";
            long before = character.Stats(p)[AttributeId.Hp];
            decimal after = (decimal)before + heal - damage;
            // (inferred) A GM change bypasses shields and lethal prevention: it sets HP, as the button says.
            if (after <= 0) return await KillAsync(e);
            await character.SetHpSpAsync(p, (long)Math.Min(after, long.MaxValue), null);
            return $"{Name(e)} HP {before} -> {character.Stats(p)[AttributeId.Hp]}.";
        }
        if (e.Unit is MonsterUnit m)
        {
            if (m.Dead) return $"{Name(e)} is dead.";
            long before = m.Hp;
            decimal after = (decimal)before + heal - damage;
            if (after <= 0) return await KillAsync(e);
            m.Hp = (long)Math.Min(after, m.Template.MaxHp);
            await World.ToUnitViewersAsync(m, CombatService.AoiAttrChange, MonsterService.Hp(m));
            return $"{Name(e)} HP {before} -> {m.Hp}.";
        }
        return Missing(e);
    }

    /// <summary>
    /// GmKillEntity: a player dies the ordinary way (HP 0, death state, revive prompt); a monster dies without rewards or
    /// quest credit, as "!boss kill" removes it (inferred: a GM kill is not a fight).
    /// </summary>
    private async Task<string> KillAsync(Entity e)
    {
        if (e.Player is { Progress: not null } p)
        {
            if (p.Dead) return $"{Name(e)} is already dead.";
            await Combat.KillPlayerAsync(p);
            return p.Dead ? $"{Name(e)} killed." : $"{Name(e)} is changing maps; try again.";
        }
        if (e.Unit is MonsterUnit m)
        {
            if (m.Dead) return $"{Name(e)} is already dead.";
            await Monsters.KillAsync(m);
            return $"{Name(e)} killed (no rewards).";
        }
        return Missing(e);
    }

    /// <summary>GmAddNpc: a monster of that NPCConfig id 4 m in front of the GM, outside the map's spawn groups.</summary>
    private async Task<string> AddNpcAsync(GameSession s, int npc)
    {
        // (inferred) It behaves as its AI says (the panel's dummies 5099000.. stand still); it does not engage the GM.
        if (Monsters.Template(npc) == null) return $"NPC {npc} is not a monster.";
        double angle = s.Face * Math.PI / 180;
        var at = new MapPoint(s.Position.X + (float)Math.Sin(angle) * 4, s.Position.Y, s.Position.Z + (float)Math.Cos(angle) * 4, s.Face + 180);
        if (await Monsters.SpawnSingleAsync(s.RuntimeMap, npc, at) is not { } unit) return $"Could not spawn {npc} here.";
        return $"Spawned {Monsters.NpcName(npc)} ({npc}, uid {unit.Uid}).";
    }

    // ---- attributes ----

    /// <summary>
    /// The entity's attributes as the server computes them (a player's full sheet; a monster's template with its live
    /// buffs, HP and level), or null when the panel cannot read them.
    /// </summary>
    private Dictionary<int, long>? Attributes(Entity e)
    {
        if (e.Player is { Progress: not null } p) return character.Stats(p).Data;
        if (e.Unit is not MonsterUnit m) return null;
        var values = Combat.MonsterStats(m).Data;
        values[AttrMaxSp] = m.Template.MaxSp;
        values[AttributeId.Hp] = m.Dead ? 0 : m.Hp;
        values[AttrLevel] = m.Template.Level;
        return values;
    }

    /// <summary>GmDumpEntityAttr (every non-zero attribute) and GmShowAttr (one, or all for 0): "id name value".</summary>
    private string ShowAttributes(Entity e, int attr)
    {
        if (Attributes(e) is not { } values) return Missing(e);
        List<KeyValuePair<int, long>> shown = attr != 0 ? [new(attr, values.GetValueOrDefault(attr))]
            : [.. values.Where(v => v.Value != 0).OrderBy(v => v.Key)];
        return $"{Name(e)}: " + string.Join(", ", shown.Select(v => $"{v.Key} {AttrName(v.Key)} {v.Value}"));
    }

    private string AttrName(int attr) => AttributeConfig.Get(data, attr)?.Variable ?? "?";

    /// <summary>
    /// GmSetEntityAttr (a value) and GmChangeEntityAttr (a change). HP/SP set the current amounts. A player's other
    /// attributes take the difference as a raw contribution through the ordinary stat pipeline, synced like a buff's
    /// (31008 and 31048), so derived stats follow. A monster's template copy takes the value. Session only: neither
    /// survives a relog or a respawn (inferred).
    /// </summary>
    private async Task<string> SetAttributeAsync(Entity e, int attr, long value, bool delta)
    {
        if (AttributeConfig.Get(data, attr) == null) return $"No attribute {attr} in AttributeConfig.";
        if (Attributes(e) is not { } values) return Missing(e);
        long before = values.GetValueOrDefault(attr), target = delta ? before + value : value;
        if (e.Player is { } p)
        {
            if (attr == AttrLevel) return "Set the level with GmSetLevel.";
            if (attr is AttributeId.Hp or AttributeId.Sp)
            {
                if (p.Dead) return $"{Name(e)} is dead.";
                await character.SetHpSpAsync(p, attr == AttributeId.Hp ? target : null, attr == AttributeId.Sp ? target : null);
            }
            else
            {
                HookGmAttributes();
                var changes = p.GmAttributes ??= [];
                // A percentage of the group scales the contribution: a few corrections bring the final value to the
                // asked one when the pipeline allows it (its rounding up can leave it a point off).
                long reached = before;
                for (int pass = 0; pass < 3 && reached != target; pass++)
                {
                    double total = changes.GetValueOrDefault(attr) + (target - reached);
                    if (total == 0) changes.Remove(attr);
                    else changes[attr] = total;
                    reached = character.Stats(p)[attr];
                }
                await character.RefreshStatsAsync(p);
            }
            return $"{Name(e)} {attr} {AttrName(attr)}: {before} -> {character.Stats(p)[attr]}.";
        }
        var m = (MonsterUnit)e.Unit!;
        if (m.Dead) return $"{Name(e)} is dead.";
        var changed = new AOIAttrChange { EntityUuid = (ulong)m.Uid };
        if (attr == AttributeId.Hp)
        {
            if (target <= 0) return await KillAsync(e);
            m.Hp = Math.Min(target, m.Template.MaxHp);
        }
        else if (WithAttribute(m.Template, attr, target) is { } template)
        {
            m.Template = template;
            if (m.Hp > template.MaxHp) m.Hp = template.MaxHp;
            changed.Property.Add(new AoIProperty { Idx = attr, Data = target, BaseData = target });
        }
        else return $"A monster has no attribute {attr} {AttrName(attr)} of its own to set.";
        changed.Property.Add(new AoIProperty { Idx = AttributeId.Hp, Data = m.Hp });
        await World.ToUnitViewersAsync(m, CombatService.AoiAttrChange, changed);
        return $"{Name(e)} {attr} {AttrName(attr)}: {before} -> {Attributes(e)!.GetValueOrDefault(attr)}.";
    }

    /// <summary>The monster template with one attribute's field changed; null for an attribute it does not carry.</summary>
    private static MonsterTemplate? WithAttribute(MonsterTemplate t, int attr, long v) => attr switch
    {
        AttributeId.MaxHp => t with { MaxHp = Math.Max(1, v) },
        AttrMaxSp => t with { MaxSp = v },
        AttributeId.Atk => t with { Atk = v },
        AttributeId.Matk => t with { Matk = v },
        AttributeId.Def => t with { Def = v },
        AttributeId.Mdef => t with { Mdef = v },
        AttributeId.Hit => t with { Hit = v },
        AttributeId.Flee => t with { Flee = v },
        AttributeId.Cri => t with { Cri = v },
        AttributeId.Speed => t with { SpeedCm = (int)Math.Clamp(v, 0, int.MaxValue) },
        AttributeId.CastSpeed => t with { CastSpeed = v },
        AttrLevel => t with { Level = (int)Math.Clamp(v, 1, int.MaxValue) },
        _ => null,
    };

    /// <summary>Registered on first use: a server whose GMs never change attributes runs no extra stat provider.</summary>
    private void HookGmAttributes()
    {
        if (_gmAttributesHooked) return;
        _gmAttributesHooked = true;
        character.ExtraAttributes.Add(p => p.GmAttributes is { } changes ? changes.Select(c => ((long)c.Key, c.Value)) : []);
    }

    // ---- buffs ----

    /// <summary>GmAddBuff: BuffService's ordinary placement (stacking, immunity), the GM as its source; duration in ms.</summary>
    private async Task<string> AddBuffAsync(GameSession s, Entity e, int buff, int durationMs)
    {
        if (BuffConfig.Get(data, buff) is not { } row) return $"No buff {buff} in BuffConfig.";
        if (durationMs < 0) throw new ArgumentException("the duration is milliseconds, 0 for the buff's own");
        bool applied;
        if (e.Player is { Role: not null } p)
        {
            await Buffs.AddAsync(p, buff, durationMs, s.Role!.Id);
            applied = p.Buffs.Any(b => b.ConfigId == buff);
        }
        else if (e.Unit is { } unit)
        {
            await Buffs.AddSupportAsync(unit, buff, durationMs, s.Role!.Id);
            applied = Buffs.UnitBuffs(unit).Any(b => b.ConfigId == buff);
        }
        else return Missing(e);
        return applied ? $"Buff {buff} {BuffName(row)} on {Name(e)}." : $"Buff {buff} was not applied to {Name(e)} (immune or resisted).";
    }

    /// <summary>
    /// GmRemoveBuff: every stack of that BuffConfig id, or with "by stacks" that many piles of its group (BuffService's
    /// pile consumption). A unit drops the whole buff either way: unit buffs have no partial removal (inferred).
    /// </summary>
    private async Task<string> RemoveBuffAsync(Entity e, int buff, bool byStacks, int stacks)
    {
        if (BuffConfig.Get(data, buff) is not { } row) return $"No buff {buff} in BuffConfig.";
        if (e.Player is { Role: not null } p)
        {
            var live = p.Buffs.Where(b => b.ConfigId == buff).ToArray();
            if (live.Length == 0) return $"{Name(e)} has no buff {buff}.";
            int piles = live.Sum(b => b.PileCount);
            if (byStacks && stacks > 0 && stacks < piles)
            {
                await Buffs.ConsumeBuffPilesAsync(p, (int)row.BuffId, stacks);
                return $"Buff {buff} {BuffName(row)} on {Name(e)}: {stacks} of {piles} stacks removed.";
            }
            foreach (var b in live) await Buffs.RemoveAsync(p, b);
        }
        else if (e.Unit is { } unit)
        {
            if (!Buffs.UnitBuffs(unit).Any(b => b.ConfigId == buff)) return $"{Name(e)} has no buff {buff}.";
            await Buffs.RemoveUnitConfigAsync(unit, buff);
        }
        else return Missing(e);
        return $"Buff {buff} {BuffName(row)} removed from {Name(e)}.";
    }

    /// <summary>GmShowBuff: the live buffs with their stacks and remaining seconds.</summary>
    private string ShowBuffs(Entity e)
    {
        IReadOnlyList<ActiveBuff>? list = e.Player is { } p ? p.Buffs : e.Unit is { } unit ? Buffs.UnitBuffs(unit) : null;
        if (list == null) return Missing(e);
        long now = MonsterService.NowMs;
        var live = list.Where(b => b.ExpireMs > now).ToList();
        if (live.Count == 0) return $"{Name(e)} has no buffs.";
        return $"{Name(e)}: " + string.Join(", ", live.Select(b =>
            $"{b.ConfigId} {(BuffConfig.Get(data, b.ConfigId) is { } row ? BuffName(row) : "")}"
            + (b.PileCount > 1 ? $" x{b.PileCount}" : "")
            + (b.ExpireMs == long.MaxValue ? " permanent" : $" {(b.ExpireMs - now + 999) / 1000}s")));
    }

    private string BuffName(BuffConfig row) => row.Name is [var id, ..] ? data.Text((long)id) : "";

    // ---- switches, casts, camps ----

    /// <summary>
    /// GmNoCD, GmDamageFix, GmUnlimit: the GM's own switch, kept for the session. Turning "no cooldown" on also ends the
    /// cooldowns already running, so the skill bar frees at once (inferred).
    /// </summary>
    private static async Task<string> SwitchAsync(GameSession s, GmCombatSwitches flag, bool on)
    {
        s.GmCombat = on ? s.GmCombat | flag : s.GmCombat & ~flag;
        if (on && flag == GmCombatSwitches.NoCooldown)
            foreach (int group in s.SkillCds.Keys.Concat(s.SkillModeCds.Keys.Select(k => k.Group)).Distinct().ToArray())
                await CombatService.ResetCooldownAsync(s, group);
        string what = flag switch
        {
            GmCombatSwitches.NoCooldown => "No cooldown",
            GmCombatSwitches.FixedDamage => "Fixed damage (no variance)",
            _ => "Invincible, no costs",
        };
        return $"{what} {(on ? "on" : "off")} for {s.Role!.Name}.";
    }

    /// <summary>
    /// GmCastSkill (the GM casts at herself) and GmTargetCastSkill (the target casts at the GM, the button's default
    /// target): the ordinary 31102 path, so the client sees the accepted cast or the usual refusal. A player may use the
    /// skill unlearned for this one cast, and a monster casts only a skill of its own boss set (inferred both).
    /// </summary>
    private async Task<string> CastAsync(GameSession s, Entity caster, int skill)
    {
        if (SkillConfig.Get(data, skill) is not { } row) return $"No skill {skill} in SkillConfig.";
        string name = row.Name is [var text, ..] ? data.Text(text) : "";
        if (caster.Unit is MonsterUnit m) return m.Dead ? $"{Name(caster)} is dead." : Monsters.GmCastBossSkill(m, s, skill);
        if (caster.Player is not { Role: not null, Progress: not null } p) return Missing(caster);
        var before = p.SkillCast;
        p.GmCastSkill = skill;
        try
        {
            await Combat.CastAsync(p, new CastSkillReq { SkillId = skill, TargetId = s.Role!.Id, TargetPos = s.Position.Clone(), Face = p.Face });
        }
        finally { p.GmCastSkill = 0; }
        return p.SkillCast is { } cast && !ReferenceEquals(cast, before)
            ? $"{Name(caster)} casts {skill} {name}." : $"{Name(caster)} could not cast {skill} {name} now (the client shows the reason).";
    }

    /// <summary>
    /// GmSetEntityCamp (the panel's options: 0 neutral, 1 player, 2 monster): a monster's camp as a scene script sets it
    /// (31070, WorldEffectsService.SetCampAsync). A player's camp is her event faction, which owns the PvP sides, so the
    /// panel leaves it alone (inferred).
    /// </summary>
    private async Task<string> SetCampAsync(Entity e, int camp)
    {
        if (camp < 0) throw new ArgumentException("the camp is 0 (neutral), 1 (player) or 2 (monster)");
        if (e.Player != null) return "A player's camp follows her event faction; GmSetEntityCamp sets monsters only.";
        if (e.Unit is not MonsterUnit m) return Missing(e);
        if (m.Dead) return $"{Name(e)} is dead.";
        m.CampOverride = camp;
        await World.ToUnitViewersAsync(m, WorldEffectsService.Camp, new EntityChangeCampNotify { EntityId = m.Uid, Camp = camp });
        return $"{Name(e)} camp {camp}.";
    }
}
