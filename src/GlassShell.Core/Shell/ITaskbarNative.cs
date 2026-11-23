namespace GlassShell.Core.Shell;

public interface ITaskbarNative
{
    nint FindPrimaryTaskbar();
    bool SetWindowVisible(nint hwnd, bool visible);
    bool RegisterTopAppBar(nint appBarHwnd, int heightPx, ScreenRect monitorBounds);
    void UnregisterAppBar(nint appBarHwnd);
    bool IsD3DFullscreenActive();
}
