using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class EmblemService
{
    // 金色月輪11164301的原技能列：11604 mode2為+400，支配烙印11601期間的mode5
    // 則已含1.5倍、11165201減速20%/3000ms；不能將mode2加到Buff的mode4變成mode6。
    // 共鳴modifier位於活Buff之後，採set選完整變體；到期/撤共鳴由既有signature重算。
    // 同ID Buff11164301是另一個11608模式設定，不能把它當成本授權的執行Buff。
    private static SkillParameterRules.Modifier[] OverBrandSkillEffects(GameSession session, GameData data) =>
        [new(11604, 15, 2, SkillParameterRules.HasBuffGroup(session, data, 11601) ? 5 : 2, 0)];
}
