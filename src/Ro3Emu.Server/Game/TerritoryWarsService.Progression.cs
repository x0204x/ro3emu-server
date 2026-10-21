using System.Text.Json;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class TerritoryWarsService
{
    private JsonElement? ResultRule(int id) => _data.Table("TerritoryWarResultRuleConfig").Values
        .Where(r => GameData.Int(r, "_iRuleID") == id).Select(r => (JsonElement?)r).FirstOrDefault();

    /// <summary>
    /// TerritoryWarStageConfig final-auction rule 1/2/3 limits the activity's qualified guild list to
    /// 27/9/3. (Inferred) Apply the ceiling after per-zone seats, retain incumbents first, then held bid,
    /// escort score, earliest bid, guild id. The client consumes the resulting battleZoneData list.
    /// </summary>
    private IReadOnlyCollection<GuildStore.State> ApplyAuctionResultRule(WorldState next, AuctionRound round)
    {
        int id = _clock.RoundStage(round.Round, 16, 17, 18)?.ResultRule ?? 0;
        var rule = ResultRule(id);
        if (rule == null || GameData.Int(rule.Value, "_iRuleType") != 1) return [];
        int limit = (int)Math.Max(0, GameData.Int(rule.Value, "_iLimitCount"));
        var selected = round.Zones.Values.SelectMany(z => z.Entries).OrderByDescending(e => e.Occupier)
            .ThenByDescending(BidValue).ThenByDescending(e => e.EscortScore).ThenBy(e => e.Time).ThenBy(e => e.GuildId)
            .Select(e => e.GuildId).Distinct().Take(limit).ToList();
        next.RuleResults[id] = selected;
        var refunds = new Dictionary<long, GuildStore.State>();
        foreach (var zone in round.Zones.Values)
        {
            foreach (var entry in zone.Entries.Where(e => !selected.Contains(e.GuildId) && !e.Occupier))
            {
                if (!refunds.TryGetValue(entry.GuildId, out var guild))
                { guild = _guildStore.Get(entry.GuildId); if (guild != null) refunds[guild.Id] = guild; }
                if (guild != null)
                { if (entry.Legacy) guild.Materials = checked(guild.Materials + entry.Amount); else guild.AuctionMaterials = checked(guild.AuctionMaterials + entry.Amount); }
            }
            zone.Entries.RemoveAll(e => !selected.Contains(e.GuildId));
        }
        return refunds.Values;
    }

    /// <summary>
    /// RuleType 2 advances battle winners, capped at 9/1/1. (Inferred) Rank winners across the round by
    /// actual capture/hold score then guild id; territorial ownership itself remains per-map. These
    /// finalists determine the next phase's reserved incumbent seats, not who may place a paid bid.
    /// </summary>
    private async Task ApplyBattleResultRulesAsync()
    {
        foreach (var stage in _clock.Stages.Where(s => s.Type is 6 or 10 or 14 && s.EndTime <= _clock.Now))
        {
            if (state.RuleResults.ContainsKey(stage.ResultRule) || ResultRule(stage.ResultRule) is not { } rule
                || GameData.Int(rule, "_iRuleType") != 2) continue;
            var records = state.Battles.Values.Where(b => b.Round == stage.Round).ToArray();
            if (records.Any(b => !b.Settled)) continue;
            state.RuleResults[stage.ResultRule] = records.Where(b => b.Winner > 0)
                .GroupBy(b => b.Winner).OrderByDescending(g => g.Sum(b => b.Scores.GetValueOrDefault(g.Key)))
                .ThenBy(g => g.Key).Take((int)Math.Max(0, GameData.Int(rule, "_iLimitCount"))).Select(g => g.Key).ToList();
            await SaveAsync(state);
        }
    }
}
