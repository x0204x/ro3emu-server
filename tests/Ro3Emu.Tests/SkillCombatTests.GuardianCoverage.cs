using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private static readonly string[] GuardianBuffFields = ["_kBuff1", "_kBuff2"];

    [ClientDataFact]
    public async Task GuardianEveryExportedFamilyAdmitsAndExecutesItsPrimaryThroughTheProtocol()
    {
        await using var alice = await InWorld("Alice");
        var data = _app.Services.GetRequiredService<GameData>();
        var source = Session("alice");
        var target = await Target("alice", stationary: true);
        var stages = data.Table("PetStarConfig").Values.Where(r => GameData.Int(r, "_iStar") == 1
            && GameData.Int(r, "_iStage") == 0 && GameData.Int(r, "_iProtectSkill") > 0).OrderBy(r => GameData.Int(r, "_iPetID")).ToArray();
        Assert.Equal(56, stages.Length);
        foreach (var stage in stages)
        {
            int pet = (int)GameData.Int(stage, "_iPetID");
            var pieces = GuardianPetRules.Numbers(stage, "_kPiece");
            await EquipGuardian(alice, pet, (int)pieces[0], pieces: (int)pieces[1]);
            await Loop.InvokeAsync(async () =>
            {
                target.Hp = 10000000;
                target.X = source.Position.X; target.Y = source.Position.Y; target.Z = source.Position.Z;
                source.SkillCds.Clear(); source.SkillModeCds.Clear();
                await Character.SetHpSpAsync(source, long.MaxValue, null);
            });
            var skill = _app.Services.GetRequiredService<PetService>().Guardians(source).Single(p => p.IsPrimary);
            await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq
            { SkillId = skill.Id, TargetId = target.Uid, TargetPos = target.Pos, Face = 0 });
            var reply = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
            Assert.True(reply.Result == 0, $"Pet {pet} rejected: {reply.Result}");
            if (skill.Kind is GuardianPetRules.Kind.Damage or GuardianPetRules.Kind.Heal)
                await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == skill.EffectId && h.SessionId == reply.SessionId);
            else
            {
                var entries = GuardianBuffFields.Select(key => skill.Effect.GetProperty(key))
                    .Where(v => v.ValueKind == System.Text.Json.JsonValueKind.Array).SelectMany(v => v.EnumerateArray()).ToArray();
                Assert.NotEmpty(entries);
                int buff = (int)(GameData.Number(entries[0][0]) ?? 0);
                await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser, n => n.Buffs.Any(b => b.ConfigId == buff));
            }
        }
    }
}
