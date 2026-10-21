using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Valhalla Trials (HeroicSpirit, function 96). 42 one-time boss
/// layers (HeroicSpiritLayerConfig: 6 difficulties over the same 7 dungeons) and the Odin statue "Blessing", levelled
/// with Einherjar Coins. 83490 enters a layer's dungeon (DungeonService runs it; the clear is recorded with its
/// _iPassReward, see DungeonService.Obligations), 83491 prays, 5046 DBHeroicSpiritInfo shows both
/// (lua-src Logic/Asset/LC_DAL_Valhalla.lua). The statue is role blob "heroic_spirit" (DBOdinStatue).
/// Not done: the statue's attribute 4008 "Field Auto-Battle EXP Bonus" has no consumer in this server.
/// </summary>
public sealed class ValhallaService
{
    public const uint Sync = 5046, Challenge = 83490, Pray = 83491;
    public const int Function = 96;
    // ErrorCodeConfig 7101-7108 (texts proven; which request answers which is inferred). 7107 is ErrorCodes.DungeonBusy.
    public const int ErrDone = 7101, ErrCoins = 7102, ErrNoLayer = 7103, ErrPrevious = 7104, ErrLevel = 7105, ErrConfig = 7108;
    private const int GamePlayType = 23;

    private readonly GameData _data;
    private readonly DungeonCatalog _catalog;
    private readonly DungeonService _dungeons;
    private readonly CharacterService _character;
    private readonly FunctionService _functions;
    private readonly TaskService _tasks;
    private readonly RoleStore _roles;

    public ValhallaService(GameData data, DungeonCatalog catalog, DungeonService dungeons, CharacterService character,
        FunctionService functions, TaskService tasks, RoleStore roles)
    {
        this._data = data; this._catalog = catalog; this._dungeons = dungeons; this._character = character;
        this._functions = functions; this._tasks = tasks; this._roles = roles;
        dungeons.Completed.Add(ClearedAsync);
    }

    /// <summary>PublicParamConfig HeroSpiritItemId (Einherjar Coin 2000001) and HeroSpiritItemExp (statue EXP per coin, 2).</summary>
    private int CoinItem => Param("HeroSpiritItemId", 2000001);
    private int ExpPerCoin => Math.Max(1, Param("HeroSpiritItemExp", 2));

    private int Param(string key, int fallback) => _data.Table("PublicParamConfig").TryGetValue(key, out var row)
        ? (int)(GameData.Number(row) ?? fallback) : fallback;

    // ------------------------------------------------------------------ state / 5046

    public void Load(GameSession s)
    {
        var bytes = _roles.LoadHeroicSpirit(s.Role!.Id);
        // A role that never prayed: statue level 0 (the level-0 row exists, attribute 0; inferred start).
        s.OdinStatue = bytes is { Length: > 0 } ? DBOdinStatue.Parser.ParseFrom(bytes) : new DBOdinStatue { Inited = 1 };
    }

    /// <summary>
    /// 5046, after 5062 (the cleared layers are dungeon records). The quest targets 334001 were evaluated at the 5027
    /// login sync before those records loaded, so a role with a clear refreshes them once.
    /// </summary>
    public async Task LoginAsync(GameSession s)
    {
        await SyncAsync(s);
        if (DungeonService.ValhallaCleared(s.Dungeons).Any()) await _tasks.RefreshAsync(s);
    }

    private Task SyncAsync(GameSession s) => CharacterService.SyncAsync(s, Sync, Snapshot(s));

    /// <summary>finishedChallenge: the client keeps only the key set (LC_DAL_Valhalla.lua:104-120); 1 per cleared layer.</summary>
    private static DBHeroicSpiritInfo Snapshot(GameSession s)
    {
        var info = new DBHeroicSpiritInfo { OdinStatue = s.OdinStatue?.Clone() ?? new DBOdinStatue(), LatestChallengeId = s.ValhallaLatest };
        foreach (long layer in DungeonService.ValhallaCleared(s.Dungeons)) info.FinishedChallenge[layer] = 1;
        return info;
    }

    private Task ClearedAsync(GameSession s, int scene, bool paid) =>
        _data.Row("DungeonConfig", scene) is { } row && GameData.Int(row, "_iGamePlayType") == GamePlayType
            ? RefreshAsync(s) : Task.CompletedTask;

    private async Task RefreshAsync(GameSession s)
    {
        await SyncAsync(s);
        await _tasks.RefreshAsync(s); // 334001 "Clear Valhalla ${1}"
    }

    // ------------------------------------------------------------------ 83490 challenge

