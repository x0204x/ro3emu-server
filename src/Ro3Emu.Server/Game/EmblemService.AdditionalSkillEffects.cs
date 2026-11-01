using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class EmblemService
{
    // 精確stage授予列才進入；mode1原件已包含整套傷害、範圍與Buff2，不手工再加一次。
    // 11130101：11301各級+5000且新增901601/1500ms；11533101：三影技各級+1500、半徑+100cm。
    private static SkillParameterRules.Modifier[] AdditionalSkillEffects(int grant) => grant switch
    {
        11130101 => [new(11301, 15, 2, 1, 0)],
        11430601 => [new(14301, 15, 2, 1, 0)],
        11130201 => [new(11303, 15, 2, 1, 0)],
        11130501 => [new(11308, 15, 2, 1, 0)],
        11336301 => [new(13320, 15, 2, 1, 0)],
        11164501 => [new(11618, 15, 2, 1, 0)],
        // 11430801 同ID Buff 明確設定mode1；Choral整列含+60000治療係數、專用盾與詠唱，不能再加一次600%。
        11430801 => [new(14303, 15, 2, 1, 0)],
        11531601 => [new(15301, 15, 2, 1, 0)],
        11531901 => [new(15303, 15, 2, 1, 0)],
        11533101 => [new(15320, 15, 2, 1, 0), new(15322, 15, 2, 1, 0), new(15323, 15, 2, 1, 0)],
        _ => [],
    };
}
