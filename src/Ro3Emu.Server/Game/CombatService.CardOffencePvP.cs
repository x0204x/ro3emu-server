using System.Text.Json;
using System.Text.Json.Nodes;
using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    internal List<Func<GameSession, GameSession, JsonElement, DamageHitResult, long, long>> PvpOutgoingDamageAdjusters { get; } = [];
    internal sealed record CardSplashPlayer(GameSession Player, Func<bool> Current);

    /// <summary>20508/20509 的說明是「額外目標」，不是只限魔物。沿用既有敵對／可見判定，
    /// 魔物與合法 PvP 玩家共用五人上限；距離相同以 ID 排序（inferred）。</summary>
    internal (MonsterUnit[] Monsters, CardSplashPlayer[] Players) CardSplashTargets(GameSession source,
        Vec3 center, long primaryId, float radius, int limit)
    {
        var candidates = new List<(long Id, float Distance, MonsterUnit? Monster, CardSplashPlayer? Player)>();
        foreach (var monster in MonstersNear(source, center, radius))
        {
            float distance = SplashDistanceSquared(center, monster.Pos);
            if (monster.Uid != primaryId && distance <= radius * radius)
                candidates.Add((monster.Uid, distance, monster, null));
        }
        foreach (var player in _sessions.All().Where(p => p.Role?.Id != primaryId && CanPlayerHit(source, p) && CanSee(source, p)))
        {
            float distance = SplashDistanceSquared(center, player.Position);
            if (distance > radius * radius) continue;
            var role = player.Role; var attachment = player.Attachment;
            long scene = player.WorldScene, generation = player.SceneGeneration;
            bool Current() => ReferenceEquals(player.Role, role) && !player.SceneLoading && player.WorldScene == scene
                && player.SceneGeneration == generation && ReferenceEquals(player.Attachment, attachment) && attachment?.Cancelled != true;
            candidates.Add((role!.Id, distance, null, new CardSplashPlayer(player, Current)));
        }
        var chosen = candidates.OrderBy(t => t.Distance).ThenBy(t => t.Id).Take(Math.Max(0, limit)).ToArray();
        return (chosen.Select(t => t.Monster).OfType<MonsterUnit>().ToArray(),
            chosen.Select(t => t.Player).OfType<CardSplashPlayer>().ToArray());
    }

    internal async Task CastCardSplashPlayersAsync(GameSession source, JsonElement row, CardSplashPlayer[] targets,
        long amount, Func<bool> currentSource)
    {
        if (amount <= 0 || !currentSource()) return;
        var node = JsonNode.Parse(row.GetRawText())!.AsObject();
        node["_ro3GuardianFixedHit"] = amount; // 已由原命中換算，不再套一次攻防；仍進護盾／保命／持久化入口。
        var hit = JsonSerializer.SerializeToElement(node);
        var skill = new SkillConfig(hit);
        foreach (var target in targets)
        {
            if (!currentSource()) return;
            if (!target.Current()) continue;
            await HitEventPlayersAsync(source, [target.Player], hit, (int)skill.Id, HitEffect(skill.ScriptShow),
                ++_sessionIds, () => currentSource() && target.Current());
        }
    }

    private static float SplashDistanceSquared(Vec3 a, Vec3 b) =>
        (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z);
}
