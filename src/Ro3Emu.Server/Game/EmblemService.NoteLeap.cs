using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class EmblemService
{
    // 11310 mode4完整列已含+85000倍率、10目標與原140003消耗；不再加倍率或造第六次充能。
    // mode5沒有可證實授權來源，不能僅因外觀相近開放。當次epoch失效即不授權，舊buff無法自行武裝。
    private static SkillParameterRules.Modifier[] NoteLeapSkillEffects(GameSession session, GameData data) =>
        session.SkillProtocol.NoteLeapReady?.Invoke() == true
        && SkillParameterRules.HasBuffGroup(session, data, 11324)
        && session.Energy.TryGetValue(140003, out var energy) && energy.Value > 0
        ? [new(11310, 15, 2, 4, 0)] : [];
}
