using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class PetService
{
    public const uint SelfChoose = 33313;
    internal const int SelfChoosePetAction = 204;

    /// <summary>
    /// ItemConfig _iUseEffect [[204, n, pet, pet...]] (ActionConfig 204 SelfChoosePet, ItemConfig._iSubType 912), e.g.
    /// the FirstHeartScoreConfig row 6 box 1250715 [[204,4,30004,31005,32001,33004]]. The client lists the pets from the
    /// third value on (UI_Com_RewardSelectWidgetCtrl BuildOptionList) and sends 31323 with one ItemUseParamInfo
    /// selfChoosePet.choosePet {option index from 0: pet id} (BuildUseParam). The box goes and the pet comes in one
    /// commit; an owned pet turns into its PetBaseConfig._kDecompose shards like an incubated or lottery duplicate.
    /// 33313 PetChooseNtf {petId} goes before 5017: LC_DAL_Pet._processPetChooseNtf asks HavePet for the new-pet panel.
    /// Which pets n limits is not used by the client; one box picks one pet (the box has no _iGroupUse).
    /// </summary>
    private async Task<int?> TryUseSelfChooseAsync(GameSession s, ItemUseReq req, InvItem item, JsonElement row, int count)
    {
        if (!row.TryGetProperty("_iUseEffect", out var effects) || effects.ValueKind != JsonValueKind.Array || effects.GetArrayLength() != 1
            || effects[0].ValueKind != JsonValueKind.Array || effects[0].GetArrayLength() < 3
            || GameData.Number(effects[0][0]) != SelfChoosePetAction) return null;
        var options = effects[0].EnumerateArray().Skip(2).Select(v => (int)(GameData.Number(v) ?? 0)).ToArray();
        var choice = req.ItemParam.Count == 1 && req.ItemParam[0].VarCase == ItemUseParamInfo.VarOneofCase.SelfChoosePet
            ? req.ItemParam[0].SelfChoosePet.ChoosePet : null;
        if (s.Pets == null) return ErrorCodes.FeatureUnavailable;
        if (count != 1 || choice is not { Count: 1 }) return ErrorCodes.Parameter;
        var (index, petId) = choice.Single();
        if (index < 0 || index >= options.Length || options[index] != petId) return ErrorCodes.Parameter;
        if (_data.Row("PetBaseConfig", petId) is not { } pet || !_stars.ContainsKey((petId, 1, 0))) return ErrorCodes.PetNoConfig;
        var next = s.Pets.Clone();
        var goods = new List<(int, int)>();
        if (!next.ListPet.ContainsKey(petId)) next.ListPet[petId] = NewPet(petId);
        else
        {
            var broken = Flat(pet, "_kDecompose");
            if (broken.Length != 2 || broken[1] <= 0 || broken[1] > int.MaxValue || _data.Row("ItemConfig", broken[0]) == null)
                return ErrorCodes.PetNoConfig;
            goods.Add(((int)broken[0], (int)broken[1]));
        }
        UpdatePower(next);
        byte[] saved = next.ToByteArray();
        if (await _character.ExchangeLockedAsync(s, [], goods, new Dictionary<long, int> { [item.Guid] = 1 }, popup: false,
            persist: tx =>
            {
                tx.RoleBlob(s.Role!.Id, "pets", saved);
                tx.WithEconomy(_character.FamilyEconomy(s, "cd13b/pet-self-choose", [(item.Entry, 1L)], goods,
                    new
                    {
                        Item = item.Entry, Pet = petId, Duplicate = goods.Count > 0,
                        StateHash = Convert.ToHexString(SHA256.HashData(saved))
                    }));
            }, committed: () => s.Pets = next, asyncCommit: true) == null) return ErrorCodes.ItemsShort;
        await s.SendGameAsync(SelfChoose, new PetChooseNtf { PetId = petId });
        await ChangedAsync(s, items: true);
        return 0;
    }
}
