using System;
using System.Runtime.InteropServices;
using GlassShell.Core.Glass;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct3D9;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;

namespace GlassShell.UI.Glass;

public sealed class DxgiBackdropSampler : IBackdropSampler
{
    private readonly DxgiSamplerStateMachine _stateMachine = new();
    private readonly object _gate = new();

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDirect3D9Ex? _d3d9;
    private IDirect3DDevice9Ex? _d3d9Device;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _desktopTexture;
    private ID3D11ShaderResourceView? _desktopSrv;
    private bool _disposed;

    public DxgiBackdropSampler()
    {
        TryInitializeDeviceAndDuplication();
    }

    public SamplerHealth Health => _stateMachine.Health;
    public int DesktopWidth { get; private set; }
    public int DesktopHeight { get; private set; }
    internal ID3D11Device? Device => _device;
    internal ID3D11DeviceContext? Context => _context;
    internal IDirect3DDevice9Ex? D3D9Device => _d3d9Device;
    internal ID3D11ShaderResourceView? DesktopSrv => _desktopSrv;

    public bool TryUpdateFrame()
    {
        lock (_gate)
        {
            if (_disposed) return false;

            if (_stateMachine.Health == SamplerHealth.FallbackDegraded)
            {
                if (!_stateMachine.ShouldRetryAcquire()) return false;
                if (!TryAcquireDuplication()) return false;
            }

            if (_duplication is null || _device is null || _context is null)
            {
                _stateMachine.ReportAccessLost();
                return false;
            }

            IDXGIResource? desktopResource = null;
            try
            {
                var hr = _duplication.AcquireNextFrame(0, out _, out desktopResource);
                if (hr == Vortice.DXGI.ResultCode.WaitTimeout)
                {
                    return _desktopSrv is not null;
                }

                if (hr.Failure || desktopResource is null)
                {
                    HandleAccessLost();
                    return false;
                }

                using var frameTex = desktopResource.QueryInterface<ID3D11Texture2D>();
                var desc = frameTex.Description;
                EnsureDesktopTexture((int)desc.Width, (int)desc.Height, desc.Format);

                if (_desktopTexture is not null)
                {
                    _context.CopyResource(_desktopTexture, frameTex);
                }

                _duplication.ReleaseFrame();
                return _desktopSrv is not null;
            }
            catch (SharpGenException)
            {
                HandleAccessLost();
                return false;
            }
            finally
            {
                desktopResource?.Dispose();
            }
        }
    }

    private void TryInitializeDeviceAndDuplication()
    {
        try
        {
            var featureLevels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
            var hr = D3D11CreateDevice(
                null,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                featureLevels,
                out _device,
                out _context);

            if (hr.Failure || _device is null || _context is null)
            {
                _stateMachine.ReportAccessLost();
                return;
            }

            // One single shared D3D9Ex device for all WPF D3DImage surfaces (avoids multi-device leaks/corruption)
            _d3d9 = D3D9.Direct3DCreate9Ex();
            var pp = new Vortice.Direct3D9.PresentParameters
            {
                Windowed = true,
                SwapEffect = Vortice.Direct3D9.SwapEffect.Discard,
                BackBufferFormat = Vortice.Direct3D9.Format.A8R8G8B8,
                BackBufferWidth = 1,
                BackBufferHeight = 1,
                DeviceWindowHandle = GetDesktopWindow(),
                PresentationInterval = PresentInterval.Immediate
            };
            _d3d9Device = _d3d9.CreateDeviceEx(
                0,
                DeviceType.Hardware,
                GetDesktopWindow(),
                CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve,
                pp);

            TryAcquireDuplication();
        }
        catch
        {
            _stateMachine.ReportAccessLost();
        }
    }

    private bool TryAcquireDuplication()
    {
        if (_device is null) return false;

        DisposeDuplicationOnly();
        try
        {
            using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            if (adapter.EnumOutputs(0, out IDXGIOutput output).Failure || output is null)
            {
                _stateMachine.ReportAccessLost();
                return false;
            }

            using (output)
            using (var output1 = output.QueryInterface<IDXGIOutput1>())
            {
                var bounds = output.Description.DesktopCoordinates;
                DesktopWidth = bounds.Right - bounds.Left;
                DesktopHeight = bounds.Bottom - bounds.Top;
                _duplication = output1.DuplicateOutput(_device);
                _stateMachine.MarkAcquired();
                return true;
            }
        }
        catch
        {
            _stateMachine.ReportAccessLost();
            return false;
        }
    }

    private void EnsureDesktopTexture(int width, int height, Vortice.DXGI.Format format)
    {
        if (_device is null) return;
        if (_desktopTexture is not null && DesktopWidth == width && DesktopHeight == height) return;

        _desktopSrv?.Dispose();
        _desktopTexture?.Dispose();

        DesktopWidth = width;
        DesktopHeight = height;

        var texDesc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        };

        _desktopTexture = _device.CreateTexture2D(texDesc);
        _desktopSrv = _device.CreateShaderResourceView(_desktopTexture);
    }

    private void HandleAccessLost()
    {
        DisposeDuplicationOnly();
        _stateMachine.ReportAccessLost();
    }

    private void DisposeDuplicationOnly()
    {
        try { _duplication?.Dispose(); } catch { }
        _duplication = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            DisposeDuplicationOnly();
            _desktopSrv?.Dispose();
            _desktopTexture?.Dispose();
            _d3d9Device?.Dispose();
            _d3d9?.Dispose();
            _context?.Dispose();
            _device?.Dispose();
            _desktopSrv = null;
            _desktopTexture = null;
            _d3d9Device = null;
            _d3d9 = null;
            _context = null;
            _device = null;
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetDesktopWindow();
}
