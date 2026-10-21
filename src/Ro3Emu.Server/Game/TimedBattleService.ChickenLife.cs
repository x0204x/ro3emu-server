using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;
using static Ro3Emu.Server.Game.ChickenRules;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Survivor Battle death and revival, with the DungeonLua_2240101 rules (<see cref="ChickenRules"/>): a death inside the
/// first 3 minutes of the battle spends the free revive and opens the 82663 revive map (82661 picks the point); a later
/// death drops a Death crate with the pack and an ID tag 15000056 holding the card 50001; a teammate carrying the card
/// revives with the 20 s channel (82650 or using the card, 82680 to the target). A team with nobody left standing is
/// out; the last team (or the best survivor at the 20 min end) wins. 82641 gives up. Flow (inferred) from the brief §6.
/// </summary>
public sealed partial class TimedBattleService
{
    public const uint ForceDead = 82641, StartRevive = 82650, Relife = 82661, RelifeOther = 82662, RelifeNotify = 82663,
        RevivingNotify = 82680;

    private sealed class ReviveChannel
    {
        public required GameSession Reviver { get; init; }
        public required long StartMs { get; init; }
        public required float X { get; init; }
        public required float Z { get; init; }
        public required GameSession[] Targets { get; init; }
    }

    // ---- statistics hooks (CombatService) ----

    private void ChickenDealt(GameSession source, GameSession target, long damage, bool lethal)
    {
        if (Chicken(source) is not (var match, var run, var me) || !run.Players.TryGetValue(target, out var victim)) return;
        me.Damage += damage;
        victim.Injury += damage;
        victim.LastHitBy[source] = NowMs();
    }

    private void ChickenHealed(GameSession player, long amount)
    {
        if (Chicken(player) is (_, _, var me)) me.Cure += amount;
    }

    /// <summary>The kill credit after CombatService published the death (PlayerDied already ran). Assists: the victim's
    /// other attackers of the last 10 s (inferred).</summary>
    private Task ChickenKillAsync(Match match, GameSession killer, GameSession victim)
    {
        if (match.Chicken is not { } run || run.Stage != StageStartGame || !run.Players.TryGetValue(victim, out var dead)
            || !run.Players.ContainsKey(killer) || match.Faction[killer] == match.Faction[victim]) return Task.CompletedTask;
        match.Kills[killer] = match.Kills.GetValueOrDefault(killer) + 1;
        long now = NowMs();
        foreach (var (helper, at) in dead.LastHitBy)
            if (helper != killer && now - at <= 10_000 && run.Players.TryGetValue(helper, out var assist)
                && match.Faction[helper] != match.Faction[victim]) assist.Assists++;
        dead.LastHitBy.Clear();
        return Task.CompletedTask;
    }

    // ---- death ----

    /// <summary>CombatService.PlayerDied for a participant of a running battle (PvP or poison).</summary>
    private async Task ChickenDiedAsync(GameSession player)
    {
        if (Chicken(player) is not (var match, var run, var me) || run.Stage != StageStartGame) return;
        long now = NowMs();
        me.Deaths++;
        me.State = RoundRoleStatus.Death;
        me.Heals.Clear();
        await InterruptChannelsAsync(match, run, c => c.Reviver == player);
        if (!me.GaveUp && me.FreeRelifeLeft > 0 && now < run.BattleStartMs + FirstResurrectionMs)
        {
            me.FreeRelifeLeft--;
            await OpenRelifeAsync(player, me, player.Position.X, player.Position.Z, now);
        }
        else
        {
            Eliminate(match, run, player, now);
            await DropBelongingsAsync(match, run, player, me);
        }
        await SyncTeamAsync(match, match.Faction[player]);
        await BroadcastHudAsync(match);
        PostEndCheck(match);
    }

    /// <summary>Out until revived: the team stays in while anyone of it stands or waits on a revive map.</summary>
    private void Eliminate(Match match, ChickenRun run, GameSession player, long now)
    {
        var me = run.Players[player];
        me.RelifeUntilMs = 0;
        me.EliminatedMs = now;
        match.Fallen.Add(player);
        RecordEliminations(match, run);
    }

    private static void RecordEliminations(Match match, ChickenRun run)
    {
        var remaining = RemainingFactions(match);
        foreach (int faction in match.Players.Select(p => match.Faction[p]).Distinct().Order())
            if (!remaining.Contains(faction) && !run.EliminatedFactions.Contains(faction)) run.EliminatedFactions.Add(faction);
    }

