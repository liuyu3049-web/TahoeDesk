using System;
using System.Collections.Generic;
using GlassShell.Core.Shell;

namespace GlassShell.Core.Discovery;

public interface IWindowTracker : IDisposable
{
    IReadOnlyList<WindowInfo> GetTopLevelWindows();
    event Action? WindowsChanged;
    event Action<ForegroundWindowInfo>? ForegroundChanged;
    event Action? TaskbarShownDetected;
    void Start();
    void Stop();
}
