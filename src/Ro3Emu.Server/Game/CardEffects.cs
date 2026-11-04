using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Socketed cards' Buff(2) special effects (CardConfig._kSkillAttribute -> SpecialEffectConfig._kSpecialEffect [2, buff],
/// 304 of the 1,340 card operations; Attr(1) and Attr1CountAddAttr2(8) are EquipmentStunts'). Every referenced BuffConfig
/// row is a zero-time passive (iPassiveBuff 1): like totem passives they are registered per source (the card GUID) for
/// as long as the copy is socketed in an active socket (EquipmentStunts.ActiveCards), which publishes their icons, applies their _kChangeAttri (Variable CT, 2024001..4)
/// and arms BuffService's lethal-hit revive for groups 20520/20521 (2052001.., 2052101..). (inferred) Their
/// _iSceneClear removes them on a scene change; the loaded scene registers them again, carrying the revive cooldown.
///
/// Executed behaviours (data-settled families; the trigger timing "when dealing damage" is read from the
/// descriptions and applied to every damaging hit on a monster or a player, inferred):
/// - status on hit: a base passive with _kLogicData [0, status buff, ms] (2000001 Stun 901601 500 ms, Blind, Curse,
///   Immobilize, Sleep, Freeze, Silence) gains chance from every passive whose _kBuffAttributeList row
///   ["0", base group, "4", "1", chance, "0"] names its group ("Chance stacks with multiple cards"); the description's
///   third value is the internal cooldown in seconds. The status lands as a monster unit buff, as skill _kBuff2 does, or on a player
///   through the target's tag resistance. Blind limits a monster's sight (BuffService.Blind); on a player it stays an icon.
/// - life steal 20170 (2017101..): chance from the same modifier rows, heal = desc[1]% of the damage up to desc[2],
///   internal cooldown desc[3] seconds.
/// - SP absorb 2050401: _kLogicData [chance bp, ...], desc[1]% of the damage as SP.
/// - size: 20501 (2050101..) adds _kLogicData[0] bp of damage against Small/Medium/Large (NPCConfig._iSize 1..3)
///   monsters; 20500 (2050001..) adds _kLogicData[1] bp with a _kLogicData[0] bp chance.
/// - 物理命中觸發 Bash、Jupitel Thunder；普通攻擊觸發 Double Attack，依各星階機率及共用內置冷卻。
/// 其他卡片行為分別在 Offence、Defence、Reflection、SkillEvents partial：依 NPC 標籤增傷、
/// 普攻濺射、暴擊後增傷、鎧甲轉屬性、魔法無效、玩家單體反射、控制免疫、霸體、冷卻重置、
/// 扣除目標 SP 與治療觸發。各模組註明從原表確認及伺服器端推定的範圍。
/// 尚未補齊：怪物限定隱匿偵測的同步／選取流程；裝備破壞事件不存在，不能宣稱不壞卡已實際阻止破壞。
/// Skill(3) grants (Heal, Disguise, Magnum Break, Envenom, Sight, Sightless Mind) go into RoleProgress.GrantedSkills.
///
/// - Splash: 20508 (Shadow Card: Baphomet, 2050801..) and 20509 (Card: Baphomet, 2050901..), see <see cref="SplashOf"/>.
/// - monster family: Orc Lady, Kobold Leader, Goblin Leader, Aster, Crab, Shellfish add _kLogicData[0] bp of damage against
///   monsters with their NPCConfig._kNpcTag, see <see cref="FamilyTag"/>.
/// - autocast on a Physical attack: 20190 Bash, 20200 Jupitel Thunder; Double Attack on a Basic Attack: 20210, see
///   <see cref="AutocastsAsync"/>. Divine Light on a heal: 20531, see <see cref="HealAutocastAsync"/>.
/// - cooldown reset after a skill: 20529, see <see cref="CooldownResetAsync"/>.
/// - next hit after a crit: 20507, see <see cref="NextHitBonus"/>.
/// - armor element: the cards' passives 9044xx and 20515 are read by CombatService.ArmorElement on every hit a player takes;
///   Shadow Card: Angeling's 20514 puts 904407 on for seconds, see <see cref="OnHitTakenAsync"/>.
/// - what a hit on the holder becomes: Golden Thief Bug's nullify and Ladybug's reduction, see <see cref="AdjustDamageTaken"/>.
/// - reflect on a player's single-target skill: Maya, Orc Lord, see <see cref="ReflectAsync"/>.
/// - SP burn on a player: Dark Priest, see <see cref="BurnSpAsync"/>.
/// - status immunity: Moonlight Flower's Slow, Orc Hero's Stun, by group in BuffService.ImmuneTo, the rows having only their text.
/// - Endure: Eddga, by group in BuffService, no knockback or knock-up. Its hitstun is the client's.
/// Status, life steal, SP absorb, autocast, Double Attack and the next hit after a crit answer a hit on a monster or a player. Size and
/// family name monsters. Divine Light and the cooldown reset follow the own cast whoever it reached. Splash takes monsters only.
/// No effect possible: indestructible gear (nothing breaks gear), and Maya Purple's "Reveals hidden or cloaked monsters": no NPC or AI row
/// holds a hiding buff or a hiding skill, its 5000901 and 5002101 are applied by nothing, and the server never hides a monster.
/// Skill(3) grants (Heal, Disguise, Magnum Break, Envenom, Sight, Sightless Mind) go into RoleProgress.GrantedSkills. The granted Magnum Break's
/// buff makes Basic Attacks Fire (CombatService.ElementFromBuff), Envenom follows with its Poisoned row (CombatService.EnvenomPoisonAsync).
/// Card: Kraken's "has a Chance to inflict Bleeding" has no tuple and no number, so only its always-Stun runs.
/// </summary>
public sealed partial class CardEffects
{
    private readonly GameData _data;
    private readonly CharacterService _character;
    private readonly BuffService _buffs;
    private readonly SkillService _skills;
    private readonly CombatService _combat;
    private readonly SessionRegistry _sessions;
    private readonly ConditionalWeakTable<GameSession, Runtime> _runtime = [];

