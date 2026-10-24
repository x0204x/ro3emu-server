using Romsg;

namespace Ro3Emu.Server.Game;

/// <summary>
/// A scripted step's cover mechanic (the story Pharaoh's 3120702), inferred as a whole: the story scene has the Pyramid
/// 5300011 (the raid Pharaoh's pyramid model, "mvp法老王金字塔") but no script, and the raid's tip 4370002 says what it is
/// for: "Pharaoh is unleashing Cleansing Light! Quickly, hide behind the Pyramid!". The story Pharaoh's skills have no
/// names; the hit is its strongest (5340301, 10x ATK).
/// </summary>
public sealed partial class DungeonService
{
    private void StartShelter(Attempt a, ReconstructedDungeons.Shelter shelter)
    {
        int generation = a.StepGeneration;
        StepLater(a, generation, shelter.EveryMs / 2, () => ShelterWarnAsync(a, generation, shelter), whileFighting: true);
    }

    private async Task ShelterWarnAsync(Attempt a, int generation, ReconstructedDungeons.Shelter shelter)
    {
        if (ShelterBoss(a, shelter) is not { } boss) return;
        await BroadcastAsync(a, SystemBroadcast, new SystemBoardCastMsgNtf { Source = MsgSource.System, ChatId = shelter.Tip });
        // The boss holds its ground while it gathers the light, as during any special cast.
        boss.BusyUntilMs = Math.Max(boss.BusyUntilMs, MonsterService.NowMs + shelter.WarnMs);
        StepLater(a, generation, shelter.WarnMs, () => ShelterStrikeAsync(a, generation, shelter), whileFighting: true);
    }

    private async Task ShelterStrikeAsync(Attempt a, int generation, ReconstructedDungeons.Shelter shelter)
    {
        if (ShelterBoss(a, shelter) is not { } boss) return;
        var cover = a.SceneActors.FirstOrDefault(u => u.NpcId == shelter.Cover);
        var exposed = Present(a).Where(m => a.Loaded.Contains(m) && !m.SceneLoading && !m.Dead
            && (cover == null || !Sheltered(boss.X, boss.Z, cover.X, cover.Z, m.Position.X, m.Position.Z, shelter))).ToArray();
        if (exposed.Length > 0 && _data.Row("SkillConfig", shelter.Skill) is { } row)
            await _combat.MonsterSkillHitAsync(boss, row, exposed, boss.Uid);
        StepLater(a, generation, shelter.EveryMs - shelter.WarnMs, () => ShelterWarnAsync(a, generation, shelter), whileFighting: true);
    }

    private static Maps.MonsterUnit? ShelterBoss(Attempt a, ReconstructedDungeons.Shelter shelter) =>
        a.Room.Monsters.FirstOrDefault(m => m.Template.NpcId == shelter.Boss && !m.Dead && m.Hp > 0);

    /// <summary>Behind the cover as seen from the boss: past it along the boss-cover line, within the shadow's width
    /// and depth.</summary>
    internal static bool Sheltered(float bossX, float bossZ, float coverX, float coverZ, float x, float z,
        ReconstructedDungeons.Shelter shelter)
    {
        float vx = coverX - bossX, vz = coverZ - bossZ, d = MathF.Sqrt(vx * vx + vz * vz);
        if (d < 0.5f) return false; // the boss stands on the cover: no shadow
        float ux = vx / d, uz = vz / d, wx = x - bossX, wz = z - bossZ;
        float along = wx * ux + wz * uz, across = MathF.Abs(wx * uz - wz * ux);
        return along >= d && along <= d + shelter.Depth && across <= shelter.Width;
    }
}
