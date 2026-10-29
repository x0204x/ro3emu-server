using Ro3Emu.Loc;

namespace Ro3Emu.Loc.Tests;

public sealed class QuestConditionOverrideTests
{
    [Theory]
    [InlineData(13150600172, "應付周圍不斷湧來的魔物", "清除全部 3 波來襲的魔物，並通關「斐揚南部」故事副本")]
    [InlineData(13150600183, "擊敗兇殘的虎王", "先抵達對峙點支援保亞與晤勒，再擊敗虎王並通關「斐揚樹林」故事副本")]
    [InlineData(13150600199, "擊敗兇殘的大嘴鳥", "擊敗 1 隻兇殘的大嘴鳥並通關「大嘴鳥巢」副本")]
    public void TaskTargetSidGetsItsExactCondition(long sid, string shipped, string expected) =>
        Assert.Equal(expected, Ro3Loc.Sid(shipped, sid));

    [Theory]
    [InlineData(13150200300, "應付周圍不斷湧來的魔物")]
    [InlineData(13150100495, "偷蛋的下場")]
    [InlineData(13150100506, "危險的戰鬥")]
    public void OtherRowsStayUnchanged(long sid, string shipped) =>
        Assert.Equal(shipped, Ro3Loc.Sid(shipped, sid));

}
