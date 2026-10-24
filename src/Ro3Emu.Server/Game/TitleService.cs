using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Title ranks (function 14, TitleConfig): 5031 AUTO_SYNC DBTitleInfo and 43102 TITLE_LEVEL. The original never opened
/// function 14 (base level 9999) and no client code hosts its widget; OpenContent opens it and
/// Ro3Emu.ClientHooks.TitleButton puts UI_Sys_Role_TitleWidget in the character window.
/// <list type="bullet">
/// <item>43102 (UI_Sys_Role_TitleWidgetCtrl.OnClickUpgrade, an empty request) claims the next rank: the row with the
/// smallest _kIndex above the current one (rank 1 "Beginner" from 0). The widget checks _iPower against
/// maxHistoryPower, _iLev against the base level and _kSpend against the bag before sending; the server checks the
/// same and takes _kSpend.</item>
/// <item>The new rank goes out as a 5031 increment: LC_DAL_Title.UpgradeUI plays the upgrade animation, whose end
/// event is what clears the widget's request lock (a full sync would leave it locked). 43102's reply is not read by
/// the client (its dataset TitleLevel has no handler) but is sent as the proto has it.</item>
/// <item>The rank adds _iPowerAdd to the titleScore power (PowerService.TitleId) and is the role's
/// RolePublicBaseInfo.title. _kAttr is [[101,0],[103,0],[104,0]] in every row, so no stat changes.</item>
/// </list>
/// Inferred: the Crests (_kSpend items 1250801-1250808) have no source in the client data; a boss kill drops one at
/// ServerOptions.TitleCrestPercent, of the tier its level reaches (see CrestDrops). The PvP damage bonus by title
/// (TitleBuffConfig, Formula_ATitleDamageAdd) is not applied.
/// </summary>
public sealed class TitleService
{
    public const uint Sync = 5031, Level = 43102;
    public const int Function = 14;

    private readonly GameData _data;
    private readonly CharacterService _character;
    private readonly FunctionService _functions;
    private readonly RoleStore _roles;
    private readonly ServerOptions _options;
    private List<TitleConfig>? _ranks;

    public TitleService(GameData data, CharacterService character, FunctionService functions, RoleStore roles,
        PowerService power, RewardService rewards, GmCommands gm, Microsoft.Extensions.Options.IOptions<ServerOptions> options)
    {
        this._data = data; this._character = character; this._functions = functions; this._roles = roles;
        this._options = options.Value;
        power.TitleId = s => s.TitleInfo?.TitleId ?? 0;
        character.TitleOf = s => s.TitleInfo?.TitleId ?? 0;
        rewards.ExtraKillDrops.Add(CrestDrops);
        gm.Register("title", "set <rank> | info (title rank test aid)", GmAsync);
    }

    /// <summary>TitleConfig rows in _kIndex order.</summary>
    private List<TitleConfig> Ranks() => _ranks ??=
        [.. _data.Table("TitleConfig").Values.Select(r => new TitleConfig(r)).Where(r => r.Id > 0).OrderBy(r => r.Index)];

    /// <summary>The rank after <paramref name="current"/> (the first one from 0).</summary>
    private TitleConfig? Next(long current)
    {
        var ranks = Ranks();
        int at = ranks.FindIndex(r => r.Id == current);
        int next = at < 0 ? 0 : at + 1; // in _kIndex order; an unknown or missing rank starts from the first
        return next < ranks.Count ? ranks[next] : null;
    }

    public void Load(GameSession s)
    {
        var bytes = _roles.LoadTitle(s.Role!.Id);
        s.TitleInfo = bytes is { Length: > 0 } ? DBTitleInfo.Parser.ParseFrom(bytes) : new DBTitleInfo();
    }

    public Task LoginAsync(GameSession s) => s.TitleInfo == null ? Task.CompletedTask
        : CharacterService.SyncAsync(s, Sync, s.TitleInfo.Clone());

