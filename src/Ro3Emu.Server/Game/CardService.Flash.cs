using System.Text.Json;
using Microsoft.Data.Sqlite;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class CardService
{
    /// <summary>
    /// 31469 The client submits four bag instance GUIDs, including repetitions
    /// from one stack (UI_Sys_CardCollectionSynthesisWidgetCtrl.lua:838-900,924-1055). Its selection
    /// admits normal, Flash and Gray card instances with zero star and one eligible quality
    /// (LC_CardViewHelper.lua:528-608); a Flash input gets a confirmation, not a refusal.
    /// </summary>
    public async Task TreasuredAsync(GameSession session, CardTreasuredReq req)
    {
        using var _ = await CharacterService.Lock(session);
        int error = 0;
        int output = 0;
        InvItem? granted = null;
        var selected = new Dictionary<long, int>();
        int quality = 0;

        if (!_functions.IsOpen(session, 1504)) error = ErrorCodes.FeatureUnavailable;
        if (error == 0 && ((int)req.SourceType != 1 || req.Guids.Count != 4 || req.Guids.Any(guid => guid <= 0)))
            error = ErrorCodes.Parameter;
        if (error == 0)
        {
            foreach (long guid in req.Guids)
                selected[guid] = selected.GetValueOrDefault(guid) + 1;
            foreach (var (guid, count) in selected)
            {
                var card = session.Items.FirstOrDefault(item => item.Guid == guid);
                if (card == null || card.Grid != 0 || !_character.IsCard(card.Entry))
                {
                    error = ErrorCodes.ItemMissing;
                    break;
                }
                if (card.IsLocked || EmblemService.IsReserved(session, card))
                {
                    error = ErrorCodes.ItemLocked;
                    break;
                }
                if (card.Stack < count)
                {
                    error = ErrorCodes.ItemsShort;
                    break;
                }
                // The Lua list excludes starred cards, but does not exclude levelled cards.
                int cardType = (int)(card.Attrs?.GetValueOrDefault(530) ?? 0);
                if ((card.Attrs?.GetValueOrDefault(502) ?? 0) != 0 || cardType is < 0 or > 2 || card.BindType is < 0 or > 2)
                {
                    error = ErrorCodes.Parameter;
                    break;
                }
                var itemRow = _data.Row("ItemConfig", card.Entry);
                int thisQuality = itemRow is { } row ? (int)GameData.Int(row, "_iQuality") : 0;
                if (thisQuality is not (4 or 5 or 6) || quality != 0 && thisQuality != quality)
                {
                    error = ErrorCodes.Parameter;
                    break;
                }
                quality = thisQuality;
            }
        }

        // Each current output row is for playbook 101 and opens on day one. Refuse missing or
        // mismatched season/configuration before charging any input.
        JsonElement? outputRow = null;
        if (error == 0)
        {
            int playBook = (int)(session.SeasonState?.PlayBookId ?? 0);
            outputRow = _data.Table("FlashCardOutputConfig").Values.FirstOrDefault(row =>
                GameData.Int(row, "_iQuality") == quality &&
                row.TryGetProperty("_kMultiverseArray", out var books) && books.ValueKind == JsonValueKind.Array &&
                books.EnumerateArray().Any(book => book.GetInt32() == playBook));
            if (outputRow is not { ValueKind: JsonValueKind.Object }) error = ErrorCodes.FeatureUnavailable;
            else
            {
                int openDay = (int)GameData.Int(outputRow.Value, "_iOpenDay");
                int currentDay = (_clock.GetLocalNow().Date - _season.Start.Date).Days + 1;
                if (openDay <= 0 || currentDay < openDay && !_season.OpenContent.ServerDayFree || session.SeasonState!.SeasonId != _season.SeasonId)
                    error = ErrorCodes.FeatureUnavailable;
            }
        }
        if (error == 0)
        {
            output = RollFlashOutput(outputRow!.Value);
            if (output == 0 || !_character.IsCard(output)) error = ErrorCodes.ItemNoConfig;
        }

        if (error == 0)
        {
            // StageFirstCardSight enlists a new 5057 entry in this exchange's transaction.
            // (inferred) Permanent binding dominates timed binding, which dominates free items.
            // A timed result keeps the latest expiry, so synthesis never shortens a surviving bind.
            var inputs = selected.Keys.Select(guid => session.Items.Single(item => item.Guid == guid)).ToArray();
            bool permanent = inputs.Any(item => item.BindType == 1 || item.BindType == 2 && item.BindExpire <= 0);
            int bindType = permanent ? 1 : inputs.Any(item => item.BindType == 2) ? 2 : 0;
            long bindExpire = bindType == 2 ? inputs.Where(item => item.BindType == 2).Max(item => item.BindExpire) : 0;
            try
            {
                var result = await _character.ExchangeLockedAsync(session, [], [], selected,
                    popup: false, sync: false,
                    persist: builder =>
                    {
                        builder.WithEconomy(_character.FamilyEconomy(session, "cd13a/card-flash",
                            inputs.Select(item => (item.Entry, (long)selected[item.Guid])).ToArray(),
                            [(output, 1)], new { Output = output, bindType, bindExpire }));
                    },
                    attributedGoods: [new CharacterService.AttributedGood(output, 1, bindType, bindExpire, 1)],
                    asyncCommit: true);
                if (result == null || result.Count != 1) error = ErrorCodes.ItemsShort;
                else granted = result[0];
            }
            catch (SqliteException exception)
            {
                _log.LogError(exception, "Flash card synthesis transaction failed for role {RoleId}", session.Role!.Id);
                error = ErrorCodes.Unknown;
            }
        }
        if (granted != null)
        {
            await CharacterService.SyncAsync(session, LibrarySync, session.Library!.Clone());
            await _character.ItemsChangedAsync(session, stats: false); // 5009 must precede 31469 presentation.
        }
        await session.SendGameAsync(Treasured, new CardTreasuredResp
        {
            Result = error, Guid = granted?.Guid ?? 0, Itemid = granted?.Entry ?? 0,
        });
    }

    private int RollFlashOutput(JsonElement outputRow)
    {
        if (!outputRow.TryGetProperty("_kOutputPool", out var groups) || groups.ValueKind != JsonValueKind.Array)
            return 0;
        var eligible = new List<JsonElement>();
        foreach (var group in groups.EnumerateArray())
            eligible.AddRange(_data.Table("FlashCardPoolConfig").Values.Where(row =>
                GameData.Int(row, "_iGroupID") == group.GetInt32() && GameData.Int(row, "_iWeight") > 0 &&
                row.TryGetProperty("_kCardList", out var cards) && cards.ValueKind == JsonValueKind.Array && cards.GetArrayLength() > 0));
        long total = eligible.Sum(row => GameData.Int(row, "_iWeight"));
        if (total <= 0) return 0;
        // (inferred) Weight selects a pool row; card entries within that row are equiprobable.
        long roll = Random.Shared.NextInt64(total);
        foreach (var row in eligible.OrderBy(row => GameData.Int(row, "_iID")))
        {
            roll -= GameData.Int(row, "_iWeight");
            if (roll >= 0) continue;
            var cards = row.GetProperty("_kCardList");
            return cards[Random.Shared.Next(cards.GetArrayLength())].GetInt32();
        }
        return 0;
    }
}
