using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using Ro3Emu.Loc;

namespace Ro3Emu.Loc.Tests;

public sealed class TraditionalChineseProfileTests
{
    [Fact]
    public void EnglishLabelsAndNativeChineseUseTraditionalProfileInsteadOfEnglishMap() =>
        Profile(() =>
        {
            Assert.Equal("背包", Ro3Loc.Map("Backpack"));
            Assert.Equal("背包", Ro3Loc.Map("背包"));
            Assert.Equal("技能", Ro3Loc.MapAwake("Skills"));
            Assert.Equal("交易所", Ro3Loc.MapHud("Trade"));
            Assert.Equal("<color=#fff>技能</color>", Ro3Loc.Map("<color=#fff>Skills</color>"));
            Assert.Equal("123.45%", Ro3Loc.Map("123.45%"));
        });

    [Fact]
    public void SidDisambiguatesIdenticalEnglishAndExactQuestCorrectionStillWins() =>
        Profile(() =>
        {
            Assert.Equal("下一階", Ro3Loc.Sid("None", 71001));
            Assert.Equal("下一頁", Ro3Loc.Sid("71002", 71002));
            Assert.Equal("原生新版譯文", Ro3Loc.Sid("原生新版譯文", 71001));
            Assert.Equal("先抵達對峙點支援保亞與晤勒，再擊敗虎王並通關「斐揚樹林」故事副本", Ro3Loc.Sid("Other", 13150600183));
        });

    [Fact]
    public void NativeGetterChangesAreObservedWithoutChangingPlayerSettings() =>
        Profile(() =>
        {
            string current = "ChineseTraditional";
            typeof(Ro3Loc).GetField("_nativeReader", BindingFlags.Static | BindingFlags.NonPublic)
                .SetValue(null, (Func<string>)(() => current));
            Assert.Equal("背包", Ro3Loc.Map("Backpack"));
            current = "English";
            Assert.Equal("Backpack", Ro3Loc.Map("背包"));
            current = "Korean";
            Assert.Equal("Skills", Ro3Loc.Map("Skills"));
            current = "ChineseTraditional";
            Assert.Equal("技能", Ro3Loc.Map("Skills"));
        });

    [Fact]
    public void NativeLanguageSwitchClearsPreviousProfileAndKeepsOtherLanguagesNative() =>
        Profile(() =>
        {
            Assert.Equal("背包", Ro3Loc.Map("Backpack"));
            Ro3Loc.UseNativeLanguage("en");
            Assert.Equal("Backpack", Ro3Loc.Map("背包"));
            Ro3Loc.UseNativeLanguage("ko");
            Assert.Equal("背包", Ro3Loc.Map("背包"));
            Ro3Loc.UseNativeLanguage("zh_TW");
            Assert.Equal("背包", Ro3Loc.Map("Backpack"));
        });

    [Fact]
    public void FormattedEnglishPreservesValuesAndRichText() =>
        Profile(() =>
        {
            Assert.Equal("剩餘 125 個", Ro3Loc.Map("Remaining 125 items"));
            Assert.Equal("<b>剩餘 125 個</b>", Ro3Loc.Map("<b>Remaining 125 items</b>"));
            Assert.Equal("Player Alice", Ro3Loc.Map("Player Alice"));
        });

    private static void Profile(Action check)
    {
        var type = typeof(Ro3Loc);
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        object gate = type.GetField("Gate", flags).GetValue(null);
        lock (gate)
        {
            string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ro3emu");
            Directory.CreateDirectory(folder);
            string[] names = { "loc_zh_TW.tsv", "loc_zh_TW.sids.tsv", "loc_en.tsv", "loc_zh_TW_misses.tsv" };
            var originals = names.ToDictionary(n => n, n => File.Exists(Path.Combine(folder, n)) ? File.ReadAllBytes(Path.Combine(folder, n)) : null);
            void Reset()
            {
                foreach (string name in new[] { "_map", "_templates", "_sids", "_traditionalSids", "_dir", "_nativeReader" }) type.GetField(name, flags).SetValue(null, null);
                type.GetField("_nativeProbed", flags).SetValue(null, false);
                type.GetField("_failed", flags).SetValue(null, false);
                type.GetField("_traditionalChinese", flags).SetValue(null, false);
                type.GetField("_nativeLanguage", flags).SetValue(null, null);
                ((IDictionary)type.GetField("Derived", flags).GetValue(null)).Clear();
                type.GetField("Misses", flags).GetValue(null).GetType().GetMethod("Clear").Invoke(type.GetField("Misses", flags).GetValue(null), null);
            }
            try
            {
                File.WriteAllText(Path.Combine(folder, names[0]), "# RO3Emu localization profile zh-TW v1\nBackpack\t背包\n背包\t背包\nSkills\t技能\nTrade\t交易所\nRemaining ${1} items\t剩餘 ${1} 個\n");
                File.WriteAllText(Path.Combine(folder, names[1]), "# RO3Emu localization profile zh-TW v1\n71001\t下一階\n71002\t下一頁\n");
                File.WriteAllText(Path.Combine(folder, names[2]), "背包\tBackpack\n技能\tSkills\n");
                Reset(); Ro3Loc.UseNativeLanguage("zh_TW"); check();
            }
            finally
            {
                foreach (var pair in originals)
                    if (pair.Value == null) File.Delete(Path.Combine(folder, pair.Key));
                    else File.WriteAllBytes(Path.Combine(folder, pair.Key), pair.Value);
                Reset();
            }
        }
    }
}
