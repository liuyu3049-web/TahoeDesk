using TahoeDesk.Core.MenuEngine;
using Xunit;

namespace TahoeDesk.Core.Tests.MenuEngine;

public sealed class FriendlyAppNameResolverTests
{
    [Theory]
    [InlineData("explorer", "CabinetWClass", "下载", "文件资源管理器")]
    [InlineData("explorer", "Progman", "Program Manager", "桌面")]
    [InlineData("explorer", "WorkerW", "", "桌面")]
    [InlineData("chrome", "Chrome_WidgetWin_1", "Google", "Chrome")]
    [InlineData("msedge", "Chrome_WidgetWin_1", "New Tab", "Edge")]
    [InlineData("Code", "Chrome_WidgetWin_1", "App.cs", "VS Code")]
    [InlineData("Weixin", "Qt51514QWindowIcon", "微信", "微信")]
    [InlineData("WeChat", "WeChatMainWndForPC", "微信", "微信")]
    [InlineData("ApplicationFrameHost", "ApplicationFrameWindow", "设置", "设置")]
    public void Resolve_MapsProcessAndClassToCleanChineseDisplayName(
        string processName, string className, string windowTitle, string expected)
    {
        Assert.Equal(expected, FriendlyAppNameResolver.Resolve(processName, className, windowTitle));
    }
}
