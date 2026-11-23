using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
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
    private ID3D11PixelShader? _psBlurH;
    private ID3D11PixelShader? _psComposite;
    private ID3D11SamplerState? _linearSampler;
    private ID3D11Buffer? _constantBuffer;
    private ID3D11Query? _gpuSyncQuery;

    private ID3D11Texture2D? _tempBlurTex;
    private ID3D11RenderTargetView? _tempBlurRtv;
    private ID3D11ShaderResourceView? _tempBlurSrv;

    private ID3D11Texture2D? _renderTarget;
    private ID3D11RenderTargetView? _rtv;
    private IDirect3DTexture9? _sharedD3d9Tex;
    private IDirect3DSurface9? _sharedD3d9Surface;

    private int _rtWidth;
    private int _rtHeight;
    private long _lastRenderedFrameVersion = -1;
    private ScreenRect _lastRenderedRect;
    private GlassRenderParams _lastRenderedParams;
    private float _lastShadowPad;
    private bool _initialized;
    private bool _disposed;

    public LiquidGlassRenderer(DxgiBackdropSampler sampler)
    {
        _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
    }

    public nint SharedD3D9SurfacePtr => _sharedD3d9Surface?.NativePointer ?? 0;

    public bool RenderSurface(
        ScreenRect surfaceRectPx,
        GlassRenderParams glassParams,
        float shadowPaddingPx,
        Action beforeRecreateTargets,
        out bool frameUpdated)
    {
        frameUpdated = false;
        if (_disposed || surfaceRectPx.Width <= 4 || surfaceRectPx.Height <= 4) return false;

        if (!_sampler.TryGetOutputForRect(surfaceRectPx, out var srv, out var outputBounds, out long frameVersion)
            || srv is null || outputBounds.Width <= 0 || outputBounds.Height <= 0)
        {
            return false;
        }

        // Skip redundant GPU re-draw when desktop frame + surface rect + glass params are unchanged!
        if (_sharedD3d9Surface is not null
            && frameVersion == _lastRenderedFrameVersion
            && surfaceRectPx == _lastRenderedRect
            && glassParams == _lastRenderedParams
            && Math.Abs(shadowPaddingPx - _lastShadowPad) < 0.1f)
        {
            return true;
        }

        var device = _sampler.Device;
        var ctx = _sampler.Context;
        var d3d9Device = _sampler.D3D9Device;
        if (device is null || ctx is null || d3d9Device is null)
        {
            return false;
        }

        if (!_initialized && !TryInitializePipeline(device))
        {
            return false;
        }

        if (!EnsureRenderTargets(device, d3d9Device, surfaceRectPx.Width, surfaceRectPx.Height, beforeRecreateTargets))
        {
            return false;
        }

        try
        {
            float dw = outputBounds.Width;
            float dh = outputBounds.Height;
            var cb = new GlassConstantBuffer
            {
                SurfaceRectNorm = new Vector4(
                    (surfaceRectPx.Left - outputBounds.Left) / dw,
                    (surfaceRectPx.Top - outputBounds.Top) / dh,
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

            var vp = new Vortice.Mathematics.Viewport(0, 0, surfaceRectPx.Width, surfaceRectPx.Height);
            ctx.UpdateSubresource(in cb, _constantBuffer!);
            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            ctx.IASetInputLayout(null);
            ctx.VSSetShader(_vs);
            ctx.PSSetConstantBuffer(0, _constantBuffer);
            ctx.PSSetSampler(0, _linearSampler);
            ctx.RSSetViewport(vp);

            // Pass 1: Horizontal 15-tap Gaussian blur from output texture -> _tempBlurTex
            ctx.OMSetRenderTargets(_tempBlurRtv!);
            ctx.PSSetShader(_psBlurH);
            ctx.PSSetShaderResource(0, srv);
            ctx.Draw(3, 0);
            ctx.PSSetShaderResource(0, null!);

            // Pass 2: Vertical 15-tap Gaussian blur + Squircle SDF + clean Liquid Glass -> _renderTarget
            ctx.OMSetRenderTargets(_rtv!);
            ctx.ClearRenderTargetView(_rtv!, new Color4(0, 0, 0, 0));
            ctx.PSSetShader(_psComposite);
            ctx.PSSetShaderResource(0, _tempBlurSrv!);
            ctx.Draw(3, 0);
            ctx.PSSetShaderResource(0, null!);

            // CRITICAL: Wait for GPU completion via D3D11 Event Query before WPF D3D9Ex reads the shared texture!
            // Eliminates shimmering tiled checkerboard corruption ("格子花屏") when multiple glass windows render concurrently.
            if (_gpuSyncQuery is not null)
            {
                ctx.End(_gpuSyncQuery);
                ctx.Flush();
                WaitForGpuSync(ctx, _gpuSyncQuery);
            }
            else
            {
                ctx.Flush();
            }

            _lastRenderedFrameVersion = frameVersion;
            _lastRenderedRect = surfaceRectPx;
            _lastRenderedParams = glassParams;
            _lastShadowPad = shadowPaddingPx;
            frameUpdated = true;
            return SharedD3D9SurfacePtr != 0;
        }
        catch
        {
            return false;
        }
    }

    private static void WaitForGpuSync(ID3D11DeviceContext ctx, ID3D11Query query)
    {
        var sw = Stopwatch.StartNew();
        while (!ctx.GetData<int>(query, out _))
        {
            if (sw.ElapsedMilliseconds >= 4) break;
            Thread.SpinWait(64);
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
            var psBlurBlob = Compiler.Compile(hlsl, "PSBlurH", shaderPath, "ps_5_0");
            var psCompBlob = Compiler.Compile(hlsl, "PSComposite", shaderPath, "ps_5_0");

            _vs = device.CreateVertexShader(vsBlob.Span);
            _psBlurH = device.CreatePixelShader(psBlurBlob.Span);
            _psComposite = device.CreatePixelShader(psCompBlob.Span);

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

            _gpuSyncQuery = device.CreateQuery(new QueryDescription(Vortice.Direct3D11.QueryType.Event));

            _initialized = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool EnsureRenderTargets(
        ID3D11Device device,
        IDirect3DDevice9Ex d3d9Device,
        int width,
        int height,
        Action beforeRecreateTargets)
    {
        if (_renderTarget is not null && _tempBlurTex is not null
            && _rtWidth == width && _rtHeight == height && _sharedD3d9Surface is not null)
        {
            return true;
        }

        beforeRecreateTargets();
        DisposeTargets();

        try
        {
            var tempDesc = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                MiscFlags = ResourceOptionFlags.None
            };
            _tempBlurTex = device.CreateTexture2D(tempDesc);
            _tempBlurRtv = device.CreateRenderTargetView(_tempBlurTex);
            _tempBlurSrv = device.CreateShaderResourceView(_tempBlurTex);

            var outDesc = tempDesc with { MiscFlags = ResourceOptionFlags.Shared };
            _renderTarget = device.CreateTexture2D(outDesc);
            _rtv = device.CreateRenderTargetView(_renderTarget);

            using var dxgiRes = _renderTarget.QueryInterface<IDXGIResource>();
            nint sharedHandle = dxgiRes.SharedHandle;
            if (sharedHandle == 0) return false;

            _sharedD3d9Tex = d3d9Device.CreateTexture(
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
            _lastRenderedFrameVersion = -1;
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
        _tempBlurSrv?.Dispose();
        _tempBlurRtv?.Dispose();
        _tempBlurTex?.Dispose();

        _sharedD3d9Surface = null;
        _sharedD3d9Tex = null;
        _rtv = null;
        _renderTarget = null;
        _tempBlurSrv = null;
        _tempBlurRtv = null;
        _tempBlurTex = null;
        _rtWidth = 0;
        _rtHeight = 0;
        _lastRenderedFrameVersion = -1;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeTargets();
        _gpuSyncQuery?.Dispose();
        _constantBuffer?.Dispose();
        _linearSampler?.Dispose();
        _psComposite?.Dispose();
        _psBlurH?.Dispose();
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
}
