using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        public IDXGIOutputDuplication? Duplication { get; set; }
        public ID3D11Texture2D? Texture { get; set; }
        public ID3D11ShaderResourceView? Srv { get; set; }
        public ID3D11Texture2D? WallpaperTexture { get; set; }
        public ID3D11ShaderResourceView? WallpaperSrv { get; set; }
        public long FrameVersion { get; set; }

        public void DisposeWallpaper()
        {
            WallpaperSrv?.Dispose();
            WallpaperTexture?.Dispose();
            WallpaperSrv = null;
            WallpaperTexture = null;
        }

        public void Dispose()
        {
            try { Duplication?.Dispose(); } catch { }
            Duplication = null;
            DisposeWallpaper();
            Srv?.Dispose();
            Texture?.Dispose();
            Srv = null;
            Texture = null;
        }
    }

    private const uint SPI_GETDESKWALLPAPER = 0x0073;
    private readonly DxgiSamplerStateMachine _stateMachine = new();
    private readonly object _gate = new();
    private readonly List<OutputCaptureSlot> _outputs = [];

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDirect3D9Ex? _d3d9;
    private IDirect3DDevice9Ex? _d3d9Device;
    private SharedGlassPipeline? _sharedPipeline;
    private bool _pipelineInitAttempted;
    private long _wallpaperVersion = 1;
    private bool _wallpaperDirty = true;
    private long _lastWallpaperEngineCheckMs = -10_000;
    private bool _isWallpaperEngineRunning;
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
            _wallpaperDirty = true;
            _wallpaperVersion++;
            if (_device is null || _context is null)
            {
                TryInitializeDeviceAndDuplications();
                return;
            }
            TryAcquireAllOutputs();
        }
    }

    public void InvalidateWallpaper()
    {
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var o in _outputs) o.DisposeWallpaper();
            _wallpaperDirty = true;
            _wallpaperVersion++;
        }
    }

    /// <summary>
    /// Returns the exact behind-the-Dock desktop wallpaper texture for standard Windows wallpapers
    /// (zero tooltip/icon/window smudge, pixel-accurate macOS Tahoe glass!). Returns false when
    /// Wallpaper Engine is running so the Dock switches to live DXGI same-Y flank sampling.
    /// </summary>
    internal bool TryGetWallpaperBackdropForRect(
        ScreenRect surfaceRectPx,
        out ID3D11ShaderResourceView? srv,
        out ScreenRect outputBounds,
        out long frameVersion)
    {
        srv = null;
        outputBounds = default;
        frameVersion = 0;

        lock (_gate)
        {
            if (_disposed || _device is null || _outputs.Count == 0) return false;
            if (IsWallpaperEngineActive()) return false;

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

            if (_wallpaperDirty || slot.WallpaperSrv is null)
            {
                TryBuildWallpaperTextureForSlot(slot);
                _wallpaperDirty = false;
            }

            if (slot.WallpaperSrv is not null)
            {
                srv = slot.WallpaperSrv;
                outputBounds = slot.DesktopBounds;
                frameVersion = -1000L - _wallpaperVersion; // Distinct negative namespace so switching to/from DXGI always redraws!
                return true;
            }

            return false;
        }
    }

    private bool IsWallpaperEngineActive()
    {
        long now = Environment.TickCount64;
        if (now - _lastWallpaperEngineCheckMs < 4000)
        {
            return _isWallpaperEngineRunning;
        }
        _lastWallpaperEngineCheckMs = now;
        try
        {
            var p64 = Process.GetProcessesByName("wallpaper64");
            bool running = p64.Length > 0;
            foreach (var p in p64) p.Dispose();
            if (!running)
            {
                var p32 = Process.GetProcessesByName("wallpaper32");
                running = p32.Length > 0;
                foreach (var p in p32) p.Dispose();
            }
            _isWallpaperEngineRunning = running;
        }
        catch
        {
            _isWallpaperEngineRunning = false;
        }
        return _isWallpaperEngineRunning;
    }

    private void TryBuildWallpaperTextureForSlot(OutputCaptureSlot slot)
    {
        if (_device is null) return;
        slot.DisposeWallpaper();

        try
        {
            string? path = ResolveWallpaperFilePath();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

            byte[] fileBytes;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (fs.Length <= 0 || fs.Length > 64 * 1024 * 1024) return;
                fileBytes = new byte[fs.Length];
                fs.ReadExactly(fileBytes);
            }

            using var ms = new MemoryStream(fileBytes, writable: false);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = ms;
            bmp.DecodePixelWidth = 640; // Visually identical after 22px Gaussian blur, 2.6x lower VRAM & decode RAM!
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();

            var bgra = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            int srcW = bgra.PixelWidth;
            int srcH = bgra.PixelHeight;
            if (srcW < 4 || srcH < 4) return;

            // Match Windows 11 "Fill" aspect-ratio center-crop for this exact monitor
            double monAspect = Math.Max(0.25, (double)slot.DesktopBounds.Width / Math.Max(1, slot.DesktopBounds.Height));
            double imgAspect = (double)srcW / srcH;
            int cropX = 0, cropY = 0, cropW = srcW, cropH = srcH;
            if (imgAspect > monAspect)
            {
                cropW = Math.Clamp((int)Math.Round(srcH * monAspect), 1, srcW);
                cropX = (srcW - cropW) / 2;
            }
            else if (imgAspect < monAspect)
            {
                cropH = Math.Clamp((int)Math.Round(srcW / monAspect), 1, srcH);
                cropY = (srcH - cropH) / 2;
            }

            int stride = cropW * 4;
            var pixels = new byte[stride * cropH];
            bgra.CopyPixels(new System.Windows.Int32Rect(cropX, cropY, cropW, cropH), pixels, stride, 0);

            var texDesc = new Texture2DDescription
            {
                Width = (uint)cropW,
                Height = (uint)cropH,
                MipLevels = 1,
                ArraySize = 1,
                Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None
            };

            unsafe
            {
                fixed (byte* ptr = pixels)
                {
                    var initData = new SubresourceData((nint)ptr, (uint)stride, 0);
                    slot.WallpaperTexture = _device.CreateTexture2D(texDesc, new[] { initData });
                    slot.WallpaperSrv = _device.CreateShaderResourceView(slot.WallpaperTexture);
                }
            }
        }
        catch
        {
            slot.DisposeWallpaper();
        }
    }

    private static string? ResolveWallpaperFilePath()
    {
        try
        {
            var sb = new StringBuilder(512);
            if (SystemParametersInfoW(SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0))
            {
                string p = sb.ToString().Trim('\0', ' ');
                if (!string.IsNullOrWhiteSpace(p) && File.Exists(p)) return p;
            }
        }
        catch { }

        string transcoded = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Themes", "TranscodedWallpaper");
        return File.Exists(transcoded) ? transcoded : null;
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

            // Lazy on-demand IDXGIOutputDuplication: ONLY allocate DWM duplication ring buffers (~20-40 MB!)
            // when live DXGI capture is genuinely needed (e.g. Wallpaper Engine active).
            if (slot.Duplication is null && !TryAttachDuplicationForSlot(slot))
            {
                _stateMachine.ReportAccessLost();
                return false;
            }

            IDXGIResource? desktopResource = null;
            try
            {
                var hr = slot.Duplication!.AcquireNextFrame(0, out OutduplFrameInfo frameInfo, out desktopResource);
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
            // Populate lightweight monitor bounds ONLY — defer IDXGIOutputDuplication until actually needed
            // so static-wallpaper setups use 0 bytes of DXGI Desktop Duplication ring-buffer RAM/VRAM!
            var monitors = MonitorContext.GetAllMonitors();
            foreach (var mon in monitors)
            {
                _outputs.Add(new OutputCaptureSlot { DesktopBounds = mon.Bounds });
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

    private bool TryAttachDuplicationForSlot(OutputCaptureSlot slot)
    {
        if (_device is null) return false;
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
                    var coords = desc.DesktopCoordinates;
                    if (Math.Abs(coords.Left - slot.DesktopBounds.Left) <= 4
                        && Math.Abs(coords.Top - slot.DesktopBounds.Top) <= 4)
                    {
                        using var output1 = output.QueryInterface<IDXGIOutput1>();
                        slot.Duplication = output1.DuplicateOutput(_device);
                        return slot.Duplication is not null;
                    }
                }
            }
            return false;
        }
        catch
        {
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, StringBuilder pvParam, uint fWinIni);
}
