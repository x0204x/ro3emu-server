using Microsoft.Extensions.Logging.Abstractions;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// #bug-reports "I have 163 Flee ... Roda Frog (Level 13) ... Every single attack hits me". The data decides per skill
/// row: _iSkillHit 10000 never misses (nearly every damaging skill and most monster normal attacks), 0 rolls against
/// FLEE (the players' normal attacks and some monsters': MVP swings, GvG and Pet Tower monsters).
/// </summary>
public sealed class FleeTests
{
    private static GameData Data()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new ServerOptions
        { GameDataDir = Path.Combine(ClientData.RepoRoot, "data") });
        return new GameData(options, NullLogger<GameData>.Instance);
    }

    [ClientDataFact]
    public void AFieldMonsterNormalAttackWithFullSkillHitNeverMisses()
    {
        var frog = Data().Row("SkillConfig", 5002101)!.Value; // Roda Frog's normal attack
        Assert.Equal(1, GameData.Int(frog, "_iIsNormalAtk"));
        Assert.Equal(10000, GameData.Int(frog, "_iSkillHit"));
        Assert.True(CombatService.NeverMisses(frog));
    }

    [ClientDataFact]
    public void AnMvpSwingAndAPlayerNormalAttackRollAgainstFlee()
    {
        var data = Data();
        var mvp = data.Row("SkillConfig", 4121501)!.Value; // MVPAttack
        Assert.Equal(1, GameData.Int(mvp, "_iIsNormalAtk"));
        Assert.False(CombatService.NeverMisses(mvp));
        var swordman = data.Row("SkillConfig", 1000001)!.Value; // a player's normal attack
        Assert.False(CombatService.NeverMisses(swordman));
        Assert.True(CombatService.NeverMisses(data.Row("SkillConfig", 1510205)!.Value)); // Sonic Blow
    }
}