    /// <summary>The pack into a Death crate 15000052, and (while ResurrectionLimit/IdentityDropLimit allow) an ID tag
    /// 15000056 with the card 50001 whose attributes 1200/1201 name the dead (LC_ItemProtoParse.lua:910-955).</summary>
    private async Task DropBelongingsAsync(Match match, ChickenRun run, GameSession player, ChickenPlayer me)
    {
        var at = new MapPoint(player.Position.X, player.Position.Y, player.Position.Z, player.Face);
        var items = me.Pack.Values.ToArray();
        me.Pack.Clear(); me.SkillBar.Clear(); me.ItemBar.Clear(); me.EquipBar.Clear(); me.IdentityCards.Clear();
        if (items.Length > 0)
            await SpawnChestAsync(match, run, DeathCrate, at, chest =>
            {
                foreach (var item in items) AddToChest(run, chest, item.Entry, item.Stack, item.OwnerId, item.OwnerName);
            });
        if (me.GaveUp || me.Resurrections >= ResurrectionLimit || me.TagDrops >= IdentityDropLimit) return;
        me.TagDrops++;
        long id = match.Participants[player].Id;
        string name = player.Role?.Name ?? match.Participants[player].Name;
        await SpawnChestAsync(match, run, IdentityTag, at with { X = at.X + 1 }, chest =>
            AddToChest(run, chest, IdentityCardItem, 1, id, name));
    }

    // ---- revive map (82663 / 82661) ----

    /// <summary>82663: LC_DAL_Chicken.lua:3252-3338 opens the Revive-type map panel with a countdown to relifeEndTime
    /// (ms) and the death marker; 0 closes it.</summary>
    private static async Task OpenRelifeAsync(GameSession player, ChickenPlayer me, float x, float z, long now)
    {
        me.RelifeUntilMs = now + RelifeChooseMs;
        me.RelifeX = x; me.RelifeZ = z;
        await player.SendGameAsync(RelifeNotify, new ChickenRelifeNotify
        { RelifeEndTime = me.RelifeUntilMs, PositionX = (int)x, PositionY = (int)z });
    }

    /// <summary>82661: the revive map's chosen point (UI_Sys_Chicken_MapPanelCtrl.lua:1997 -> LC_ChickenHelper.lua
    /// :2732-2765, world x/z like 82623).</summary>
    public async Task RelifeAsync(GameSession session, ChickenRelifeReq req)
    {
        int result = ErrorCodes.Parameter;
        if (Chicken(session) is (var match, var run, var me))
            await RunMatchTransitionAsync(match, "chicken relife", async () =>
            {
                if (run.Stage != StageStartGame || me.RelifeUntilMs == 0 || !session.Dead) { result = ErrorCodes.FeatureUnavailable; return; }
                if (req.PositionX is < MapMin or > MapMax || req.PositionY is < MapMin or > MapMax) return;
                result = 0;
                me.RelifeX = req.PositionX; me.RelifeZ = req.PositionY;
                await session.SendGameAsync(Relife, new ChickenRelifeResp { Result = 0 });
                await ReviveAtAsync(match, run, session, req.PositionX, req.PositionY);
            });
        if (result != 0) await session.SendGameAsync(Relife, new ChickenRelifeResp { Result = result });
    }

    private async Task ReviveAtAsync(Match match, ChickenRun run, GameSession player, float x, float z)
    {
        var me = run.Players[player];
        // A death still being published (or another revive) holds the claim: the tick retries a second later.
        if (!_combat.TryClaimRevive(player)) { me.RelifeUntilMs = NowMs() + 1000; return; }
        me.RelifeUntilMs = 0;
        try
        {
            if (!_combat.CommitReviveClaim(player)) return;
            me.State = RoundRoleStatus.InWar;
            me.Revived++;
            match.Fallen.Remove(player);
            await _combat.PublishReviveAsync(player, WalkablePoint(x, z));
        }
        finally { _combat.ReleaseReviveClaim(player); }
        await player.SendGameAsync(RelifeNotify, new ChickenRelifeNotify { RelifeEndTime = 0 });
        await SyncTeamAsync(match, match.Faction[player]);
        await BroadcastHudAsync(match);
    }

    // ---- teammate revive (82650 / 82680) ----

    /// <summary>82650: the ID-card button (UI_Sys_Chicken_TopIDWidgetCtrl.lua:144), the item tip or the bag
    /// (LC_ChickenHelper.lua:2793-2802).</summary>
    public async Task StartReviveAsync(GameSession session)
    {
        int result = await StartReviveCoreAsync(session);
        await session.SendGameAsync(StartRevive, new ChickenStartReviveResp { Result = result });
    }

