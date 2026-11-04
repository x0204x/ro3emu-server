using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

/// <summary>
/// First-clear rewards (TenGameplayBossConfig._iPartAward, MultiverseGroupConfig -> AwardConfig): a cleared boss offers
/// its reward on 5062 and 82587 pays it once. UI_Sys_Dungeon_RightInfoWidgetCtrl lists _iPartAward as the first-pass
/// reward and sends 82587 only while isFirst is Complete. Paying by claim rather than at settlement (82584
/// firstPassAward), and offering it to roles cleared before it was paid, are the maintainer's choice (inferred: the
/// client supports both).
/// </summary>
public sealed partial class DungeonService
{
    /// <summary>5062 isFirst (UI_Sys_Dungeon_RightInfoWidgetCtrl iFirstAwardType): not cleared, cleared and unpaid, paid.</summary>
    internal const int FirstAwardNone = 0, FirstAwardComplete = 1, FirstAwardFinish = 2;

    /// <summary>A boss without a first-clear reward (the 100103 rows) shows Finish once cleared, never a claimable empty one.</summary>
    private static int FirstPassAwardState(State state, string key, JsonElement boss, Record? record)
        => state.FirstPassPaid.Contains(key) ? FirstAwardFinish
            : record?.Passed != true ? FirstAwardNone
            : new TenGameplayBossConfig(boss).PartAward != 0 ? FirstAwardComplete : FirstAwardFinish;

    /// <summary>
    /// 82587: pays the cleared boss's first-clear reward, rolled once, in the commit that marks it paid. A Hard boss also
    /// pays its Normal counterpart's reward when that is still unpaid: "After successfully Challenging a Hard Boss, if its
    /// Normal Difficulty has not yet been Cleared for the first time, you will directly Claim that Boss's Normal First Clear
    /// Rewards" (Phantom Realm rules, Localization_en).
    /// </summary>
    public async Task ClaimFirstPassAsync(GameSession s, ClaimFirstPassAwardReq req)
    {
        string key = $"{req.MultiDungeonId}:{req.MarkId}";
        long roleId = s.Role?.Id ?? 0;
        using var scope = roleId > 0 && _loop.Owners is { } owners
            ? await owners.EnterAsync("dungeon first pass claim", [Ownership.ReservationKey.Role(roleId),
                Ownership.ReservationKey.Lifecycle(roleId), Ownership.ReservationKey.Entity("dungeon_first_pass_claim", $"{roleId}/{key}")]) : null;
        if (s.Role?.Id != roleId) throw new OperationCanceledException("Role changed before first pass claim admission.");
        if (s.Owner != null) scope?.Context.Pin(s.Owner);
        int error = FirstPassRefusal(s, req.MultiDungeonId, req.MarkId);
        if (error == 0)
        {
            var targets = FirstPassTargets(s, req.MultiDungeonId, req.MarkId);
            error = await PayFirstPassAsync(s, req.MultiDungeonId, req.MarkId);
            // The answer is about the claimed boss; a Normal counterpart that fails to pay stays claimable on its own.
            if (error == 0)
                foreach (var (multi, mark) in targets)
                    await PayFirstPassAsync(s, multi, mark);
        }
        if (s.Dungeons != null) await SyncAsync(s);
        await s.SendGameAsync(ClaimFirstPass, new ClaimFirstPassAwardResp
        { Result = error, MultiDungeonId = req.MultiDungeonId, MarkId = req.MarkId });
    }

    private int FirstPassRefusal(GameSession s, int multiId, int mark)
    {
        if (s.Role == null || s.Dungeons == null || MultiRow(multiId) is not { } multi || !Meets(s, multi)
            || _catalog.Boss(multi, mark) is not { } boss) return ErrorCodes.Parameter;
        var state = CurrentState(s.Dungeons);
        int award = FirstPassAwardState(state, $"{multiId}:{mark}", boss, state.Records.GetValueOrDefault($"{multiId}:{mark}"));
        return award == FirstAwardComplete ? 0 : award == FirstAwardFinish ? ErrorCodes.RewardClaimed : ErrorCodes.Parameter;
    }