    /// <summary>The client's own gates (LC_ValhallaHelper.lua:104-165, the Tips widget), checked again here.</summary>
    private int ChallengeError(GameSession s, long layerId, out JsonElement layer)
    {
        layer = default;
        if (s.Role == null || s.Progress == null || s.Dungeons == null) return ErrorCodes.FeatureUnavailable;
        if (!_functions.IsOpen(s, Function)) return ErrorCodes.FunctionLocked;
        if (_data.Row("HeroicSpiritLayerConfig", layerId) is not { } row) return ErrNoLayer;
        layer = row;
        var cleared = DungeonService.ValhallaCleared(s.Dungeons).ToHashSet();
        if (cleared.Contains(layerId)) return ErrDone;
        long previous = GameData.Int(row, "_iPreLayer");
        if (previous != 0 && !cleared.Contains(previous)) return ErrPrevious;
        if (s.Progress.Level < GameData.Int(row, "_iLevelLimit")) return ErrLevel;
        return 0;
    }

    public async Task ChallengeAsync(GameSession s, HeroicSpiritChallengeReq req)
    {
        int error = ChallengeError(s, req.ChallengeId, out var layer);
        var plan = error == 0 ? _catalog.Valhalla(layer) : null;
        if (error == 0 && plan == null) error = ErrConfig;
        if (error == 0) error = await _dungeons.AdmitValhallaAsync(s, plan!);
        await s.SendGameAsync(Challenge, new HeroicSpiritChallengeRes { Err = error });
        if (error != 0)
        {
            // The DAL's errorHandler answers the framework envelope with its own popup (LC_DAL_Valhalla.lua:252-274).
            await WorldCoreService.ErrorAsync(s, Challenge, error);
            return;
        }
        s.ValhallaLatest = req.ChallengeId;
        await _dungeons.BeginValhallaAsync(s, plan!);
    }

    // ------------------------------------------------------------------ 83491 pray

    private JsonElement? LevelRow(long level) => _data.Table("HeroicSpiritLevelConfig").Values
        .Where(r => GameData.Int(r, "_iLevel") == level).Select(r => (JsonElement?)r).FirstOrDefault();

    /// <summary>
    /// One Blessing: as many coins as the current level still needs (the client's own check, UI_Sys_ValhallaPanelCtrl
    /// OnClickPray: ceil((_iExp - curExp) / HeroSpiritItemExp)); the statue gains coins x HeroSpiritItemExp and levels up
    /// while that covers a level (inferred server arithmetic: an odd _iExp leaves 1 EXP over). The request carries no body.
    /// </summary>
    public async Task PrayAsync(GameSession s)
    {
        using var gate = await CharacterService.Lock(s);
        int error = s.Role == null || s.OdinStatue == null ? ErrorCodes.FeatureUnavailable
            : !_functions.IsOpen(s, Function) ? ErrorCodes.FunctionLocked : 0;
        DBOdinStatue? next = null;
        (int Entry, long Count)[] cost = [];
        if (error == 0)
        {
            var statue = s.OdinStatue!;
            long need = LevelRow(statue.CurLv) is { } row ? GameData.Int(row, "_iExp") : 0;
            if (need <= 0) error = ErrorCodes.Parameter; // the last level (_iExp 0): the client says "Maximum value reached!"
            else
            {
                long coins = (need - statue.CurExp + ExpPerCoin - 1) / ExpPerCoin;
                cost = [(CoinItem, Math.Max(1, coins))];
                next = statue.Clone();
                next.Inited = 1;
                next.CurExp += cost[0].Count * ExpPerCoin;
                while (LevelRow(next.CurLv) is { } level && GameData.Int(level, "_iExp") is var exp and > 0 && next.CurExp >= exp)
                {
                    next.CurExp -= exp;
                    next.CurLv++;
                }
                if (_character.CountOf(s, CoinItem) < cost[0].Count) error = ErrCoins;
            }
        }
        if (error == 0)
        {
            var saved = next!;
            if (await _character.ExchangeLockedAsync(s, cost, [], popup: false, persist: tx =>
                {
                    tx.RoleBlob(s.Role!.Id, "heroic_spirit", saved.ToByteArray());
                    tx.WithEconomy(_character.FamilyEconomy(s, "cd13b/valhalla-pray", cost, [],
                        new { saved.CurLv, saved.CurExp }));
                }, committed: () => s.OdinStatue = saved) == null)
                error = ErrCoins;
        }
        await s.SendGameAsync(Pray, new HeroicSpiritPrayRes { Err = error });
        if (error == 0) await SyncAsync(s);
    }
}
