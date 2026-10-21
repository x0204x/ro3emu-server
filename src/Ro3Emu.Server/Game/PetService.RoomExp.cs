using System.Security.Cryptography;
using Google.Protobuf;

namespace Ro3Emu.Server.Game;

public sealed partial class PetService
{
    internal const int AddIncubateRoomExpAction = 603;

    /// <summary>
    /// 603 AddPetIncubateRoomExp [603, exp]: the hatchery by-products 1250725..1250731 ("Dismantle to receive @{1}
    /// Proficiency", 5..160 each) add their EXP to the room's proficiency bar (DBPetInfo.incubationExp) times the count
    /// used. The item and the EXP are one commit; a full bar then levels the room the way a collect does
    /// (AutoRoomLevelAsync: each affordable PetIncubationRoomConfig step is its own exchange). 5017 follows.
    /// </summary>
    private async Task<int> UseRoomExpItemAsync(ItemActionUse use)
    {
        var s = use.Session;
        int code = Gate(s, incubation: true);
        if (code != 0) return code;
        long gain = use.Effects.Sum(e => e.Length > 1 ? e[1] : 0) * use.Count;
        if (gain <= 0) return ErrorCodes.ItemNoConfig;
        var next = s.Pets!.Clone();
        next.IncubationExp = checked(next.IncubationExp + gain);
        UpdatePower(next);
        byte[] saved = next.ToByteArray();
        if (!await _character.CommitItemActionAsync(use, tx => tx.RoleBlob(s.Role!.Id, "pets", saved), () => s.Pets = next,
            new { Exp = gain, StateHash = Convert.ToHexString(SHA256.HashData(saved)) }, kind: "cd13b/pet-room-exp"))
            return ErrorCodes.ItemsShort;
        await AutoRoomLevelAsync(s);
        await SyncAsync(s);
        return 0;
    }
}
