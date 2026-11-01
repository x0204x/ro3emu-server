using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    private sealed record OffertoriumOwner(ActiveSkillCast Cast, long BuffGuid);
    private readonly Dictionary<GameSession, OffertoriumOwner> _offertoriums = new();

    // 14315 四級 → 14325 四級。描述指定「自身魔攻」與「小隊」，不是每個承受者的魔攻。
    // (inferred) 來源持有唯一光環，首次在落地後一秒；每跳重取同五人小隊與30公尺範圍。
    private async Task<bool> StartOffertoriumAsync(GameSession source, ActiveSkillCast cast)
    {
        if (Script(cast.Row) != "Offertorium") return false;
        foreach (var tuple in Tuples(cast.Row, "_kBuff1"))
        {
            if (!RollBuff(tuple, out int id, out int duration) || id is < 1432501 or > 1432504) continue;
            await _buffs.AddSupportAsync(source, id, duration, cast.OwnerId);
            if (!CurrentCastMap(source, cast)) return true;
            var buff = source.Buffs.FirstOrDefault(b => b.ConfigId == id && b.SourceId == cast.OwnerId);
            if (buff == null) continue;
            var owner = new OffertoriumOwner(cast, buff.Guid);
            _offertoriums[source] = owner; // 重施只更新同一來源，不能多開一組週期。
            ScheduleOffertoriumCleanup(source, owner, buff.ExpireMs);
        }
        return true;
    }

    private void ScheduleOffertoriumCleanup(GameSession source, OffertoriumOwner owner, long expiry) =>
        ScheduleRuntime(owner.Cast.WorldScene, TimeSpan.FromMilliseconds(Math.Max(0, expiry - Now) + 1000), () =>
        {
            if (_offertoriums.GetValueOrDefault(source) != owner) return Task.CompletedTask;
            var live = source.Buffs.FirstOrDefault(b => b.Guid == owner.BuffGuid && b.ExpireMs > Now);
            if (live != null && CurrentCastMap(source, owner.Cast)) ScheduleOffertoriumCleanup(source, owner, live.ExpireMs);
            else _offertoriums.Remove(source);
            return Task.CompletedTask;
        });

    private async Task OffertoriumTickAsync(GameSession source, ActiveBuff buff, Func<bool> periodicCurrent)
    {
        if (!_offertoriums.TryGetValue(source, out var owner) || owner.BuffGuid != buff.Guid) return;
        if (source.Role?.Id != buff.SourceId || !CurrentCastMap(source, owner.Cast) || !_sessions.All().Contains(source))
        {
            _offertoriums.Remove(source);
            return;
        }
        var cast = owner.Cast;
        bool Current() => periodicCurrent() && _offertoriums.GetValueOrDefault(source) == owner && CurrentCastMap(source, cast)
            && _sessions.All().Contains(source) && source.Buffs.Any(b => b.Guid == owner.BuffGuid);
        bool Eligible(GameSession recipient) => Current() && recipient.Role != null && !recipient.Dead
            && !recipient.SceneLoading && recipient.WorldScene == cast.WorldScene
            && (recipient == source || SameHealingParty?.Invoke(source, recipient) == true)
            && Distance(source.Position, recipient.Position) <= Numbers(cast.Row, "_kRange").FirstOrDefault() / 100;
        if (BuffConfig.Get(_data, buff.ConfigId) is not { } rule) return;
        var targets = _world.PlayersIn(cast.WorldScene).Where(Eligible).OrderByDescending(p => p == source)
            .ThenBy(p => Distance(source.Position, p.Position)).Take((int)Math.Max(1, GameData.Int(cast.Row, "_iTargetMax"))).ToArray();
        foreach (var recipient in targets)
        {
            if (!Eligible(recipient)) continue;
            var role = recipient.Role;
            var attachment = recipient.Attachment;
            long generation = recipient.SceneGeneration;
            bool SameRecipient() => Eligible(recipient) && ReferenceEquals(role, recipient.Role)
                && ReferenceEquals(attachment, recipient.Attachment) && attachment?.Cancelled != true
                && generation == recipient.SceneGeneration;
            long actual;
            using (await CharacterService.Lock(recipient))
            {
                if (!SameRecipient()) continue;
                var sourceStats = _character.HealingStats(source);
                var stats = _character.HealingStats(recipient);
                long amount = (long)Math.Max(0, Math.Round(sourceStats[104] * rule.Data1.FirstOrDefault() / 10000d
                    + Numbers(rule.Row, "_kData2").FirstOrDefault()));
                foreach (var modifier in ExtraHeal) amount = modifier(source, amount);
                amount = ModifiedHealing(sourceStats, stats, amount);
                actual = Math.Min(amount, Math.Max(0, stats[101] - stats[2000]));
                if (actual <= 0) continue;
                await _character.SetHpSpAsync(recipient, stats[2000] + actual, null);
            }
            if (!SameRecipient()) continue;
            await ToPlayerAndViewersAsync(recipient, SkillHit, new SkillHitInfo
            {
                SrcID = cast.OwnerId, SkillId = cast.Request.SkillId, SessionId = cast.Id, RootCtxGuid = cast.Id,
                Dsts = { new DamageInfo { DstID = role!.Id, Damage = actual, DamageType = DamageType.Heal, DstElement = 1 } },
            });
            foreach (var handler in Healed) handler(source, actual);
            foreach (var handler in HealedAsync) { if (!SameRecipient()) break; await handler(source, actual); }
            foreach (var handler in HealingApplied)
            {
                if (!SameRecipient()) break;
                await handler(source, recipient, cast.Row, actual);
            }
        }
    }
}
