using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using GlassShell.Core.Glass;
using GlassShell.Core.Shell;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct3D9;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace GlassShell.UI.Glass;

public sealed class LiquidGlassRenderer : IDisposable
{
    private readonly DxgiBackdropSampler _sampler;
    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _ps;
    private ID3D11SamplerState? _linearSampler;
    private ID3D11Buffer? _constantBuffer;

    private IDirect3D9Ex? _d3d9;
    private IDirect3DDevice9Ex? _d3d9Device;
    private ID3D11Texture2D? _renderTarget;
    private ID3D11RenderTargetView? _rtv;
    private IDirect3DTexture9? _sharedD3d9Tex;
    private IDirect3DSurface9? _sharedD3d9Surface;

    private int _rtWidth;
    private int _rtHeight;
    private bool _initialized;
    private bool _disposed;

    public LiquidGlassRenderer(DxgiBackdropSampler sampler)
    {
        _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
    }

    public nint SharedD3D9SurfacePtr => _sharedD3d9Surface?.NativePointer ?? 0;

    public bool RenderSurface(ScreenRect surfaceRectPx, GlassRenderParams glassParams, float shadowPaddingPx)
    {
        if (_disposed || surfaceRectPx.Width <= 4 || surfaceRectPx.Height <= 4) return false;
        if (!_sampler.TryUpdateFrame()) return false;

        var device = _sampler.Device;
        var ctx = _sampler.Context;
        var srv = _sampler.DesktopSrv;
        if (device is null || ctx is null || srv is null || _sampler.DesktopWidth <= 0 || _sampler.DesktopHeight <= 0)
        {
            return false;
        }

        if (!_initialized && !TryInitializePipeline(device))
        {
            return false;
        }

        if (!EnsureRenderTarget(device, surfaceRectPx.Width, surfaceRectPx.Height))
        {
            return false;
        }

        try
        {
            float dw = _sampler.DesktopWidth;
            float dh = _sampler.DesktopHeight;
            var cb = new GlassConstantBuffer
            {
                SurfaceRectNorm = new Vector4(
                    surfaceRectPx.Left / dw,
                    surfaceRectPx.Top / dh,
                    surfaceRectPx.Width / dw,
                    surfaceRectPx.Height / dh),
                PixelSize = new Vector2(surfaceRectPx.Width, surfaceRectPx.Height),
                DesktopSize = new Vector2(dw, dh),
                CornerRadius = glassParams.CornerRadiusPx,
                ShadowPadding = shadowPaddingPx,
                RefractionBand = glassParams.RefractionBandPx,
                RefractionStrength = glassParams.RefractionStrength,
                BlurRadius = glassParams.BlurRadius,
                TintAlpha = glassParams.TintAlpha,
                SpecularStrength = glassParams.SpecularStrength,
                IsDarkMode = glassParams.IsDarkMode ? 1.0f : 0.0f
            };

            ctx.UpdateSubresource(in cb, _constantBuffer!);
            ctx.OMSetRenderTargets(_rtv!);
            ctx.RSSetViewport(new Vortice.Mathematics.Viewport(0, 0, surfaceRectPx.Width, surfaceRectPx.Height));
            ctx.ClearRenderTargetView(_rtv!, new Color4(0, 0, 0, 0));

            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            ctx.IASetInputLayout(null);
            ctx.VSSetShader(_vs);
            ctx.PSSetShader(_ps);
            ctx.PSSetConstantBuffer(0, _constantBuffer);
            ctx.PSSetShaderResource(0, srv);
            ctx.PSSetSampler(0, _linearSampler);

            ctx.Draw(3, 0);
            ctx.PSSetShaderResource(0, null!);
            ctx.Flush();
            return SharedD3D9SurfacePtr != 0;
        }
        catch
        {
            return false;
        }
    }

    private bool TryInitializePipeline(ID3D11Device device)
    {
        try
        {
            string shaderPath = Path.Combine(AppContext.BaseDirectory, "Glass", "Shaders", "LiquidGlass.hlsl");
            if (!File.Exists(shaderPath)) return false;

            string hlsl = File.ReadAllText(shaderPath);
            var vsBlob = Compiler.Compile(hlsl, "VSMain", shaderPath, "vs_5_0");
            var psBlob = Compiler.Compile(hlsl, "PSMain", shaderPath, "ps_5_0");

            _vs = device.CreateVertexShader(vsBlob.Span);
            _ps = device.CreatePixelShader(psBlob.Span);

            _linearSampler = device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp
            });

            _constantBuffer = device.CreateBuffer(new BufferDescription
            {
                ByteWidth = (uint)Marshal.SizeOf<GlassConstantBuffer>(),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ConstantBuffer
            });

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

            _initialized = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool EnsureRenderTarget(ID3D11Device device, int width, int height)
    {
        if (_renderTarget is not null && _rtWidth == width && _rtHeight == height && _sharedD3d9Surface is not null)
        {
            return true;
        }

        DisposeTargets();

        try
        {
            var desc = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                MiscFlags = ResourceOptionFlags.Shared
            };

            _renderTarget = device.CreateTexture2D(desc);
            _rtv = device.CreateRenderTargetView(_renderTarget);

            using var dxgiRes = _renderTarget.QueryInterface<IDXGIResource>();
            nint sharedHandle = dxgiRes.SharedHandle;
            if (sharedHandle == 0 || _d3d9Device is null) return false;

            _sharedD3d9Tex = _d3d9Device.CreateTexture(
                (uint)width,
                (uint)height,
                1,
                Vortice.Direct3D9.Usage.RenderTarget,
                Vortice.Direct3D9.Format.A8R8G8B8,
                Pool.Default,
                ref sharedHandle);

            _sharedD3d9Surface = _sharedD3d9Tex.GetSurfaceLevel(0);
            _rtWidth = width;
            _rtHeight = height;
            return true;
        }
        catch
        {
            DisposeTargets();
            return false;
        }
    }

    private void DisposeTargets()
    {
        _sharedD3d9Surface?.Dispose();
        _sharedD3d9Tex?.Dispose();
        _rtv?.Dispose();
        _renderTarget?.Dispose();
        _sharedD3d9Surface = null;
        _sharedD3d9Tex = null;
        _rtv = null;
        _renderTarget = null;
        _rtWidth = 0;
        _rtHeight = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeTargets();
        _d3d9Device?.Dispose();
        _d3d9?.Dispose();
        _constantBuffer?.Dispose();
        _linearSampler?.Dispose();
        _ps?.Dispose();
        _vs?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GlassConstantBuffer
    {
        public Vector4 SurfaceRectNorm;
        public Vector2 PixelSize;
        public Vector2 DesktopSize;
        public float CornerRadius;
        public float ShadowPadding;
        public float RefractionBand;
        public float RefractionStrength;
        public float BlurRadius;
        public float TintAlpha;
        public float SpecularStrength;
        public float IsDarkMode;
    }

    [DllImport("user32.dll")]
    private static extern nint GetDesktopWindow();
}
