using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class CardEffects
{
    private const string SplashDamageField = "_ro3CardSplashDamage";
    private readonly ConditionalWeakTable<GameSession, OffenceState> _offence = [];

    private sealed class OffenceState
    {
        public long CardGuid, BuffGuid, ExpiresMs, BonusBp;
        public bool ResolvingSplash;
    }

    private void InitializeOffence(CardService cards, SceneService scenes)
    {
        _combat.DamageAdjusters.Add(AdjustOffenceDamage);
        _combat.MonsterSkillHitPublished.Add(OffenceHitPublishedAsync);
        _combat.PvpSkillHitPublished.Add(OffencePlayerHitPublishedAsync);
        _combat.PvpOutgoingDamageAdjusters.Add((s, _, row, result, amount) => ConsumeCriticalBonus(s, row, result, amount));
        _combat.CastDamageOverrides.Add((_, cast, _, _) =>
            cast.Row.TryGetProperty(SplashDamageField, out var amount) && amount.TryGetInt64(out long value)
                ? (Math.Max(0, value), DamageHitResult.Normal) : null);
        cards.SocketsChanged.Add(RefreshOffenceAsync);
        scenes.Loaded.Add(ClearOffenceAsync);
        _combat.PlayerDied.Add(ClearOffenceAsync);
    }

    /// <summary>配對 NPCConfig 的族群標籤；不用顯示名稱或模型推測未標記的副本複製怪。
    /// 1000 的原表集合不含獸人英雄及酋長；哥布靈首領明確有 1002。</summary>
    internal static int OffenceFamilyTag(long group) => group switch
    {
        >= 20150 and <= 20153 => 1000,
        >= 20220 and <= 20223 => 1001,
        >= 20270 and <= 20273 => 1004,
        >= 20280 and <= 20283 => 1005,
        >= 20290 and <= 20293 => 1003,
        20532 or 20533 => 1002,
        _ => 0,
    };

    private static long IncreaseByBasisPoints(long damage, long basisPoints) =>
        (long)Math.Min(long.MaxValue, damage + decimal.Floor((decimal)damage * basisPoints / 10000));

    /// <summary>族群加成依卡片實體加總（inferred）；暴擊後續增傷只取已發布前一擊所建立的待用狀態。
    /// 濺射已由原命中傷害換算，不能再套卡片族群／暴擊加成。</summary>
    internal long AdjustOffenceDamage(GameSession s, MonsterUnit target, JsonElement row, DamageHitResult result, long damage)
    {
        if (damage <= 0 || result == DamageHitResult.Miss || row.TryGetProperty(SplashDamageField, out _)) return damage;
        var passives = Passives(s).DistinctBy(p => (p.Card.Guid, p.Id)).ToArray();
        long familyBonus = 0;
        if (_data.Row("NPCConfig", target.Template.NpcId) is { } npc)
        {
            var tags = Numbers(npc, "_kNpcTag");
            foreach (var passive in passives)
            {
                int tag = OffenceFamilyTag(GameData.Int(passive.Row, "_iBuffID"));
                if (tag != 0 && tags.Contains(tag) && Numbers(passive.Row, "_kLogicData") is [var bonus, ..])
                    familyBonus += bonus;
            }
        }
        if (familyBonus > 0) damage = IncreaseByBasisPoints(damage, familyBonus);
        return ConsumeCriticalBonus(s, row, result, damage);
    }

    private long ConsumeCriticalBonus(GameSession s, JsonElement row, DamageHitResult result, long damage)
    {
        if (damage <= 0 || result == DamageHitResult.Miss || IsOffenceReaction(s, row)) return damage;
        if (_offence.TryGetValue(s, out var state) && state.ExpiresMs > MonsterService.NowMs && state.BonusBp > 0
            && Passives(s).Any(p => p.Card.Guid == state.CardGuid && GameData.Int(p.Row, "_iBuffID") == 20507)
            && s.Buffs.Any(b => b.Guid == state.BuffGuid && b.ExpireMs > MonsterService.NowMs))
        {
            damage = IncreaseByBasisPoints(damage, state.BonusBp);
            state.ExpiresMs = state.BonusBp = 0; // 本次已取用；發布後才清除圖示或由新暴擊重新建立。
        }
        return damage;
    }

    private bool IsOffenceReaction(GameSession s, JsonElement row) => row.TryGetProperty(SplashDamageField, out _)
        || row.TryGetProperty("_ro3CardReflection", out _) || RuntimeFor(s).ResolvingAutocast;

    private async Task RefreshOffenceAsync(GameSession s)
    {
        if (_offence.TryGetValue(s, out var state)
            && !Passives(s).Any(p => p.Card.Guid == state.CardGuid && GameData.Int(p.Row, "_iBuffID") == 20507))
            await ClearOffenceAsync(s);
    }

    private async Task ClearOffenceAsync(GameSession s)
    {
        if (!_offence.TryGetValue(s, out var state)) return;
        long guid = state.BuffGuid;
        state.CardGuid = state.BuffGuid = state.ExpiresMs = state.BonusBp = 0;
        if (s.Buffs.FirstOrDefault(b => b.Guid == guid) is { } buff) await _buffs.RemoveAsync(s, buff);
    }

    /// <summary>只在實際命中與 HP／死亡發布後觸發；當次暴擊不會先吃到自己剛建立的 +5%。
    /// 20507 的原表 [20911..14,2000] 決定加成狀態與兩秒期限；換場、死亡、拔卡不延長期限。</summary>
    private Task OffenceHitPublishedAsync(GameSession s, MonsterUnit target, JsonElement row, long effective, DamageHitResult result) =>
        OffencePublishedAsync(s, target.Uid, target.Pos.Clone(), row, effective, result);

    private Task OffencePlayerHitPublishedAsync(GameSession s, GameSession target, JsonElement row, long effective, DamageHitResult result) =>
        target.Role == null ? Task.CompletedTask : OffencePublishedAsync(s, target.Role.Id, target.Position.Clone(), row, effective, result);

    private async Task OffencePublishedAsync(GameSession s, long targetId, Vec3 center, JsonElement row, long effective, DamageHitResult result)
    {
        if (s.Role == null || s.Dead || effective <= 0 || result == DamageHitResult.Miss || IsOffenceReaction(s, row)) return;
        var role = s.Role; var attachment = s.Attachment;
        long scene = s.WorldScene, generation = s.SceneGeneration;
        bool Current() => ReferenceEquals(s.Role, role) && !s.Dead && !s.SceneLoading && s.WorldScene == scene
            && s.SceneGeneration == generation && ReferenceEquals(s.Attachment, attachment) && attachment?.Cancelled != true;
        var state = _offence.GetOrCreateValue(s);
        if (state.ResolvingSplash) return;
        if (state.BuffGuid != 0 && state.ExpiresMs <= MonsterService.NowMs) await ClearOffenceAsync(s);
        if (!Current()) return;
        var passives = Passives(s).ToList();
        if (result == DamageHitResult.Critical)
        {
            var source = passives.Where(p => GameData.Int(p.Row, "_iBuffID") == 20507)
                .OrderByDescending(p => p.Id).FirstOrDefault();
            if (source != null && Numbers(source.Row, "_kLogicData") is [var buffId, var duration]
                && _data.Row("BuffConfig", buffId) is { } bonusRow && Numbers(bonusRow, "_kLogicData") is [var bonus])
            {
                await ClearOffenceAsync(s);
                if (!Current()) return;
                await _buffs.AddAsync(s, (int)buffId, (int)duration, role.Id);
                if (!Current()) return;
                if (s.Buffs.FirstOrDefault(b => b.ConfigId == buffId && b.SourceId == role.Id) is { } added)
                {
                    state.CardGuid = source.Card.Guid;
                    state.BuffGuid = added.Guid;
                    state.ExpiresMs = added.ExpireMs;
                    state.BonusBp = bonus;
                }
            }
        }
        if (Current()) await SplashAsync(s, targetId, center, row, effective, passives, state);
    }

    /// <summary>巴風特：只接受近戰普通攻擊。半徑、額外五人上限、比例、機率及冷卻均來自當前階級。
    /// （inferred）以原命中的實扣傷害為基準；額外命中不再次扣防，也不當成普攻或遞迴觸發卡片。</summary>
    private async Task SplashAsync(GameSession s, long primaryId, Vec3 center, JsonElement source, long effective,
        IReadOnlyList<Passive> passives, OffenceState state)
    {
        var skill = new SkillConfig(source);
        if (!skill.IsNormalAttack(_data) || skill.DmgType != 1 || skill.DistanceType != 1) return;
        var role = s.Role; var attachment = s.Attachment;
        long scene = s.WorldScene, generation = s.SceneGeneration;
        bool Current() => ReferenceEquals(s.Role, role) && !s.Dead && !s.SceneLoading && s.WorldScene == scene
            && s.SceneGeneration == generation && ReferenceEquals(s.Attachment, attachment) && attachment?.Cancelled != true;
        foreach (var passive in passives.DistinctBy(p => p.Id))
        {
            if (!Current()) return;
            long group = GameData.Int(passive.Row, "_iBuffID");
            if (group is not (20508 or 20509)) continue;
            var logic = Numbers(passive.Row, "_kLogicData");
            var desc = Description(passive.Row);
            int shift = group == 20508 ? 1 : 0;
            if (desc.Length != 4 + shift || logic.Length != 1 + shift) continue;
            long chance = shift == 1 ? logic[0] : 10000;
            if (SkillConfig.Get(_data, logic[shift]) is not { } splash) continue;
            float radius = (float)desc[shift];
            int limit = (int)desc[shift + 1];
            var targets = _combat.CardSplashTargets(s, center, primaryId, radius, limit);
            if (targets.Monsters.Length + targets.Players.Length == 0 || Roll(10000) >= chance || !Ready(s, group, (long)(desc[shift + 3] * 1000))) continue;
            long amount = (long)decimal.Floor((decimal)effective * (decimal)desc[shift + 2] / 100);
            if (amount <= 0) continue;
            var node = JsonNode.Parse(splash.AsDescribedHit(0, 1, splash.EleType, limit).GetRawText())!.AsObject();
            node[SplashDamageField] = amount;
            // 目標已依上方共用上限選完。CastExtraAsync 的 Area 會另選玩家，重複打中原目標；
            // 單體選取只阻止第二次自動選人，chosenTargets 仍保留完整已選魔物清單。
            node["_iRangeType"] = SkillRelease.SingleTarget;
            state.ResolvingSplash = true;
            var proc = RuntimeFor(s);
            bool wasResolving = proc.ResolvingAutocast;
            proc.ResolvingAutocast = true;
            try
            {
                var hit = JsonSerializer.SerializeToElement(node);
                await _combat.CastExtraAsync(s, hit, targets.Monsters);
                if (Current()) await _combat.CastCardSplashPlayersAsync(s, hit, targets.Players, amount, Current);
            }
            finally { proc.ResolvingAutocast = wasResolving; state.ResolvingSplash = false; }
        }
    }

}