    public async Task LevelAsync(GameSession s)
    {
        int result;
        long title;
        using (await CharacterService.Lock(s))
        {
            result = await LevelLockedAsync(s);
            title = s.TitleInfo?.TitleId ?? 0;
        }
        await s.SendGameAsync(Level, new TitleLevelRsp { Result = result, TitleId = title });
    }

    private async Task<int> LevelLockedAsync(GameSession s)
    {
        if (s.TitleInfo == null || s.Progress == null) return ErrorCodes.Unknown;
        if (!_functions.IsOpen(s, Function)) return ErrorCodes.FunctionLocked;
        if (Next(s.TitleInfo.TitleId) is not { } next) return ErrorCodes.Parameter;
        if (s.Progress.Level < next.Lev) return ErrorCodes.LevelTooLow;
        if (PowerService.Total(PowerService.History(s)) < next.Power) return ErrorCodes.Parameter;
        var cost = (next.Spend ?? []).Where(p => p.Length >= 2 && p[1] > 0).Select(p => ((int)p[0], p[1])).ToList();
        var saved = new DBTitleInfo { TitleId = next.Id };
        byte[] bytes = saved.ToByteArray();
        long role = s.Role!.Id;
        var paid = await _character.ExchangeLockedAsync(s, cost, [], popup: false,
            persist: tx =>
            {
                tx.RoleBlob(role, "title", bytes);
                tx.WithEconomy(_character.FamilyEconomy(s, "cd13a/character-title", cost, [], new { Title = next.Id }));
            },
            committed: () => s.TitleInfo = saved);
        if (paid == null) return ErrorCodes.ItemsShort;
        await CharacterService.SyncAsync(s, Sync, s.TitleInfo.Clone()); // an increment after the login's full sync
        await _character.RefreshStatsAsync(s);
        return 0;
    }

    /// <summary>
    /// (inferred) A boss (NPCConfig _iNPCSubType 3, 4 or 6) drops a Crest at ServerOptions.TitleCrestPercent to a role
    /// whose title function is open: the tier of the boss's level (Journey below 50, then one tier per 10 levels, Sun
    /// 90-94, Heroes 95-99, the Hero Token from 100), matching the ranks that spend them.
    /// </summary>
    private IEnumerable<(int Entry, int Count)> CrestDrops(GameSession s, MonsterUnit monster)
    {
        if (_options.TitleCrestPercent <= 0 || s.Progress == null || !_functions.IsOpen(s, Function)) yield break;
        if (_data.Row("NPCConfig", monster.Template.NpcId) is not { } row || new NPCConfig(row).NPCSubType is not (3 or 4 or 6)) yield break;
        if (Random.Shared.Next(100) >= _options.TitleCrestPercent) yield break;
        int level = monster.Template.Level;
        int crest = level switch
        {
            < 50 => 1250801,
            < 60 => 1250802,
            < 70 => 1250803,
            < 80 => 1250804,
            < 90 => 1250805,
            < 95 => 1250806,
            < 100 => 1250807,
            _ => 1250808,
        };
        if (_data.Row("ItemConfig", crest) != null) yield return (crest, 1);
    }

    private async Task GmAsync(GameSession s, string[] a)
    {
        if (a.Length >= 2 && a[0] == "set" && long.TryParse(a[1], out long rank) && s.TitleInfo != null)
        {
            var saved = new DBTitleInfo { TitleId = rank };
            _roles.SaveTitle(s.Role!.Id, saved.ToByteArray());
            s.TitleInfo = saved;
            await CharacterService.SyncAsync(s, Sync, saved.Clone());
        }
        var next = Next(s.TitleInfo?.TitleId ?? 0);
        await GmCommands.ReplyAsync(s, $"title {s.TitleInfo?.TitleId ?? 0}, next {next?.Id ?? 0} (level {next?.Lev ?? 0}, power {next?.Power ?? 0}), "
            + $"history power {PowerService.Total(PowerService.History(s))}, open {_functions.IsOpen(s, Function)}");
    }
}
