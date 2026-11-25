using System;
using System.Collections.Generic;
using TahoeDesk.Core.Shell;

namespace TahoeDesk.Core.Discovery;

public interface IWindowTracker : IDisposable
{
    IReadOnlyList<WindowInfo> GetTopLevelWindows();
    event Action? WindowsChanged;
    event Action<ForegroundWindowInfo>? ForegroundChanged;
    event Action<nint>? WindowMinimizing;
    event Action? TaskbarShownDetected;
    void Start();
    void Stop();
}
