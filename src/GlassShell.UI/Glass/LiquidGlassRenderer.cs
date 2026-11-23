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
    private sealed class SharedOutputSlot : IDisposable
    {
        public required ID3D11Texture2D RenderTarget { get; init; }
        public required ID3D11RenderTargetView Rtv { get; init; }
        public required IDirect3DTexture9 SharedD3d9Tex { get; init; }
        public required IDirect3DSurface9 SharedD3d9Surface { get; init; }

        public void Dispose()
        {
            SharedD3d9Surface.Dispose();
            SharedD3d9Tex.Dispose();
            Rtv.Dispose();
            RenderTarget.Dispose();
        }
    }

    private readonly DxgiBackdropSampler _sampler;
    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _psBlurH;
    private ID3D11PixelShader? _psComposite;
    private ID3D11SamplerState? _linearSampler;
    private ID3D11Buffer? _constantBuffer;
    private ID3D11Query? _gpuSyncQuery;

    // Half-resolution Pass 1 intermediate blur texture (4x faster GPU fill rate)
    private ID3D11Texture2D? _tempBlurTex;
    private ID3D11RenderTargetView? _tempBlurRtv;
    private ID3D11ShaderResourceView? _tempBlurSrv;
    private int _tempW;
    private int _tempH;

    // Double-buffered ping-pong D3D11/D3D9Ex shared outputs so WPF milcore never races with active GPU writes
    private readonly SharedOutputSlot?[] _swapSlots = new SharedOutputSlot?[2];
    private int _activeSlotIndex;

    private int _rtWidth;
    private int _rtHeight;
    private long _lastRenderedFrameVersion = -1;
    private ScreenRect _lastRenderedRect;
    private GlassRenderParams _lastRenderedParams;
    private float _lastShadowPad;
    private float _lastActiveBoxWidthPx;
    private bool _initialized;
    private bool _disposed;

    public LiquidGlassRenderer(DxgiBackdropSampler sampler)
    {
        _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
    }

    public nint SharedD3D9SurfacePtr => _swapSlots[_activeSlotIndex]?.SharedD3d9Surface.NativePointer ?? 0;

    public bool RenderSurface(
        ScreenRect surfaceRectPx,
        GlassRenderParams glassParams,
        float shadowPaddingPx,
        float activeBoxWidthPx,
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

        // Skip redundant GPU re-draw when desktop frame + geometry + params are unchanged
        if (_swapSlots[_activeSlotIndex] is not null
            && frameVersion == _lastRenderedFrameVersion
            && surfaceRectPx == _lastRenderedRect
            && glassParams == _lastRenderedParams
            && Math.Abs(shadowPaddingPx - _lastShadowPad) < 0.1f
            && Math.Abs(activeBoxWidthPx - _lastActiveBoxWidthPx) < 0.35f)
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

        // Ping-pong to the back slot not currently being read by WPF milcore
        int writeIdx = 1 - _activeSlotIndex;
        var targetSlot = _swapSlots[writeIdx];
        if (targetSlot is null) return false;

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
                IsDarkMode = glassParams.IsDarkMode ? 1.0f : 0.0f,
                ActiveBoxWidthPx = activeBoxWidthPx
            };

            ctx.UpdateSubresource(in cb, _constantBuffer!);
            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            ctx.IASetInputLayout(null);
            ctx.VSSetShader(_vs);
            ctx.PSSetConstantBuffer(0, _constantBuffer);
            ctx.PSSetSampler(0, _linearSampler);

            // Pass 1 (Half-Res): Horizontal bilinear-Gaussian blur -> _tempBlurTex
            ctx.RSSetViewport(new Vortice.Mathematics.Viewport(0, 0, _tempW, _tempH));
            ctx.OMSetRenderTargets(_tempBlurRtv!);
            ctx.PSSetShader(_psBlurH);
            ctx.PSSetShaderResource(0, srv);
            ctx.Draw(3, 0);
            ctx.PSSetShaderResource(0, null!);

            // Pass 2 (Full-Res): Vertical bilinear-Gaussian blur + Squircle SDF + Liquid Glass -> back swap slot
            ctx.RSSetViewport(new Vortice.Mathematics.Viewport(0, 0, surfaceRectPx.Width, surfaceRectPx.Height));
            ctx.OMSetRenderTargets(targetSlot.Rtv);
            ctx.ClearRenderTargetView(targetSlot.Rtv, new Color4(0, 0, 0, 0));
            ctx.PSSetShader(_psComposite);
            ctx.PSSetShaderResource(0, _tempBlurSrv!);
            ctx.Draw(3, 0);
            ctx.PSSetShaderResource(0, null!);

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

            _activeSlotIndex = writeIdx;
            _lastRenderedFrameVersion = frameVersion;
            _lastRenderedRect = surfaceRectPx;
            _lastRenderedParams = glassParams;
            _lastShadowPad = shadowPaddingPx;
            _lastActiveBoxWidthPx = activeBoxWidthPx;
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
            if (sw.ElapsedMilliseconds >= 2) break;
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
        if (_swapSlots[0] is not null && _swapSlots[1] is not null && _tempBlurTex is not null
            && _rtWidth == width && _rtHeight == height)
        {
            return true;
        }

        beforeRecreateTargets();
        DisposeTargets();

        try
        {
            _tempW = Math.Max(2, width / 2);
            _tempH = Math.Max(2, height / 2);

            var tempDesc = new Texture2DDescription
            {
                Width = (uint)_tempW,
                Height = (uint)_tempH,
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

            var outDesc = new Texture2DDescription
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

            for (int i = 0; i < 2; i++)
            {
                var rt = device.CreateTexture2D(outDesc);
                var rtv = device.CreateRenderTargetView(rt);
                using var dxgiRes = rt.QueryInterface<IDXGIResource>();
                nint sharedHandle = dxgiRes.SharedHandle;
                if (sharedHandle == 0)
                {
                    rtv.Dispose();
                    rt.Dispose();
                    DisposeTargets();
                    return false;
                }

                var d3d9Tex = d3d9Device.CreateTexture(
                    (uint)width,
                    (uint)height,
                    1,
                    Vortice.Direct3D9.Usage.RenderTarget,
                    Vortice.Direct3D9.Format.A8R8G8B8,
                    Pool.Default,
                    ref sharedHandle);

                var d3d9Surf = d3d9Tex.GetSurfaceLevel(0);
                _swapSlots[i] = new SharedOutputSlot
                {
                    RenderTarget = rt,
                    Rtv = rtv,
                    SharedD3d9Tex = d3d9Tex,
                    SharedD3d9Surface = d3d9Surf
                };
            }

            _rtWidth = width;
            _rtHeight = height;
            _activeSlotIndex = 0;
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
        for (int i = 0; i < _swapSlots.Length; i++)
        {
            _swapSlots[i]?.Dispose();
            _swapSlots[i] = null;
        }

        _tempBlurSrv?.Dispose();
        _tempBlurRtv?.Dispose();
        _tempBlurTex?.Dispose();

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
        public float ActiveBoxWidthPx;
        public Vector3 _Pad;
    }
}