    private async Task<int> StartReviveCoreAsync(GameSession session)
    {
        int result = ErrorCodes.Parameter;
        if (Chicken(session) is (var match, var run, var me))
            await RunMatchTransitionAsync(match, "chicken revive start", async () =>
            {
                if ((result = CanAct(run, me, session)) != 0) return;
                if (run.Channels.Any(c => c.Reviver == session)) { result = ErrorCodes.DungeonBusy; return; }
                var targets = Team(match, match.Faction[session]).Where(mate => mate != session && mate.Dead
                    && match.Fallen.Contains(mate) && !match.Absent.Contains(mate) && run.Players[mate] is { GaveUp: false } t
                    && t.Resurrections < ResurrectionLimit && me.IdentityCards.ContainsKey(match.Participants[mate].Id)
                    && run.Channels.All(c => !c.Targets.Contains(mate))).ToArray();
                if (targets.Length == 0) { result = ErrorCodes.ItemsShort; return; }
                long now = NowMs();
                var channel = new ReviveChannel { Reviver = session, StartMs = now, X = session.Position.X, Z = session.Position.Z, Targets = targets };
                run.Channels.Add(channel);
                result = 0;
                await NotifyChannelAsync(match, channel, interrupted: false);
            });
        return result;
    }

    /// <summary>82680 to the reviver's team: LC_DAL_Chicken.lua:811-858 acts only for an id in beReviveIds.</summary>
    private Task NotifyChannelAsync(Match match, ReviveChannel channel, bool interrupted)
    {
        var notify = new ChickenOtherRevivingNotify
        {
            Reviver = match.Participants[channel.Reviver].Id, ReviveStartTime = channel.StartMs, Interrupted = interrupted,
            ReviveDuration = ReliveChantMs, BeReviveIds = { channel.Targets.Select(t => match.Participants[t].Id) },
        };
        return RoleFanout.EachAsync(Team(match, match.Faction[channel.Reviver]).Where(p => InRoom(match).Contains(p)),
            p => p.SendGameAsync(RevivingNotify, notify));
    }

    private async Task InterruptChannelsAsync(Match match, ChickenRun run, Func<ReviveChannel, bool> which)
    {
        foreach (var channel in run.Channels.Where(which).ToArray())
        {
            run.Channels.Remove(channel);
            await NotifyChannelAsync(match, channel, interrupted: true);
        }
    }

    /// <summary>Per second: revive channels (break on death, departure or a walk away; complete after 20 s) and revive
    /// maps nobody answered (the server picks: the marked point, or inside the safe circle if it is poisoned).</summary>
    private async Task TickLifeAsync(Match match, ChickenRun run, long now)
    {
        await InterruptChannelsAsync(match, run, c => c.Reviver.Dead || Chicken(c.Reviver) == null
            || MonsterService.Distance(c.Reviver.Position.X, c.Reviver.Position.Z, c.X, c.Z) > ReliveChannelMoveLimit);
        foreach (var channel in run.Channels.Where(c => now >= c.StartMs + ReliveChantMs).ToArray())
        {
            run.Channels.Remove(channel);
            var reviver = run.Players[channel.Reviver];
            foreach (var target in channel.Targets)
            {
                long id = match.Participants[target].Id;
                if (!target.Dead || !match.Fallen.Contains(target) || match.Absent.Contains(target)
                    || !reviver.IdentityCards.TryGetValue(id, out long card)) continue;
                RemoveFromPack(reviver, card, 1);
                var revived = run.Players[target];
                revived.Resurrections++; revived.Rescued++; reviver.Help++;
                match.Fallen.Remove(target);
                run.EliminatedFactions.Remove(match.Faction[target]);
                await OpenRelifeAsync(target, revived, channel.Reviver.Position.X, channel.Reviver.Position.Z, now);
            }
            await SyncTeamAsync(match, match.Faction[channel.Reviver]);
        }
        foreach (var (player, me) in run.Players.Where(kv => kv.Value.RelifeUntilMs > 0 && now >= kv.Value.RelifeUntilMs).ToArray())
        {
            if (Chicken(player) == null) { me.RelifeUntilMs = 0; continue; }
            var (cx, cz, radius, _) = CurrentCircle(run, now);
            var (x, z) = MonsterService.Distance(me.RelifeX, me.RelifeZ, cx, cz) <= radius ? (me.RelifeX, me.RelifeZ)
                : RandomPoint(run, cx, cz, Math.Max(1, radius * 0.8f));
            await ReviveAtAsync(match, run, player, x, z);
        }
    }

    /// <summary>82662 at a Rescue Wagon (UI_Com_ChickenAmbulanceRevivalPanelCtrl.lua:435): no wagon NPC is placed on this
    /// server (inferred: their refresh region 877 has no schedule), so it is refused.</summary>
    public Task RelifeOtherAsync(GameSession session, ChickenRelifeOtherReq _) =>
        session.SendGameAsync(RelifeOther, new ChickenRelifeOtherResp { Result = ErrorCodes.FeatureUnavailable });