    private sealed class Runtime
    {
        public long RoleId;
        public object? Epoch;
        /// <summary>Last registered buff GUID per (card GUID, config), to carry BuffEffectCds over a re-registration.</summary>
        public readonly Dictionary<(long Source, int Config), long> Registered = [];
        public readonly Dictionary<long, long> Cooldowns = []; // proc family (base buff group) -> ready at
        public bool ResolvingAutocast;
        public ActiveBuff? NextHitSpent;
        public (int Buff, int Ms)? NextHitArmed;
    }

    // 選角會重建 SkillProtocol 並清技能 CD；卡片事件也隨同重建，切圖及保留工作階段的重連不清空。
    private Runtime RuntimeFor(GameSession s)
    {
        if (!_runtime.TryGetValue(s, out var state) || state.RoleId != s.Role?.Id || !ReferenceEquals(state.Epoch, s.SkillProtocol))
            _runtime.AddOrUpdate(s, state = new Runtime { RoleId = s.Role?.Id ?? 0, Epoch = s.SkillProtocol });
        return state;
    }

    private sealed record Passive(InvItem Card, int Id, JsonElement Row);

    private const long SkillOp = 3;
    private const int LifeStealGroup = 20170, SpAbsorb = 2050401, SizeAlways = 20501, SizeChance = 20500;
    private const int SplashChance = 20508, SplashAlways = 20509, NextHitAfterCrit = 20507;
    private const int BashAutocast = 20190, JupitelAutocast = 20200, HealAutocast = 20531, CooldownReset = 20529;
    private const int ShadowNullify = 20510, CardNullify = 20511, Ladybug = 20260;
    private const int ShadowMagicReflect = 20512, CardMagicReflect = 20513, ShadowPhysicalReflect = 20516, CardPhysicalReflect = 20517;
    private const long SingleTargetTag = 102; // SkillTagConfig "ST"
    private const long MagicDamage = 2; // SkillConfig._iDmgType
    private const int SilencedState = 20; // SpecialStatusConfig 20, BuffService.StateFor
    private const int Vit = 3, Sp = 2001; // AttributeConfig
    private const int SpBurn = 20230, ArmorOnHit = 20514;
    private const long Melee = 1; // SkillConfig._iDistanceType (G_ClientCfg.SkillDistanceType: Remote is 2)
    private const long PhysicalDamage = 1; // SkillConfig._iDmgType

    // A test can fix each ten-thousandth roll without changing the production probability.
    internal Func<int, int> Roll { get; set; } = bound => Random.Shared.Next(bound);

    public CardEffects(GameData data, CharacterService character, BuffService buffs, CardService cards, CombatService combat,
        SceneService scenes, SkillService skills, SessionRegistry sessions)
    {
        this._data = data;
        this._sessions = sessions;
        this._skills = skills;
        this._character = character;
        this._buffs = buffs;
        this._combat = combat;
        cards.SocketsChanged.Add(RefreshAsync);
        character.WornGearChanged.Add(RefreshAsync);
        scenes.Loaded.Add(RefreshAsync);
        combat.DamageAdjusters.Add(AdjustDamage);
        combat.DealtAsync.Add(OnDealtAsync);
        InitializeReflection();
        InitializeOffence(cards, scenes);
        InitializeAutocasts();
        InitializeSkillEvents();
        InitializeDefence();
        combat.PvpHitLanded.Add(OnPvpHitAsync);
    }

    private IEnumerable<Passive> Passives(GameSession s)
    {
        foreach (var (card, id) in EquipmentStunts.CardEffects(s, _data))
            if (_data.Row("SpecialEffectConfig", id) is { } effect)
                foreach (var op in EquipmentAffixes.Rows(effect, "_kSpecialEffect"))
                    if (op.Length == 2 && op[0] == 2 && _data.Row("BuffConfig", op[1]) is { } buff)
                        yield return new Passive(card, (int)op[1], buff);
    }

    /// <summary>The passives currently registered for this session's socketed cards.</summary>
    internal IReadOnlyList<(long Source, int Config)> Registered(GameSession s) =>
        s.Buffs.Where(b => b.IsPassive && !b.IsTotemOwned).Select(b => (b.SourceId, b.ConfigId)).ToList();

