using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using TahoeDesk.Core.Glass;
using TahoeDesk.Core.Shell;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct3D9;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;

namespace TahoeDesk.UI.Glass;

internal sealed class SharedGlassPipeline : IDisposable
{
    public required ID3D11VertexShader VertexShader { get; init; }
    public required ID3D11PixelShader BlurHPixelShader { get; init; }
    public required ID3D11PixelShader CompositePixelShader { get; init; }
    public required ID3D11SamplerState LinearSampler { get; init; }
    public required ID3D11Buffer ConstantBuffer { get; init; }

    public void Dispose()
    {
        ConstantBuffer.Dispose();
        LinearSampler.Dispose();
        CompositePixelShader.Dispose();
        BlurHPixelShader.Dispose();
        VertexShader.Dispose();
    }
}

public sealed class DxgiBackdropSampler : IBackdropSampler
{
    private sealed class OutputCaptureSlot : IDisposable
    {
        public required ScreenRect DesktopBounds { get; init; }
        public required IDXGIOutputDuplication Duplication { get; init; }
        public ID3D11Texture2D? Texture { get; set; }
        public ID3D11ShaderResourceView? Srv { get; set; }
        public long FrameVersion { get; set; }

        public void Dispose()
        {
            try { Duplication.Dispose(); } catch { }
            Srv?.Dispose();
            Texture?.Dispose();
            Srv = null;
            Texture = null;
        }
    }

    private readonly DxgiSamplerStateMachine _stateMachine = new();
    private readonly object _gate = new();
    private readonly List<OutputCaptureSlot> _outputs = [];

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDirect3D9Ex? _d3d9;
    private IDirect3DDevice9Ex? _d3d9Device;
    private SharedGlassPipeline? _sharedPipeline;
    private bool _pipelineInitAttempted;
    private bool _disposed;

    internal SharedGlassPipeline? GetOrCreateSharedGlassPipeline(uint constantBufferByteWidth)
    {
        lock (_gate)
        {
            if (_disposed || _device is null) return null;
            if (_sharedPipeline is not null) return _sharedPipeline;
            if (_pipelineInitAttempted) return null;
            _pipelineInitAttempted = true;

            try
            {
                string shaderPath = Path.Combine(AppContext.BaseDirectory, "Glass", "Shaders", "LiquidGlass.hlsl");
                if (!File.Exists(shaderPath)) return null;

                string hlsl = File.ReadAllText(shaderPath);
                var vsBlob = Compiler.Compile(hlsl, "VSMain", shaderPath, "vs_5_0");
                var psBlurBlob = Compiler.Compile(hlsl, "PSBlurH", shaderPath, "ps_5_0");
                var psCompBlob = Compiler.Compile(hlsl, "PSComposite", shaderPath, "ps_5_0");

                _sharedPipeline = new SharedGlassPipeline
                {
                    VertexShader = _device.CreateVertexShader(vsBlob.Span),
                    BlurHPixelShader = _device.CreatePixelShader(psBlurBlob.Span),
                    CompositePixelShader = _device.CreatePixelShader(psCompBlob.Span),
                    LinearSampler = _device.CreateSamplerState(new SamplerDescription
                    {
                        Filter = Filter.MinMagMipLinear,
                        AddressU = TextureAddressMode.Clamp,
                        AddressV = TextureAddressMode.Clamp,
                        AddressW = TextureAddressMode.Clamp
                    }),
                    ConstantBuffer = _device.CreateBuffer(new BufferDescription
                    {
                        ByteWidth = constantBufferByteWidth,
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.ConstantBuffer
                    })
                };
                return _sharedPipeline;
            }
            catch
            {
                return null;
            }
        }
    }

    public DxgiBackdropSampler()
    {
        TryInitializeDeviceAndDuplications();
    }

    public SamplerHealth Health => _stateMachine.Health;
    public int DesktopWidth => _outputs.Count > 0 ? _outputs[0].DesktopBounds.Width : 0;
    public int DesktopHeight => _outputs.Count > 0 ? _outputs[0].DesktopBounds.Height : 0;
    internal ID3D11Device? Device => _device;
    internal ID3D11DeviceContext? Context => _context;
    internal IDirect3DDevice9Ex? D3D9Device => _d3d9Device;

    public bool TryUpdateFrame()
    {
        return TryGetOutputForRect(new ScreenRect(0, 0, 1, 1), out _, out _, out _);
    }

    /// <summary>
    /// Re-enumerates DXGI desktop outputs after display hotplug, resolution/DPI change, or sleep/unlock resume.
    /// </summary>
    public void RefreshOutputs()
    {
        lock (_gate)
        {
            if (_disposed) return;
            DisposeOutputsOnly();
            if (_device is null || _context is null)
            {
                TryInitializeDeviceAndDuplications();
                return;
            }
            TryAcquireAllOutputs();
        }
    }

