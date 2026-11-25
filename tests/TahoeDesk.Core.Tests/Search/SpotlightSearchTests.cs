using System.Collections.Generic;
using TahoeDesk.Core.Search;
using Xunit;

namespace TahoeDesk.Core.Tests.Search;

public sealed class SpotlightSearchTests
{
    [Fact]
    public void PinyinInitials_MapsCommonChineseAppNames()
    {
        Assert.Equal("wx", PinyinInitials.GetInitials("微信"));
        Assert.Equal("sz", PinyinInitials.GetInitials("设置"));
        Assert.Equal("jsq", PinyinInitials.GetInitials("计算器"));
        Assert.Equal("xz", PinyinInitials.GetInitials("下载"));
        Assert.Equal("vsc", PinyinInitials.GetInitials("VS Code"));
    }

    [Theory]
    [InlineData("2 + 3 * 4", "14")]
    [InlineData("(10 - 2) / 4", "2")]
    [InlineData("2^8", "256")]
    [InlineData("sqrt(81)", "9")]
    [InlineData("= 128 * 3.5", "448")]
    public void InstantCalculator_EvaluatesArithmeticAndPrecedence(string expr, string expected)
    {
        Assert.True(InstantCalculator.TryEvaluate(expr, out _, out string formatted));
        Assert.Equal(expected, formatted);
    }

    [Theory]
    [InlineData("2026")]
    [InlineData("123")]
    [InlineData("1 / 0")]
    [InlineData("hello world")]
    [InlineData("((1+2)")]
    public void InstantCalculator_RejectsPlainNumbersAndInvalidSyntax(string expr)
    {
        Assert.False(InstantCalculator.TryEvaluate(expr, out _, out _));
    }

    [Fact]
    public void SpotlightSearchEngine_RanksCalculatorAppsCommandsAndFiles()
    {
        var engine = new SpotlightSearchEngine();
        var apps = new List<InstalledAppEntry>
        {
            new("app:wechat", "微信", @"C:\WeChat.exe", null, false, PinyinInitials.GetInitials("微信")),
            new("app:vscode", "Visual Studio Code", @"C:\Code.exe", null, false, PinyinInitials.GetInitials("Visual Studio Code"))
        };
        var files = new List<IndexedFileEntry>
        {
            new("Q4_Report.pdf", @"C:\Users\Demo\Downloads\Q4_Report.pdf", false, PinyinInitials.GetInitials("Q4_Report.pdf"))
        };

        // Pinyin initials match
        var wxResults = engine.Search("wx", apps, files);
        Assert.NotEmpty(wxResults);
        Assert.Equal(SpotlightResultKind.Application, wxResults[0].Kind);
        Assert.Equal("微信", wxResults[0].Title);

        // Calculator ranks highest
        var calcResults = engine.Search("12 * 5", apps, files);
        Assert.NotEmpty(calcResults);
        Assert.Equal(SpotlightResultKind.Calculator, calcResults[0].Kind);
        Assert.Equal("60", calcResults[0].Title);

        // System command match
        var lockResults = engine.Search("锁屏", apps, files);
        Assert.Contains(lockResults, r => r.Kind == SpotlightResultKind.SystemCommand && r.Id == "cmd:lock");

        // Local file match
        var fileResults = engine.Search("report", apps, files);
        Assert.Contains(fileResults, r => r.Kind == SpotlightResultKind.File && r.Title == "Q4_Report.pdf");
    }
}