    /// <summary>31013 inside the match: the generic revive is not this mode's (revival is 82661 after 82663). A Rift
    /// fight is NoRevive (TimedBattleService.OnePiece): no self-revive there either.</summary>
    public async Task<bool> TryRefuseReliveAsync(GameSession session)
    {
        if (Chicken(session) == null && !InRiftFight(session)) return false;
        await session.SendGameAsync(CombatService.Relive, new EntityReliveAck
        { Result = ErrorCodes.SkillReviveNotAllowed, EntityID = (ulong)(session.Role?.Id ?? 0) });
        return true;
    }

    /// <summary>A death whose revive window this mode owns: no generic 30-second 30112 notice.</summary>
    private bool OwnsRevive(GameSession session) => Chicken(session) is (_, var run, _) && run.Stage == StageStartGame;

    // ---- 82641 give up ----

    /// <summary>
    /// 82641: the leave button (LC_DunGeon_Helper.lua:747-779 -> LC_DunGeon_Chicken.lua:1351-1390) waits 5 s for this
    /// player's 82633, then sends 30402 itself. The player is out at once (no revive, no ID tag) and gets a provisional
    /// 82633 (inferred). Its 30402 then leaves like any departure (no settlement, TimedBattleService.LeaveRoleAsync); a
    /// player who stays in the scene is settled with the others as a loss.
    /// </summary>
    public async Task ForceDeadAsync(GameSession session)
    {
        int result = ErrorCodes.FeatureUnavailable;
        ChickenResultNotify? snapshot = null;
        if (Chicken(session) is (var match, var run, var me))
            await RunMatchTransitionAsync(match, "chicken give up", async () =>
            {
                if (match.Settled) return;
                long now = NowMs();
                me.GaveUp = true;
                me.State = RoundRoleStatus.Leave;
                await InterruptChannelsAsync(match, run, c => c.Reviver == session || c.Targets.Contains(session));
                Eliminate(match, run, session, now);
                result = 0;
                snapshot = ChickenSnapshot(match, session, false);
                await SyncTeamAsync(match, match.Faction[session]);
                await BroadcastHudAsync(match);
                PostEndCheck(match);
            });
        await session.SendGameAsync(ForceDead, new ChickenPlayerForceDeadResp { Result = result });
        if (snapshot != null) await session.SendGameAsync(ChickenResult, snapshot);
    }

    // ---- end ----

    private void PostEndCheck(Match match) => _loop.PostRuntime(async () =>
    {
        if (!match.Settled && match.Chicken is { Stage: StageStartGame } && RemainingFactions(match).Count <= 1)
            await SettleMatchAsync(match);
    });

    /// <summary>
    /// Team ranks: standing teams first (more members standing, then kills, then damage; (inferred) tie-break), then the
    /// eliminated ones, the last eliminated first. Rank 1 is the winner.
    /// </summary>
    private Dictionary<int, int> ChickenRanks(Match match)
    {
        var run = match.Chicken;
        var factions = match.Players.Select(p => match.Faction[p]).Distinct().ToArray();
        var standing = RemainingFactions(match);
        var order = standing.OrderByDescending(f => match.Players.Count(p => match.Faction[p] == f && !match.Fallen.Contains(p) && !match.Absent.Contains(p)))
            .ThenByDescending(f => match.Players.Where(p => match.Faction[p] == f).Sum(p => match.Kills.GetValueOrDefault(p)))
            .ThenByDescending(f => run == null ? 0 : match.Players.Where(p => match.Faction[p] == f).Sum(p => run.Players[p].Damage))
            .ThenBy(f => f).ToList();
        if (run != null)
            foreach (int faction in Enumerable.Reverse(run.EliminatedFactions))
                if (!order.Contains(faction)) order.Add(faction);
        foreach (int faction in factions.Order()) if (!order.Contains(faction)) order.Add(faction);
        return order.Select((faction, index) => (faction, index + 1)).ToDictionary(x => x.faction, x => x.Item2);
    }

    /// <summary>Clears the match state off a participant when it leaves the room (settlement, abort, give up).</summary>
    private async Task ChickenDetachAsync(Match match, GameSession player)
    {
        player.Synced.Remove(RoundSync);
        if (match.Chicken is { } run && run.Channels.Count > 0) await InterruptChannelsAsync(match, run, c => c.Reviver == player);
        if (player.Role != null && player.Progress != null)
        {
            player.CurrentHp = null; player.CurrentSp = null;
            _character.Refresh(player);
        }
    }

    /// <summary>32700 on a chest: the pickup panel is client-hosted (ActionConfig OpenPickUpPanel), so only acknowledge.</summary>
    private async Task<bool> TryChestInteractAsync(GameSession session, NpcInteractionReq req)
    {
        if (Chicken(session) is not (_, var run, _) || run.Chests.All(c => c.Uid != req.NpcUid)) return false;
        await session.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionRsp { Result = 0, Req = req });
        return true;
    }
}