    internal bool TryGetOutputForRect(
        ScreenRect surfaceRectPx,
        out ID3D11ShaderResourceView? srv,
        out ScreenRect outputBounds,
        out long frameVersion,
        bool preferCachedFrame = false)
    {
        srv = null;
        outputBounds = default;
        frameVersion = 0;

        lock (_gate)
        {
            if (_disposed) return false;

            if (_stateMachine.Health == SamplerHealth.FallbackDegraded)
            {
                if (!_stateMachine.ShouldRetryAcquire()) return false;
                if (!TryAcquireAllOutputs()) return false;
            }

            if (_outputs.Count == 0 || _device is null || _context is null)
            {
                _stateMachine.ReportAccessLost();
                return false;
            }

            int cx = (surfaceRectPx.Left + surfaceRectPx.Right) / 2;
            int cy = (surfaceRectPx.Top + surfaceRectPx.Bottom) / 2;
            OutputCaptureSlot? slot = null;
            foreach (var candidate in _outputs)
            {
                var b = candidate.DesktopBounds;
                if (cx >= b.Left && cx < b.Right && cy >= b.Top && cy < b.Bottom)
                {
                    slot = candidate;
                    break;
                }
            }
            slot ??= _outputs[0];

            // Overlay cards opening over the desktop prefer the already-captured clean pre-popup frame
            // so popups never sample themselves even without WDA_EXCLUDEFROMCAPTURE.
            if (preferCachedFrame && slot.Srv is not null)
            {
                srv = slot.Srv;
                outputBounds = slot.DesktopBounds;
                frameVersion = slot.FrameVersion;
                return true;
            }

            IDXGIResource? desktopResource = null;
            try
            {
                var hr = slot.Duplication.AcquireNextFrame(0, out OutduplFrameInfo frameInfo, out desktopResource);
                if (hr == Vortice.DXGI.ResultCode.WaitTimeout)
                {
                    if (slot.Srv is not null)
                    {
                        srv = slot.Srv;
                        outputBounds = slot.DesktopBounds;
                        frameVersion = slot.FrameVersion;
                        return true;
                    }
                    return false;
                }

                if (hr.Failure || desktopResource is null)
                {
                    HandleAccessLost();
                    return false;
                }

                // Pointer-only update (mouse cursor moved, ZERO desktop pixels changed):
                // skip full-screen CopyResource and keep FrameVersion unchanged so all glass surfaces early-out at 0ms!
                if (frameInfo.AccumulatedFrames == 0 && frameInfo.LastPresentTime == 0 && slot.Srv is not null)
                {
                    slot.Duplication.ReleaseFrame();
                    srv = slot.Srv;
                    outputBounds = slot.DesktopBounds;
                    frameVersion = slot.FrameVersion;
                    return true;
                }

                using var frameTex = desktopResource.QueryInterface<ID3D11Texture2D>();
                var desc = frameTex.Description;
                EnsureSlotTexture(slot, (int)desc.Width, (int)desc.Height, desc.Format);

                if (slot.Texture is not null)
                {
                    _context.CopyResource(slot.Texture, frameTex);
                    slot.FrameVersion++;
                }

                slot.Duplication.ReleaseFrame();

                if (slot.Srv is not null)
                {
                    srv = slot.Srv;
                    outputBounds = slot.DesktopBounds;
                    frameVersion = slot.FrameVersion;
                    return true;
                }
                return false;
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

    private void TryInitializeDeviceAndDuplications()
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

            TryAcquireAllOutputs();
        }
        catch
        {
            _stateMachine.ReportAccessLost();
        }
    }

    private bool TryAcquireAllOutputs()
    {
        if (_device is null) return false;

        DisposeOutputsOnly();
        try
        {
            using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();

            uint idx = 0;
            while (adapter.EnumOutputs(idx, out IDXGIOutput output).Success && output is not null)
            {
                idx++;
                using (output)
                {
                    var desc = output.Description;
                    if (!desc.AttachedToDesktop) continue;
                    try
                    {
                        using var output1 = output.QueryInterface<IDXGIOutput1>();
                        var coords = desc.DesktopCoordinates;
                        var bounds = new ScreenRect(coords.Left, coords.Top, coords.Right, coords.Bottom);
                        var dup = output1.DuplicateOutput(_device);
                        _outputs.Add(new OutputCaptureSlot { DesktopBounds = bounds, Duplication = dup });
                    }
                    catch { }
                }
            }

            if (_outputs.Count > 0)
            {
                _stateMachine.MarkAcquired();
                return true;
            }

            _stateMachine.ReportAccessLost();
            return false;
        }
        catch
        {
            _stateMachine.ReportAccessLost();
            return false;
        }
    }

    private void EnsureSlotTexture(OutputCaptureSlot slot, int width, int height, Vortice.DXGI.Format format)
    {
        if (_device is null) return;
        if (slot.Texture is not null && slot.Texture.Description.Width == (uint)width && slot.Texture.Description.Height == (uint)height)
        {
            return;
        }

        slot.Srv?.Dispose();
        slot.Texture?.Dispose();

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

        slot.Texture = _device.CreateTexture2D(texDesc);
        slot.Srv = _device.CreateShaderResourceView(slot.Texture);
    }

    private void HandleAccessLost()
    {
        DisposeOutputsOnly();
        _stateMachine.ReportAccessLost();
    }

    private void DisposeOutputsOnly()
    {
        foreach (var o in _outputs) o.Dispose();
        _outputs.Clear();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            DisposeOutputsOnly();
            _sharedPipeline?.Dispose();
            _sharedPipeline = null;
            _d3d9Device?.Dispose();
            _d3d9?.Dispose();
            _context?.Dispose();
            _device?.Dispose();
            _d3d9Device = null;
            _d3d9 = null;
            _context = null;
            _device = null;
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetDesktopWindow();
}
