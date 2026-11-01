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
/// as long as the copy is socketed, which publishes their icons, applies their _kChangeAttri (Variable CT, 2024001..4)
/// and arms BuffService's lethal-hit revive for groups 20520/20521 (2052001.., 2052101..). (inferred) Their
/// _iSceneClear removes them on a scene change; the loaded scene registers them again, carrying the revive cooldown.
///
/// Executed behaviours (data-settled families; the trigger timing "when dealing damage" is read from the
/// descriptions and applied to every damaging hit on a monster, inferred):
/// - status on hit: a base passive with _kLogicData [0, status buff, ms] (2000001 Stun 901601 500 ms, Blind, Curse,
///   Immobilize, Sleep, Freeze, Silence) gains chance from every passive whose _kBuffAttributeList row
///   ["0", base group, "4", "1", chance, "0"] names its group ("Chance stacks with multiple cards"); the description's
///   third value is the internal cooldown in seconds. The status lands as a monster unit buff, as skill _kBuff2 does.
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
/// </summary>
public sealed partial class CardEffects
{
    private readonly GameData _data;
    private readonly CharacterService _character;
    private readonly BuffService _buffs;
    private readonly SkillService _skills;
    private readonly CombatService _combat;
    private readonly ConditionalWeakTable<GameSession, Runtime> _runtime = new();

    private sealed class Runtime
    {
        public long RoleId;
        public object? Epoch;
        /// <summary>Last registered buff GUID per (card GUID, config), to carry BuffEffectCds over a re-registration.</summary>
        public readonly Dictionary<(long Source, int Config), long> Registered = new();
        public readonly Dictionary<long, long> Cooldowns = new(); // proc family (base buff group) -> ready at
        public bool ResolvingAutocast;
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

    // A test can fix each ten-thousandth roll without changing the production probability.
    internal Func<int, int> Roll { get; set; } = bound => Random.Shared.Next(bound);

    public CardEffects(GameData data, CharacterService character, BuffService buffs, CardService cards, CombatService combat,
        SceneService scenes, SkillService skills)
    {
        this._data = data;
        this._skills = skills;
        this._character = character;
        this._buffs = buffs;
        this._combat = combat;
        cards.SocketsChanged.Add(RefreshAsync);
        scenes.Loaded.Add(RefreshAsync);
        combat.DamageAdjusters.Add(AdjustDamage);
        combat.DealtAsync.Add(OnDealtAsync);
        InitializeReflection();
        InitializeOffence(cards, scenes);
        InitializeSkillEvents();
        InitializeDefence();
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
    /// star-up, scene load).</summary>
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
        ? list.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray() : [];

    private static double[] Description(JsonElement row) => row.TryGetProperty("_kDescData", out var list) && list.ValueKind == JsonValueKind.Array
        ? list.EnumerateArray().Select(v => GameData.Number(v) ?? 0).ToArray() : [];

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

    internal async Task OnDealtAsync(GameSession s, MonsterUnit target, JsonElement skillRow, long damage)
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
                if (target.Dead || target.Hp <= 0) continue;
                long cooldown = desc.Length >= 3 ? (long)(desc[2] * 1000) : 0;
                if (Roll(10000) >= chance || !Ready(s, group, cooldown)) continue;
                await _buffs.ShowOnUnitAsync(target, (int)logic[1], (int)logic[2], s.Role.Id);
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
        await AutocastOnDamageAsync(s, target, skillRow, passives);
    }

    /// <summary>"Ignores monster Size. Damage against Small, Medium, and Large monsters +X%" (2050101..4 always,
    /// 2050001..4 with a chance). Size 0 monsters are unaffected (inferred from the three named sizes).</summary>
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
