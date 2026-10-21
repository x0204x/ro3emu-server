using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The Monster Codex (Library tab "Monster", LibraryConfig 100200, function 6001; LC_DAL_Library,
/// UI_Sys_LibraryList_MonsterWidgetCtrl, UI_Sys_LibraryListPanelModel.SendMonsterActive).
/// A kill of a Codex monster (NPCConfig._iWildMonsters 1, the client's Lua_CFG_NPCConfig.GetAllWildMonster) adds
/// 5057 monsters[npc] = {monsterId, killCount + 1} in the kill's own transaction, together with its first card sight
/// (StageFirstCardSight); a new entry is locked. For an entry that is present and not unlocked the panel sends 82407
/// {monsterId}: the server marks it unlocked and grants PublicParamConfig.LibraryMonsterUnlockReward (10) Collection
/// Points, the figure the monster's detail shows (m_kTTXT_Point) and the help's "Defeating a Monster for the first time
/// ... grants Collection Points" (Localization 10960000100). The reply carries only the result; the panel takes the
/// unlock from the 5057 change (_onSyncUpdateMonsterInfo). The research titles at 10/100/1000 kills
/// (LibraryMonsterKillReward) are derived by the client from killCount.
/// (inferred) Only the killer counts, as for the kill's loot; kills count before function 6001 opens, the unlock needs
/// it; the points are the overview's item LibraryOverviewPointID (118, read by LC_Library_Helper.GetLibraryScore),
/// since LibraryConfig._iPointId is 0 for every tab.
/// </summary>
public sealed partial class CardService
{
    public const uint LibraryMonsterUnlock = 82407;
    private const int MonsterLibraryFunction = 6001; // LibraryConfig 100200 _iFunctionID

    /// <summary>A kill of a Codex monster counts in <paramref name="library"/>; false when the NPC is not one.</summary>
    private bool CountMonsterKill(DBLibraryInfo library, int npc)
    {
        if (npc <= 0 || _data.Row("NPCConfig", npc) is not { } row || GameData.Int(row, "_iWildMonsters") != 1) return false;
        if (!library.Monsters.TryGetValue(npc, out var entry)) library.Monsters[npc] = entry = new LibraryMonsterInfo { MonsterId = npc };
        if (entry.KillCount < int.MaxValue) entry.KillCount++;
        return true;
    }

    /// <summary>82407: unlock a discovered Codex monster; its Collection Points and the flag are one owned-award receipt.</summary>
    public async Task UnlockMonsterAsync(GameSession session, LibraryMonsterUnlockReq req)
    {
        int result;
        using (await CharacterService.Lock(session))
        {
            var entry = req.MonsterId is > 0 and <= int.MaxValue ? session.Library?.Monsters.GetValueOrDefault((int)req.MonsterId) : null;
            result = session.Library == null ? ErrorCodes.Unknown
                : !_functions.IsOpen(session, MonsterLibraryFunction) ? ErrorCodes.FunctionLocked
                : entry == null ? ErrorCodes.CodexUnavailable
                : entry.Unlock ? ErrorCodes.AlreadyUnlocked
                : await CommitMonsterUnlockAsync(session, (int)req.MonsterId);
        }
        if (result == 0) await CharacterService.SyncAsync(session, LibrarySync, session.Library!.Clone());
        await session.SendGameAsync(LibraryMonsterUnlock, new LibraryMonsterUnlockResp { Result = result });
    }

    private async Task<int> CommitMonsterUnlockAsync(GameSession session, int monster)
    {
        long role = session.Role!.Id;
        var next = session.Library!.Clone();
        next.Monsters[monster].Unlock = true;
        int points = Param("LibraryMonsterUnlockReward"), item = Param("LibraryOverviewPointID");
        var plan = new AwardPlan(0, 0, points > 0 && item > 0 ? [new AwardGrant(item, points)] : []);
        byte[] bytes = next.ToByteArray();
        try
        {
            bool ok = await _character.ApplyAwardAsync(session, plan, persist: tx => _roles.SaveLibrary(role, bytes, tx),
                stateLockHeld: true, committed: () => session.Library = next,
                ownedAward: new CharacterService.OwnedAwardSource("cd13e/library-award", $"library-monster/{role}/{monster}",
                    new { Version = 1, Monster = monster, Item = item, Points = points }));
            if (ok) _log.LogInformation("{Name} unlocks Codex monster {Monster}: {Points} x {Item}", session.Role.Name, monster, points, item);
            return ok ? 0 : ErrorCodes.Unknown;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogError(e, "Codex monster {Monster} unlock failed for {Role}", monster, role);
            return ErrorCodes.Unknown;
        }
    }

    private int Param(string key) => _data.Table("PublicParamConfig").TryGetValue(key, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : 0;
}