    /// <summary>Reconcile card-owned passives with the socketed copies (insert, remove, transfer, plan switch,
    /// star-up, worn gear, scene load).</summary>
    public async Task RefreshAsync(GameSession s)
    {
        if (s.Role == null) return;
        var wanted = Passives(s).Select(p => (Source: p.Card.Guid, p.Id)).ToHashSet();
        var state = RuntimeFor(s);
        bool statsChanged = false;
        foreach (var stale in s.Buffs.Where(b => b.IsPassive && !b.IsTotemOwned && !wanted.Contains((b.SourceId, b.ConfigId))).ToArray())
        {
            statsChanged |= stale.Changes.Count > 0;
            state.Registered.Remove((stale.SourceId, stale.ConfigId));
            await _buffs.RemoveAsync(s, stale);
        }
        foreach (var (source, id) in wanted)
        {
            if (s.Buffs.Any(b => b.IsPassive && !b.IsTotemOwned && b.SourceId == source && b.ConfigId == id)) continue;
            await _buffs.AddPassiveAsync(s, id, source, totemOwned: false);
            if (s.Buffs.FirstOrDefault(b => b.IsPassive && !b.IsTotemOwned && b.SourceId == source && b.ConfigId == id) is not { } added)
                continue;
            statsChanged |= added.Changes.Count > 0;
            // A scene change cleared the previous registration; its internal cooldown stays with the card.
            if (state.Registered.TryGetValue((source, id), out long previous) && s.BuffEffectCds.Remove(previous, out long readyAt))
                s.BuffEffectCds[added.Guid] = readyAt;
            state.Registered[(source, id)] = added.Guid;
        }
        await GrantSkillsAsync(s);
        if (!statsChanged) return;
        if (s.SceneLoading) _character.Refresh(s);
        else await _character.RefreshStatsAsync(s);
    }

    /// <summary>SpecialEffectConfig op Skill(3) [3, SkillConfig row] (enum_SpecialEffectType.Skill): the socketed card grants
    /// that row's group at its level while it stays socketed (Heal, Disguise, Magnum Break, Envenom, Sight, Sightless Mind;
    /// _iSystemType 3, enum_SkillSystemType.CardSkill). The skill panel lists them as card skills and they can be loaded
    /// and cast; the highest level of a group counts.</summary>
    private async Task GrantSkillsAsync(GameSession s)
    {
        if (RefreshGrantedSkills(s, _data) && s.SkillPlans != null) await _skills.SyncAsync(s);
    }

    /// <summary>由目前卡槽重建暫時技能權限。登入讀取快捷列前也必須先執行，避免將合法卡片技能當成未學習而移除。</summary>
    internal static bool RefreshGrantedSkills(GameSession s, GameData data)
    {
        if (s.Progress is not { } progress) return false;
        Dictionary<int, int> granted = [];
        foreach (var (_, id) in EquipmentStunts.CardEffects(s, data))
            if (SpecialEffectConfig.Get(data, id) is { } effect)
                foreach (var op in effect.SpecialEffect)
                    if (op is [SkillOp, var row] && SkillConfig.Get(data, row) is { SkillId: > 0 } skill)
                        granted[(int)skill.SkillId] = Math.Max(granted.GetValueOrDefault((int)skill.SkillId), (int)skill.Level);
        if (granted.Count == progress.GrantedSkills.Count && granted.All(g => progress.GrantedSkills.GetValueOrDefault(g.Key) == g.Value)) return false;
        progress.GrantedSkills.Clear();
        foreach (var (group, level) in granted) progress.GrantedSkills[group] = level;
        return true;
    }

