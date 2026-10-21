using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class EquipmentService
{
    public const uint ItemAutoDecomposeSetting = 31328, ItemLockDecompose = 31329, ItemAutoDecomposeNotice = 31330;
    private const int FunctionDecompose = 2602;

    /// <summary>
    /// 31329: set, rather than toggle, an owned instance's protection. The response handler
    /// is empty; the owning dataset (5009 bag/socket, 5007 worn) carries isLocked. Persistence precedes reply (inferred).
    /// </summary>
    public async Task LockDecomposeAsync(GameSession session, ItemLockDecomposeReq req)
    {
        using var _ = await CharacterService.Lock(session);
        session.Owners!.Current!.BindPresentation(session);
        var item = session.Items.FirstOrDefault(i => i.Guid == req.ItemGuid);
        if (item != null)
        {
            var after = session.Items.Select(i => i.Clone()).ToList();
            after.Single(i => i.Guid == item.Guid).IsLocked = req.IsLocked;
            var tx = _roles.NewTransaction();
            tx.ItemDelta(session.Role!.Id, session.Items, after);
            tx.WithEconomy(_character.FamilyEconomy(session, "cd13a/equipment-lock", [], [],
                new { Item = item.Guid, Locked = req.IsLocked }));
            await _roles.CommitAsync(tx, "equipment/lock", _ => item.IsLocked = req.IsLocked);
            if (item.Grid > 0) await _character.SyncEquipmentAsync(session);
            else await _character.ItemsChangedAsync(session, stats: false);
        }
        await session.SendGameAsync(ItemLockDecompose, new ItemLockDecomposeResp
        {
            Result = item == null ? ErrorCodes.ItemMissing : 0, ItemGuid = req.ItemGuid, IsLocked = item?.IsLocked ?? false,
        });
    }

    /// <summary>
    /// 31328 / 5009 field 80: each request replaces one tab only. Keys are condition IDs from
    /// DecomposeConfig._kAutomatic, values belong to DecomposeConditionConfig._kParam (LC_CompositionHelper:2407-2441).
    /// Reject malformed filters before changing the saved setting (inferred server validation).
    /// </summary>
    public async Task AutoDecomposeSettingAsync(GameSession session, ItemAutoDecomposeSettingReq req)
    {
        using var _ = await CharacterService.Lock(session);
        session.Owners!.Current!.BindPresentation(session);
        int result = ValidateSetting(req);
        if (result == 0 && !_functions.IsOpen(session, FunctionDecompose)) result = ErrorCodes.FeatureUnavailable;
        if (result == 0)
        {
            var next = session.AutoDecompose.Clone();
            next.TabOptionSetting[req.TabId] = req.Clone();
            var tx = _roles.NewTransaction();
            tx.ItemDelta(session.Role!.Id, session.Items, session.Items);
            tx.RoleBlob(session.Role.Id, "auto_decompose", next.ToByteArray());
            tx.WithEconomy(_character.FamilyEconomy(session, "cd13a/equipment-auto-decompose-setting", [], [],
                new { req.TabId, State = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(next.ToByteArray())) }));
            await _roles.CommitAsync(tx, "equipment/auto-decompose-setting", _ => session.AutoDecompose = next);
            await _character.ItemsChangedAsync(session, stats: false);
        }
        await session.SendGameAsync(ItemAutoDecomposeSetting, new ItemAutoDecomposeSettingResp { Result = result });
    }

    private int ValidateSetting(ItemAutoDecomposeSettingReq req)
    {
        if (_data.Row("DecomposeConfig", req.TabId) is not { } tab) return ErrorCodes.DecomposeTabMissing;
        var allowed = Flat(tab, "_kAutomatic");
        if (allowed.Length == 0) return ErrorCodes.Parameter;
        foreach (var (id, group) in req.OptionSetting)
        {
            if (!allowed.Contains(id) || _data.Row("DecomposeConditionConfig", id) is not { } condition) return ErrorCodes.Parameter;
            var options = Flat(condition, "_kParam");
            if (group.Options.Distinct().Count() != group.Options.Count || group.Options.Any(o => !options.Contains(o))) return ErrorCodes.Parameter;
        }
        return 0;
    }

    /// <summary>
    /// (inferred) Apply saved automatic filters on new GrantAsync loot/quest awards only, once, never on purchases,
    /// crafting, GM grants or dismantle returns. OR within a selected group, AND across nonempty groups, as the manual
    /// sidebar filter (LC_CompositionHelper:2228-2270); no selections means no dismantling. Only configured automatic
    /// tabs participate. Locks and worn/socketed instances are always excluded.
    /// </summary>
    private IReadOnlyList<(int Entry, long Count)> AutoReturns(GameSession session, InvItem item)
    {
        if (item.IsLocked || item.Grid != 0 || !_functions.IsOpen(session, FunctionDecompose)
            || _data.Row("ItemConfig", item.Entry) is not { } row) return [];
        foreach (var setting in session.AutoDecompose.TabOptionSetting.Values)
        {
            if (!setting.IsOpen || ValidateSetting(setting) != 0) continue;
            var tab = _data.Row("DecomposeConfig", setting.TabId)!.Value;
            long type = GameData.Int(row, "_iType"), subtype = GameData.Int(row, "_iSubType");
            // The client accepts the type list OR an explicit [type, subtype] pair (LC_CompositionHelper:480-525).
            if (!Flat(tab, "_iType").Contains(type) && !GameData.Pairs(tab, "_iSubType").Any(p => p.Item1 == type && p.Item2 == subtype)) continue;
            var selected = setting.OptionSetting.Where(g => g.Value.Options.Count > 0).ToList();
            if (selected.Count == 0) continue;
            if (selected.All(g => g.Value.Options.Any(o => Matches(item, row, (int)GameData.Int(_data.Row("DecomposeConditionConfig", g.Key)!.Value, "_iType"), o))))
                return Returns(item);
        }
        return [];
    }

    /// <summary>LC_CompositionHelper.CheckItemByFilter: quality, equip part, exact entry count, presence of specials.</summary>
    private static bool Matches(InvItem item, System.Text.Json.JsonElement row, int type, int option) => type switch
    {
        1 => GameData.Int(row, "_iQuality") == option,
        2 => GameData.Int(row, "_iEquipPart") == option,
        3 => CharacterService.IsAppraised(item) && EntryCount(item) == option,
        4 => CharacterService.IsAppraised(item) && (SpecialCount(item) == 0) == (option == 0),
        _ => false,
    };

    // ItemAttrTypeEnum / LC_ItemProtoParse.ParseEquipInfo: contiguous entry key/value pairs and special IDs.
    private static int EntryCount(InvItem item) => EquipmentAffixes.Entries(item).Count();

    private static int SpecialCount(InvItem item) => EquipmentAffixes.Specials(item).Count();
}
