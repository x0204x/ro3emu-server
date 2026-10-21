using Romsg;
using Google.Protobuf;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;
using System.Security.Cryptography;

namespace Ro3Emu.Server.Game;

public sealed partial class PetService
{
    /// <summary>Stage a lottery pet in a cloned 5017 state. Duplicate conversion follows PetBaseConfig._kDecompose.</summary>
    internal bool TryStageLotteryPet(DBPetInfo next, int petId, out (int Entry, int Count)? broken)
    {
        broken = null;
        if (_data.Row("PetBaseConfig", petId) is not { } pet || !_stars.ContainsKey((petId, 1, 0))) return false;
        if (!next.ListPet.ContainsKey(petId))
        {
            next.ListPet[petId] = NewPet(petId);
            return true;
        }
        var material = Flat(pet, "_kDecompose");
        if (material.Length != 2 || material[0] <= 0 || material[0] > int.MaxValue
            || material[1] <= 0 || material[1] > int.MaxValue || _data.Row("ItemConfig", material[0]) == null) return false;
        broken = ((int)material[0], (int)material[1]);
        return true;
    }

    internal void FinalizeLotteryPets(DBPetInfo next) => UpdatePower(next);
    internal Task SyncLotteryPetsAsync(GameSession session) => SyncAsync(session);

    /// <summary>Bind resolved pet results, ticket debit, duplicate conversion and both blobs to one E05b source.</summary>
    internal EconomyMutation LotteryEconomy(GameSession session, int pool, int ticket, int count,
        DBPetInfo next, byte[] lottery, IReadOnlyList<(int Entry, int Count)> goods,
        IReadOnlyList<LotteryItemData> outcomes) => _character.FamilyEconomy(session, "cd13b/pet-lottery",
        [(ticket, (long)count)], goods, new
        {
            Pool = pool, Ticket = ticket, Count = count,
            Results = outcomes.Select(x => new { x.ItemId, x.ItemCount, x.IsPet, x.IsBroken }).ToArray(),
            PetHash = Convert.ToHexString(SHA256.HashData(next.ToByteArray())),
            LotteryHash = Convert.ToHexString(SHA256.HashData(lottery)),
        });
}