    private static long[] Numbers(JsonElement row, string field) => row.TryGetProperty(field, out var list) && list.ValueKind == JsonValueKind.Array
        ? [.. list.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0))] : [];

    private static double[] Description(JsonElement row) => row.TryGetProperty("_kDescData", out var list) && list.ValueKind == JsonValueKind.Array
        ? [.. list.EnumerateArray().Select(v => GameData.Number(v) ?? 0)] : [];

    /// <summary>_kBuffAttributeList ["0", group, "4", "1", value, "0"]: the passive adds value to field 4 (the trigger
    /// chance, basis points) of the passive group named.</summary>
    private static IEnumerable<(long Group, long Value)> ChanceModifiers(JsonElement row)
    {
        if (!row.TryGetProperty("_kBuffAttributeList", out var list) || list.ValueKind != JsonValueKind.Array) yield break;
        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() < 5) continue;
            var values = entry.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray();
            if (values[2] == 4 && values[3] == 1) yield return (values[1], values[4]);
        }
    }

    private bool Ready(GameSession s, long family, long cooldownMs)
    {
        var state = RuntimeFor(s);
        long now = MonsterService.NowMs;
        lock (state)
        {
            if (state.Cooldowns.GetValueOrDefault(family) > now) return false;
            if (cooldownMs > 0) state.Cooldowns[family] = now + cooldownMs;
            return true;
        }
    }

    /// <summary>Base proc passives and the chance / cooldown their modifiers give them.</summary>
    private IEnumerable<(JsonElement Base, long Group, long Chance, double[] Description)> Procs(IReadOnlyList<Passive> passives)
    {
        var modifiers = passives.SelectMany(p => ChanceModifiers(p.Row).Select(m => (m.Group, m.Value, Description: Description(p.Row)))).ToList();
        foreach (var basis in passives.DistinctBy(p => p.Id))
        {
            long group = GameData.Int(basis.Row, "_iBuffID");
            var own = modifiers.Where(m => m.Group == group).ToList();
            if (own.Count == 0) continue;
            yield return (basis.Row, group, own.Sum(m => m.Value), own.OrderByDescending(m => m.Description.Length).First().Description);
        }
    }

    /// <summary>What <see cref="NextHitBonus"/> decided for the hit just dealt: spends the buff, arms it again on a crit.</summary>
    private async Task SettleNextHitAsync(GameSession s)
    {
        if (!_runtime.TryGetValue(s, out var state)) return;
        if (state.NextHitSpent is { } spent)
        {
            state.NextHitSpent = null;
            await _buffs.RemoveAsync(s, spent);
        }
        if (state.NextHitArmed is not { } armed) return;
        state.NextHitArmed = null;
        if (s.Role != null && !s.Dead) await _buffs.AddAsync(s, armed.Buff, armed.Ms, s.Role.Id);
    }

    internal async Task OnDealtAsync(GameSession s, MonsterUnit target, JsonElement skillRow, long damage)
    {
        if (RuntimeFor(s).ResolvingAutocast) return;
        await SettleNextHitAsync(s);
        await DealtProcsAsync(s, damage, () => !target.Dead && target.Hp > 0, (id, ms) => _buffs.ShowOnUnitAsync(target, id, ms, s.Role!.Id));
    }

    /// <summary>The same procs on a player's hit, "When dealing damage" having no word on whom. A fixed hit (a reflect, a guardian's)
    /// sets off nothing. The status lands through <see cref="CombatService.LandCardStatusAsync"/>; a lethal hit leaves no one to hold it.</summary>
    private async Task OnPvpHitAsync(GameSession s, GameSession target, JsonElement row, long damage, bool lethal)
    {
        if (new SkillConfig(row).IsFixedHit || RuntimeFor(s).ResolvingAutocast) return;
        // The local offence/skill-event hooks own critical bonuses and SP burn for player hits.
        await DealtProcsAsync(s, damage, () => !lethal && !target.Dead, (id, ms) => _combat.LandCardStatusAsync(s, target, id, ms));
    }

    internal static long SpBurned(long sp, long percent, long most) => Math.Min(most, sp * percent / 100);

    /// <summary>
    /// Dark Priest: "When dealing DMG, there is a @{1}% Chance to reduce the target's current SP by @{2}%, up to @{3}. Internal Cooldown: @{4}
    /// sec. The trigger Chance stacks with multiple Cards." The chance as for a status on hit; base 2023001's [0, 2003101] names the row
    /// "SP Reduction", whose _kLogicData [20, 400] is the share and the most. A monster has no current SP, so only a player's hit rolls.
    /// (inferred) The row's show 20230 is not played: nothing on the wire carries an SP loss, so the target's SP bar is what shows it.
    /// </summary>
    private async Task BurnSpAsync(GameSession s, GameSession target)
    {
        foreach (var (basis, group, chance, desc) in Procs([.. Passives(s)]))
        {
            if (group != SpBurn || new BuffConfig(basis).LogicData is not [_, var id] || SkillConfig.Get(_data, id) is not { LogicData: [var percent, var most] }
                || desc.Length == 0 || Roll(10000) >= chance || !Ready(s, SpBurn, (long)(desc[^1] * 1000))) continue;
            long sp = _character.Stats(target)[Sp];
            await _character.SetHpSpAsync(target, null, sp - SpBurned(sp, percent, most));
        }
    }

    private async Task DealtProcsAsync(GameSession s, long damage, Func<bool> targetLives, Func<int, int, Task> landStatus)
    {
        if (damage <= 0 || s.Role == null || s.Dead) return;
        var passives = Passives(s).ToList();
        if (passives.Count == 0) return;
        foreach (var (basis, group, chance, desc) in Procs(passives))
        {
            var logic = Numbers(basis, "_kLogicData");
            if (group == LifeStealGroup)
            {
                // "has a @{1}% Chance to restore Life equal to @{2}% of the damage dealt, up to @{3}. Cooldown @{4} sec."
                if (desc.Length < 4 || Roll(10000) >= chance || !Ready(s, group, (long)(desc[3] * 1000))) continue;
                long heal = Math.Min((long)desc[2], (long)Math.Floor(damage * desc[1] / 100));
                if (heal <= 0) continue;
                var stats = _character.Stats(s);
                await _character.SetHpSpAsync(s, stats[2000] + heal, null);
            }
            else if (logic.Length == 3 && logic[0] == 0 && logic[2] > 0 && _data.Row("BuffConfig", logic[1]) is not null)
            {
                // "has a @{1}% Chance to inflict a status for @{2} sec. Internal Cooldown: @{3} sec."
                if (!targetLives()) continue;
                long cooldown = desc.Length >= 3 ? (long)(desc[2] * 1000) : 0;
                if (Roll(10000) >= chance || !Ready(s, group, cooldown)) continue;
                await landStatus((int)logic[1], (int)logic[2]);
            }
        }
        foreach (var passive in passives.Where(p => p.Id == SpAbsorb).DistinctBy(p => p.Id))
        {
            // "has a @{1}% Chance to absorb @{2}% of the damage dealt to the enemy as SP."
            var logic = Numbers(passive.Row, "_kLogicData");
            var desc = Description(passive.Row);
            if (logic.Length < 1 || desc.Length < 2 || Roll(10000) >= logic[0]) continue;
            long sp = (long)Math.Floor(damage * desc[1] / 100);
            if (sp <= 0) continue;
            var stats = _character.Stats(s);
            await _character.SetHpSpAsync(s, null, stats[2001] + sp);
        }
    }

    /// <summary>
    /// Golden Thief Bug: "When hit by a magic attack, has a @{1}% Chance to nullify its damage. Internal Cooldown: @{2} seconds. (Boss
    /// instant-kill skills can bypass this card's effect.)" _kLogicData [chance bp], the cooldown in _kDescData; the two cards are two
    /// groups, each with its own. A magic attack is a hit of an _iDmgType 2 row from a monster or a player. (inferred) No row is named an
    /// instant kill, so what bypasses it is a raid mechanic's share of maximum HP (MonsterSkillHitAsync), and a tick or a fixed amount
    /// is no attack. (inferred) The hit shows as 0, as under Safety Wall: DamageHitResult has no immune value.
    /// Ladybug: "Damage taken while Silenced -@{1}%", _kLogicData [bp], any damage while the Silenced state holds.
    /// </summary>
    internal long AdjustDamageTaken(GameSession s, JsonElement? row, bool directHit, long damage)
    {
        long reduction = 0;
        foreach (var passive in Passives(s).DistinctBy(p => p.Id))
        {
            var buff = new BuffConfig(passive.Row);
            if (buff.BuffId == Ladybug && buff.LogicData is [var bp, ..] && s.Buffs.Any(b => b.StateOrdinal == SilencedState && b.ExpireMs > MonsterService.NowMs))
                reduction += bp;
            else if (buff.BuffId is ShadowNullify or CardNullify && directHit && SkillShieldRules.IsMagic(row) && buff.LogicData is [var chance, ..]
                && Description(passive.Row) is [_, var cooldown, ..] && Roll(10000) < chance && Ready(s, buff.BuffId, (long)(cooldown * 1000)))
                return 0;
        }
        return damage - (long)Math.Floor(damage * reduction / 10000d);
    }

    /// <summary>The share of the damage taken a reflect row gives back: _kDamageParamExtra [_, _, bp per VIT, _, limit, _, share bp, 101,
    /// cap bp] on row 2051201.. "Magic Reflect - Card" and 2051601.. "Physical Reflect - Card" (T_0addAXY_XisExtra_YisAttri_MaxAttri_YisAttri_MaxAttri,
    /// script Rebound_Card, read against the texts, whose numbers they match at every star). (inferred) "up to @{5}%", which only the text
    /// has, stops the whole share and not only the VIT's part: Card: Maya T3 starts at 56%. The cap on the attacker's Max Life and the row's limit
    /// stop the amount. 0 for any other shape.</summary>
    internal static long ReflectedDamage(long damage, long vit, long attackerMaxHp, long[] tuple, double maxSharePercent)
    {
        if (tuple is not [_, _, var perVit, _, var limit, _, var share, _, var cap]) return 0;
        long total = perVit > 0 ? Math.Min(share + perVit * vit, (long)(maxSharePercent * 100)) : share;
        return Math.Min(limit, Math.Min((long)Math.Floor(damage * total / 10000d), (long)Math.Floor(attackerMaxHp * cap / 10000d)));
    }

    /// <summary>
    /// Maya (magic) and Orc Lord (Physical): "When hit by a player's ST magic skill, reflects @{1}% of the damage taken, up to @{2}% of the
    /// attacker's Max Life. For every @{3} VIT, reflects an Extra @{4}% of the damage taken, up to @{5}%. Internal Cooldown: @{6} seconds."
    /// (The Shadow cards have no VIT line.) So a monster's hit reflects nothing. The buff's _kLogicData names its row, and the hit goes out as
    /// that row's, a fixed amount. A row is ST when its panel says so (_kSkilltag 102). (inferred) A Basic Attack is no skill, as for
    /// Mistress; a fixed hit, which another reflect is, answers nothing, so two holders do not trade. A holder the hit killed reflects
    /// nothing, as <see cref="CombatService.ReactToHitAsync"/> reports only the living, and a reflect may kill.
    /// </summary>
    private Task ReflectAsync(GameSession s, GameSession? enemy, JsonElement? struck, long damage)
    {
        if (enemy?.Role == null || struck is not { } hit) return Task.CompletedTask;
        var skill = new SkillConfig(hit);
        if (skill.IsFixedHit || skill.IsNormalAttack(_data) || !skill.Skilltag.Contains(SingleTargetTag)) return Task.CompletedTask;
        foreach (var passive in Passives(s).DistinctBy(p => p.Id))
        {
            var buff = new BuffConfig(passive.Row);
            bool answers = skill.DmgType == MagicDamage ? buff.BuffId is ShadowMagicReflect or CardMagicReflect
                : skill.DmgType == PhysicalDamage && buff.BuffId is ShadowPhysicalReflect or CardPhysicalReflect;
            double[] desc = Description(passive.Row);
            if (!answers || desc.Length == 0 || buff.LogicData is not [var id, ..] || SkillConfig.Get(_data, id) is not { DamageParamExtra: [var tuple, ..] } row) continue;
            long amount = ReflectedDamage(damage, _character.Stats(s)[Vit], _character.Stats(enemy)[AttributeId.MaxHp], tuple, desc.Length >= 5 ? desc[4] : 0);
            if (amount <= 0 || !Ready(s, buff.BuffId, (long)(desc[^1] * 1000))) continue;
            _combat.StrikeBackLater(s, row, amount, enemy);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Shadow Card: Angeling: "When taking damage, has a @{1}% Chance to change your armor's element to Holy for @{2} seconds. Internal
    /// Cooldown: @{3} seconds." (T2, T3 have no chance in the text and 10000 in the row.) _kLogicData [chance bp, 904407, ms], the
    /// cooldown last in _kDescData. (inferred) A hit of a monster or a player that took something, not a tick of a poison: those
    /// two are what the hooks report. The hit that sets it off is already counted on the old element.
    /// </summary>
    private async Task OnHitTakenAsync(GameSession s, long damage)
    {
        if (damage <= 0 || s.Role == null || s.Dead) return;
        foreach (var passive in Passives(s).DistinctBy(p => p.Id))
        {
            var buff = new BuffConfig(passive.Row);
            if (buff.BuffId != ArmorOnHit || buff.LogicData is not [var chance, var armor, var ms] || Description(passive.Row) is not [.., var cooldown]
                || Roll(10000) >= chance || !Ready(s, ArmorOnHit, (long)(cooldown * 1000))) continue;
            await _buffs.AddAsync(s, (int)armor, (int)ms, s.Role.Id);
        }
    }

    /// <summary>One Splash passive's numbers. <paramref name="Chance"/> is in basis points, <paramref name="Ratio"/> too, of
    /// the triggering hit's damage.</summary>
    internal readonly record struct Splash(long Chance, long CooldownMs, float Radius, int Extra, long Ratio, long Effect);

    /// <summary>
    /// "Melee Basic Attacks [have a @{1}% Chance to] trigger a Splash effect within a @{2}-meter radius. (This Splash does
    /// not count as a Normal Attack effect.) Affects up to @{3} Extra targets, with damage reduced to @{4}% of the original.
    /// Internal Cooldown: @{5} seconds." The buff's _kLogicData is [chance bp, proc row] (20508) or [proc row] (20509), and
    /// its _kDescData ends with the cooldown. The proc row (_kScriptLogic "Jianshe", "Splash - Card") has _kLogicData
    /// [effect, radius cm], _iTargetMax the extra targets and _kDamageParamExtra[0]'s last value the ratio
    /// (T_0addAXY_XisExtra_YisAttri). Effects 2050802 and 2051102 are one prefab at scale 1 and 1.67, as 3 m and 5 m.
    /// </summary>
    internal static Splash? SplashOf(BuffConfig buff, SkillConfig row)
    {
        if (buff.BuffId is not (SplashChance or SplashAlways) || row.LogicData is not [var effect, var radius]
            || row.DamageParamExtra is not [[.., var ratio], ..] || buff.DescData is not [.., var cooldown]
            || !double.TryParse(cooldown, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)) return null;
        return new Splash(buff.LogicData.Length >= 2 ? buff.LogicData[0] : 10000, (long)(seconds * 1000), radius / 100f,
            (int)row.TargetMax, ratio, effect);
    }

    /// <summary>
    /// A melee normal attack that hit (31101 is the server's: the client asks only for the locked target, 31102, and shows the
    /// dsts it is told, LC_Scene_Sync._skillHit). The rest is (inferred) from the text and the row: the damage is the hit's
    /// on the locked target, "the original" (the formula's Extra; the row has no ratio of its own), so each extra target
    /// takes that share with no defense or element of its own; the area centres on the locked target, dead or alive; "Extra"
    /// leaves out what the swing itself hit; the extra cast triggers no CastResolved ("does not count as a Normal Attack
    /// effect"); each card has its own cooldown, which an empty area also starts; the gfx plays only when something is hit.
    /// </summary>
    private async Task SplashAsync(GameSession s, ActiveSkillCast action, SkillConfig cast, IReadOnlyList<MonsterUnit> processed,
        SkillHitInfo hit, IReadOnlyList<Passive> passives)
    {
        if (processed.Count == 0 || cast.DistanceType != Melee || !cast.IsNormalAttack(_data)) return;
        var primary = processed.FirstOrDefault(m => m.Uid == action.Request.TargetId) ?? processed[0];
        if (hit.Dsts.FirstOrDefault(d => d.DstID == primary.Uid) is not { Damage: > 0 } struck) return;
        foreach (var passive in passives)
        {
            var buff = new BuffConfig(passive.Row);
            if (buff.BuffId is not (SplashChance or SplashAlways) || buff.LogicData is not [.., var id]
                || SkillConfig.Get(_data, id) is not { } row || SplashOf(buff, row) is not { } splash
                || Roll(10000) >= splash.Chance || !Ready(s, buff.BuffId, splash.CooldownMs)) continue;
            var extra = _combat.MonstersNear(s, primary.Pos, splash.Radius).Where(m => !processed.Contains(m)).Take(splash.Extra).ToList();
            if (extra.Count == 0) continue;
            await _combat.SendGfxAtAsync(s, primary, splash.Effect, row.Id);
            long damage = Math.Max(1, (long)Math.Floor(struck.Damage * splash.Ratio / 10000d));
            await _combat.CastExtraAsync(s, row.AsFixedHit(damage), extra);
        }
    }

    /// <summary>The procs that follow a player's own cast. CastResolved fires once per cast and never for an extra cast, so
    /// nothing here can trigger itself or another proc of this list.</summary>
    private async Task OnCastResolvedAsync(GameSession s, ActiveSkillCast action, IReadOnlyList<MonsterUnit> processed, SkillHitInfo hit)
    {
        if (s.Role == null) return;
        var passives = Passives(s).ToList();
        if (passives.Count == 0) return;
        var cast = new SkillConfig(action.Row);
        await SplashAsync(s, action, cast, processed, hit, passives);
        await AutocastsAsync(s, action, cast, processed, hit, passives);
        await HealAutocastAsync(s, hit, passives);
        await CooldownResetAsync(s, cast, passives);
    }

    /// <summary>Who Divine Light heals, in order: "Restores Life ... for you and a designated ST ally" with _iTargetMax 2. (inferred)
    /// The designated ally is the first the triggering heal reached other than the caster, none if it healed only the caster.
    /// Class Heal has the same words with _iTargetMax 1 and heals only its target, so the 2 is what tells the two rows apart.</summary>
    internal static long[] DivineLightTargets(long caster, IEnumerable<DamageInfo> dsts) =>
        dsts.FirstOrDefault(d => d.DamageType == DamageType.Heal && d.DstID != caster) is { } ally ? [ally.DstID, caster] : [caster];

    /// <summary>
    /// Pharaoh: "When applying a healing skill effect, has a ${1}% Chance to trigger [Divine Light] Lv@{1}. Internal Cooldown: ${2}
    /// seconds." _kLogicData [chance bp, row], _kDescData [chance, cooldown]. (inferred) A healing skill effect is an own cast that
    /// healed someone, a full ally too. Heals by a ground field, a buff's ticks, an item or an extra cast do not reach CastResolved.
    /// </summary>
    private async Task HealAutocastAsync(GameSession s, SkillHitInfo hit, IReadOnlyList<Passive> passives)
    {
        if (!hit.Dsts.Any(d => d.DamageType == DamageType.Heal)) return;
        foreach (var passive in passives.DistinctBy(p => p.Id))
        {
            var buff = new BuffConfig(passive.Row);
            if (buff.BuffId != HealAutocast || buff.LogicData is not [var chance, var id] || SkillConfig.Get(_data, id) is not { } row
                || Description(passive.Row) is not [_, var cooldown, ..] || Roll(10000) >= chance || !Ready(s, HealAutocast, (long)(cooldown * 1000))) continue;
            List<GameSession> allies = [];
            foreach (long target in DivineLightTargets(s.Role!.Id, hit.Dsts).Take((int)Math.Max(1, row.TargetMax)))
                if (target == s.Role.Id) allies.Add(s);
                else if (_sessions.All().FirstOrDefault(p => p.Role?.Id == target) is { } ally) allies.Add(ally);
            await _combat.CastExtraOnAlliesAsync(s, row.Row, allies);
        }
    }

    /// <summary>The time a reset skill's cooldown cannot be reset again: the skill's cooldown times a multiple in basis points.</summary>
    internal static long ResetLockoutMs(long cooldownMs, long multiple) => cooldownMs * multiple / 10000;

    /// <summary>
    /// Mistress: "After using a skill, has a @{1}% Chance to reset that skill's Cooldown. Its Cooldown cannot be reset again for a
    /// period of time. The internal Cooldown is @{2} times that skill's Cooldown. (Does not reset the global Cooldown.)"
    /// _kLogicData [600, 0, 30000]: the chance, then 30000 as _kDescData's 3 times in basis points; the 0 has no recovered meaning.
    /// Each skill has its own lockout, from the reset on. Only its group's cooldown ends: _iPubCD, the global one, runs on under
    /// its own groups. (inferred) "That skill's Cooldown" is the time it was shown, after attribute 205. A skill with no
    /// cooldown running (none in its row, or a chant that outlasted it) has nothing to reset, and rolls nothing. A Basic Attack is
    /// not a skill here, and a cast that never reaches CastResolved (Resurrection, an own-skill script) does not count.
    /// </summary>
    private async Task CooldownResetAsync(GameSession s, SkillConfig cast, IReadOnlyList<Passive> passives)
    {
        if (cast.Cd <= 0 || cast.IsNormalAttack(_data)) return;
        int group = (int)cast.SkillId;
        long now = MonsterService.NowMs;
        if (!s.SkillModeCds.Any(c => c.Key.Group == group && c.Value > now)) return;
        foreach (var passive in passives.DistinctBy(p => p.Id))
        {
            var buff = new BuffConfig(passive.Row);
            if (buff.BuffId != CooldownReset || buff.LogicData is not [var chance, _, var multiple] || Roll(10000) >= chance) continue;
            long cooldown = CombatService.ReducedCooldown(cast.Cd, _character.Stats(s)[AttributeId.PerCd]);
            // Apart from the proc families' keys, which are buff groups.
            if (Ready(s, -group, ResetLockoutMs(cooldown, multiple))) await CombatService.ResetCooldownAsync(s, group);
        }
    }

    /// <summary>Whether a cast sets off the autocast passive of this group.</summary>
    internal static bool TriggersAutocast(long group, long damageType, bool normalAttack) => group switch
    {
        BashAutocast or JupitelAutocast => damageType == PhysicalDamage,
        DoubleAttackProc => normalAttack,
        _ => false,
    };

    /// <summary>
    /// Bongun and Wind Ghost: "When making a Physical attack, has a ${1}% Chance to autocast [Bash] Lv.@{1}. Internal Cooldown:
    /// ${2} sec. Chance stacks with multiple cards." The base passive (2019001, 2020001) holds [0, the row]; the tier's passive
    /// adds the chance to its group and has [chance, cooldown] as _kDescData. An extra cast of the row pays nothing and starts
    /// no cooldown of its own. (inferred) A Physical attack is any own cast of an _iDmgType 1 row that reached a monster or a player, a
    /// basic attack or a skill, a miss too, counted once per cast; the proc takes the locked target, or else the first the cast
    /// reached that is still alive, and with none it neither rolls nor starts its cooldown.
    /// Side Winder: "Basic Attack has a @{1}% Chance to trigger [Double Attack], dealing Extra Neutral melee Physical damage equal
    /// to @{2}%. Internal Cooldown: @{3} sec." goes the same way (2021001 [0, 2003001], _kDescData [chance, cooldown, ratio]). Class
    /// Double Attack (TalentProcs.DoubleAttackAsync) has to describe its hit, as its passive row has no damage; this row is that
    /// hit, _kDamageParam1 [5000], Neutral, melee, Physical. Its _iDistanceMax 2000 lets (inferred) a ranged Basic Attack set it off too.
    /// </summary>
    private async Task AutocastsAsync(GameSession s, ActiveSkillCast action, SkillConfig cast, IReadOnlyList<MonsterUnit> processed,
        SkillHitInfo hit, IReadOnlyList<Passive> passives)
    {
        if (hit.Dsts.Count == 0 && action.PlayersStruck.Length == 0) return;
        foreach (var (basis, group, chance, desc) in Procs(passives).ToList())
        {
            if (!TriggersAutocast(group, cast.DmgType, cast.IsNormalAttack(_data)) || desc.Length < 2 || new BuffConfig(basis).LogicData is not [_, var id]
                || SkillConfig.Get(_data, id) is not { } row) continue;
            var target = processed.FirstOrDefault(m => m.Uid == action.Request.TargetId && m.Hp > 0) ?? processed.FirstOrDefault(m => m.Hp > 0);
            var player = action.PlayersStruck.FirstOrDefault(p => p.Role?.Id == action.Request.TargetId && !p.Dead) ?? action.PlayersStruck.FirstOrDefault(p => !p.Dead);
            if (target == null && player == null || Roll(10000) >= chance || !Ready(s, group, (long)(desc[1] * 1000))) continue;
            var state = RuntimeFor(s);
            state.ResolvingAutocast = true;
            try
            {
                if (target != null) await _combat.CastExtraAsync(s, row.Row, [target]);
                else await _combat.CastExtraOnPlayerAsync(s, row, player!);
            }
            finally { state.ResolvingAutocast = false; }
        }
    }

    /// <summary>
    /// The NPCConfig._kNpcTag a family card's buff group counts on, 0 for any other. No table names the tags, so the link is
    /// (inferred) from who holds them: 1000 the six plain Orcs, leaving out Orc Lord and Orc Hero as "Damage against all Orcs
    /// (excluding Orc Lord and Orc Hero)" does; 1001 Kobolds; 1002 Goblins, with Rotar Zairo and Goblin Leader; 1003 Aster; 1004
    /// Crab ("Kara Crabs"); 1005 Shellfish. Only the tag counts, so the untagged copies of these monsters (24000xxx, 4940xxx)
    /// and the Kobold Leader do not.
    /// </summary>
    internal static long FamilyTag(long group) => group switch
    {
        >= 20150 and <= 20153 => 1000,
        >= 20220 and <= 20223 => 1001,
        20532 or 20533 => 1002,
        >= 20290 and <= 20293 => 1003,
        >= 20270 and <= 20273 => 1004,
        >= 20280 and <= 20283 => 1005,
        _ => 0,
    };

    /// <summary>
    /// Card: Doppelganger: "When you land a critical hit, your next instance of damage is increased by @{1}%." 2050701's _kLogicData
    /// [2091101, 2000] is the buff a crit puts on the player and its time; 2091101's own [500] is the raise, its gfx and sound the
    /// client plays from the 31104. (inferred) An instance is one monster's hit of a cast, any cast, extra ones too, so an area cast
    /// spends it on its second target. The first damaging hit within the 2 s spends it. A crit that spends it arms it again.
    /// A hit on a player arms and spends it alike, through <see cref="AdjustPvpDamage"/>.
    /// </summary>
    private long NextHitBonus(GameSession s, BuffConfig passive, DamageHitResult result)
    {
        if (passive.LogicData is not [var id, var ms]) return 0;
        var state = _runtime.GetOrCreateValue(s);
        if (result is DamageHitResult.Critical or DamageHitResult.DoubleCritical) state.NextHitArmed = ((int)id, (int)ms);
        long now = MonsterService.NowMs;
        if (s.Buffs.FirstOrDefault(b => b.ConfigId == id && b.ExpireMs > now && !ReferenceEquals(b, state.NextHitSpent)) is not { } live
            || BuffConfig.Get(_data, id)?.LogicData is not [var raise, ..]) return 0;
        state.NextHitSpent = live;
        return raise;
    }

    /// <summary>What of a monster hit's raises a hit on a player has: only the next hit after a crit, the rest naming monsters.</summary>
    private long AdjustPvpDamage(GameSession s, JsonElement row, DamageHitResult result, long damage)
    {
        if (new SkillConfig(row).IsFixedHit) return damage;
        long bonus = 0;
        foreach (var passive in Passives(s).DistinctBy(p => (p.Card.Guid, p.Id)))
            if (new BuffConfig(passive.Row) is { BuffId: NextHitAfterCrit } buff) bonus += NextHitBonus(s, buff, result);
        return bonus <= 0 ? damage : damage + (long)Math.Floor(damage * bonus / 10000d);
    }

    /// <summary>Size bonuses are counted here. The local offence hook counts family and critical bonuses after this
    /// adjuster, once per hit, including damage from the v1.0.29 formula.</summary>
    internal long AdjustDamage(GameSession s, MonsterUnit target, JsonElement skillRow, DamageHitResult result, long damage)
    {
        if (damage <= 0 || target.Template.Size is < 1 or > 3) return damage;
        long bonus = 0;
        foreach (var passive in Passives(s).DistinctBy(p => (p.Card.Guid, p.Id)))
        {
            long group = GameData.Int(passive.Row, "_iBuffID");
            var logic = Numbers(passive.Row, "_kLogicData");
            if (group == SizeAlways && logic.Length >= 1) bonus += logic[0];
            else if (group == SizeChance && logic.Length >= 2 && Roll(10000) < logic[0]) bonus += logic[1];
        }
        return bonus <= 0 ? damage : damage + (long)Math.Floor(damage * bonus / 10000d);
    }
}