    /// <summary>For a Hard boss (_iDifficulty 2), the unpaid Normal boss in the same slot (_kBoss [mark, boss, slot]) the role may enter.</summary>
    private List<(int Multi, int Mark)> FirstPassTargets(GameSession s, int multiId, int mark)
    {
        var targets = new List<(int, int)>();
        var multi = MultiRow(multiId)!.Value;
        if (new TenGameplayConfig(multi).Difficulty != 2 || BossSlot(multi, mark) is not { } slot) return targets;
        var state = CurrentState(s.Dungeons!);
        foreach (var normal in _catalog.MultiRows.Where(r => new TenGameplayConfig(r).Type == new TenGameplayConfig(multi).Type
            && new TenGameplayConfig(r).Difficulty == 1 && Meets(s, r)))
            for (int m = 1; m <= DungeonCatalog.Marks(normal); m++)
                if (BossSlot(normal, m) == slot && _catalog.Boss(normal, m) is { } boss && new TenGameplayBossConfig(boss).PartAward != 0
                    && !state.FirstPassPaid.Contains($"{new TenGameplayConfig(normal).Id}:{m}"))
                    targets.Add(((int)new TenGameplayConfig(normal).Id, m));
        return targets;
    }

    private JsonElement? MultiRow(int multiId) => _catalog.MultiRows
        .Where(r => new TenGameplayConfig(r).Id == multiId).Select(r => (JsonElement?)r).FirstOrDefault();

    /// <summary>The third value of a _kBoss tuple: the boss slot a Normal and a Hard row share (101 in 50101 and 50104).</summary>
    private static long? BossSlot(JsonElement multi, int mark)
    {
        var bosses = new TenGameplayConfig(multi).Boss;
        return mark >= 1 && mark <= bosses.Length && bosses[mark - 1] is { Length: >= 3 } tuple ? tuple[2] : null;
    }

    private async Task<int> PayFirstPassAsync(GameSession s, int multiId, int mark)
    {
        string key = $"{multiId}:{mark}";
        var boss = _catalog.Boss(MultiRow(multiId)!.Value, mark)!.Value;
        long awardId = MultiverseAward(new TenGameplayBossConfig(boss).PartAward);
        AwardPlan? plan = null;
        try { if (awardId > 0) plan = _awards.ResolveDirect(s, awardId); }
        catch (AwardResolutionException e) { LogFirstPassAwardRejected(_log, e, awardId); }
        if (plan == null || plan.Grants.Count == 0) return ErrorCodes.RewardMissing;
        var next = CurrentState(s.Dungeons!);
        next.FirstPassPaid.Add(key);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(next);
        bool paid = await _character.ApplyAwardAsync(s, plan, persist: tx =>
        {
            tx.RoleBlob(s.Role!.Id, "dungeons", bytes);
            if (plan.Grants.Any(grant => grant.Entry is not (100 or 101)))
                tx.WithEventEconomy(EventEconomy.ForRole(_data, s, "dungeon/first-pass-award",
                    $"dungeon/first-pass-award/{s.Role.Id}/{key}/{next.SeasonStart}", [], plan.Grants,
                    new { Multi = multiId, Mark = mark, Award = awardId }));
        }, committed: () => s.Dungeons = next);
        return paid ? 0 : ErrorCodes.Unknown;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "First pass award {Award} rejected")]
    private static partial void LogFirstPassAwardRejected(ILogger logger, Exception exception, long award);

    /// <summary>MultiverseGroupConfig: the AwardConfig id of the current play book, else the default one; 0 when absent.</summary>
    private long MultiverseAward(long group)
    {
        if (_data.Row("MultiverseGroupConfig", group) is not { } mg) return 0;
        long id = GameData.Int(mg, $"_iValue_{_seasons.PlayBook}");
        return id > 0 ? id : GameData.Int(mg, "_iValue_default");
    }
}
