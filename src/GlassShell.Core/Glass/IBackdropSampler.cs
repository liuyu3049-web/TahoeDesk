using System;

namespace GlassShell.Core.Glass;

public enum SamplerHealth
{
    Ready,
    FallbackDegraded
}

public interface IBackdropSampler : IDisposable
{
    SamplerHealth Health { get; }
    int DesktopWidth { get; }
    int DesktopHeight { get; }
    bool TryUpdateFrame();
}
