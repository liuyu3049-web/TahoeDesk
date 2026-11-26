Texture2D SourceTex : register(t0);
SamplerState LinearClamp : register(s0);

cbuffer GlassConstants : register(b0)
{
    float4 SurfaceRectNorm;   // x=left/dw, y=top/dh, z=width/dw, w=height/dh
    float2 PixelSize;         // surface pixel width, height
    float2 DesktopSize;       // desktop output pixel width, height
    float CornerRadius;       // continuous squircle radius in px
    float ShadowPadding;      // transparent outer margin in px for soft drop shadow
    float RefractionBand;     // edge optical refraction thickness in px
    float RefractionStrength; // subtle UV offset magnitude
    float BlurRadius;         // Gaussian spread in px
    float TintAlpha;          // glass tint opacity
    float SpecularStrength;   // top-left rim highlight intensity
    float IsDarkMode;         // 1.0 = dark, 0.0 = light
    float ActiveBoxWidthPx;   // >0 = GPU-animated capsule width inside fixed full-width surface (zero resize!)
    float SampleMode;         // 0 = direct behind, 1 = reflect strip above (Dock), 2 = reflect strip below (MenuBar)
    float2 _Pad;
};

struct VSOut
{
    float4 Pos : SV_Position;
    float2 UV  : TEXCOORD0;
};

VSOut VSMain(uint id : SV_VertexID)
{
    VSOut o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.UV = uv;
    o.Pos = float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    return o;
}

// 9-tap hardware-bilinear Gaussian offsets & weights (4x faster than 15-tap, zero banding)
static const float BlurOffsets[5] = { 0.0, 1.384615, 3.230769, 5.153846, 7.0 };
static const float BlurWeights[5] = { 0.204164, 0.304005, 0.093913, 0.033800, 0.011000 };

// Pass 1 (Half-Resolution): Horizontal Gaussian blur from desktop/wallpaper texture
float4 PSBlurH(VSOut inp) : SV_Target
{
    float2 desktopUV = SurfaceRectNorm.xy + inp.UV * SurfaceRectNorm.zw;
    float scaleX = (BlurRadius / 7.0) / max(DesktopSize.x, 1.0);

    // SampleMode == 1 (Live Wallpaper Engine flank blend): sample clean live DXGI wallpaper flanks
    // to the LEFT and RIGHT of the Dock on the EXACT same Y-band — never above the Dock,
    // so tooltips, magnified icons, and windows above the Dock NEVER smudge into the Dock glass!
    if (SampleMode > 0.5)
    {
        float marginX = 6.0 / max(DesktopSize.x, 1.0);
        float leftX = saturate(SurfaceRectNorm.x - marginX);
        float rightX = saturate(SurfaceRectNorm.x + SurfaceRectNorm.z + marginX);
        float y = saturate(desktopUV.y);
        float3 colL = SourceTex.Sample(LinearClamp, float2(leftX, y)).rgb;
        float3 colR = SourceTex.Sample(LinearClamp, float2(rightX, y)).rgb;
        float3 colL2 = SourceTex.Sample(LinearClamp, float2(saturate(leftX - 0.03), y)).rgb;
        float3 colR2 = SourceTex.Sample(LinearClamp, float2(saturate(rightX + 0.03), y)).rgb;
        float t = smoothstep(0.0, 1.0, inp.UV.x);
        float3 blended = lerp(lerp(colL2, colL, 0.65), lerp(colR, colR2, 0.35), t);
        return float4(blended, 1.0);
    }

    float3 sum = SourceTex.Sample(LinearClamp, saturate(desktopUV)).rgb * BlurWeights[0];
    float weightSum = BlurWeights[0];

    [unroll]
    for (int i = 1; i < 5; i++)
    {
        float w = BlurWeights[i];
        float2 off = float2(BlurOffsets[i] * scaleX, 0.0);
        sum += SourceTex.Sample(LinearClamp, saturate(desktopUV + off)).rgb * w;
        sum += SourceTex.Sample(LinearClamp, saturate(desktopUV - off)).rgb * w;
        weightSum += 2.0 * w;
    }

    return float4(sum / weightSum, 1.0);
}

// Continuous-curvature superellipse-blended rounded box SDF (Apple Squircle)
float sdSquircle(float2 p, float2 halfSize, float radius)
{
    float r = min(radius, min(halfSize.x, halfSize.y));
    if (r <= 0.5)
    {
        float2 q0 = abs(p) - halfSize;
        return length(max(q0, 0.0)) + min(max(q0.x, q0.y), 0.0);
    }
    float2 q = abs(p) - (halfSize - r);
    float2 c = max(q, 0.0);
    float superDist = pow(pow(c.x, 2.4) + pow(c.y, 2.4), 1.0 / 2.4);
    return superDist + min(max(q.x, q.y), 0.0) - r;
}

float3 SampleVerticalGaussian(float2 localUV, float scaleY)
{
    float3 sum = SourceTex.Sample(LinearClamp, saturate(localUV)).rgb * BlurWeights[0];
    float weightSum = BlurWeights[0];

    [unroll]
    for (int i = 1; i < 5; i++)
    {
        float w = BlurWeights[i];
        float2 off = float2(0.0, BlurOffsets[i] * scaleY);
        sum += SourceTex.Sample(LinearClamp, saturate(localUV + off)).rgb * w;
        sum += SourceTex.Sample(LinearClamp, saturate(localUV - off)).rgb * w;
        weightSum += 2.0 * w;
    }

    return sum / weightSum;
}

// Pass 2 (Full-Resolution): Vertical Gaussian blur + Squircle SDF + clean Liquid Glass composite
float4 PSComposite(VSOut inp) : SV_Target
{
    float2 px = inp.UV * PixelSize;
    float2 center = PixelSize * 0.5;
    float scaleY = (BlurRadius / 7.0) / max(PixelSize.y, 1.0);

    // Fast flat blurred-backdrop mode (ShadowPadding < 0): outputs full-rect blurred & tinted glass
    // so WPF can clip/animate the Dock Squircle capsule at 120fps with ZERO D3D11 redraws during hover!
    if (ShadowPadding < -0.5)
    {
        float3 blurredFlat = SampleVerticalGaussian(inp.UV, scaleY);
        float lumaFlat = dot(blurredFlat, float3(0.299, 0.587, 0.114));
        blurredFlat = lerp(float3(lumaFlat, lumaFlat, lumaFlat), blurredFlat, 1.14);
        float3 tintFlat = IsDarkMode > 0.5 ? float3(0.11, 0.12, 0.15) : float3(0.97, 0.98, 1.00);
        float3 colFlat = lerp(blurredFlat, tintFlat, TintAlpha);
        return float4(saturate(colFlat), 1.0);
    }

    float halfW = ActiveBoxWidthPx > 1.0
        ? min(ActiveBoxWidthPx * 0.5, max(PixelSize.x * 0.5 - ShadowPadding, 1.0))
        : max(PixelSize.x * 0.5 - ShadowPadding, 1.0);
    float halfH = max(PixelSize.y * 0.5 - ShadowPadding, 1.0);
    float2 halfBox = float2(halfW, halfH);
    float2 p = px - center;

    // Early out completely outside shadow region
    if (abs(p.x) > halfW + ShadowPadding + 2.0 || abs(p.y) > halfH + ShadowPadding + 2.0)
    {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    float d = sdSquircle(p, halfBox, CornerRadius);
    float coverage = 1.0 - smoothstep(-0.75, 0.75, d);

    float shadowAlpha = 0.0;
    if (ShadowPadding > 0.5)
    {
        float shadowOffset = min(3.5, ShadowPadding * 0.25);
        float shadowD = sdSquircle(p - float2(0.0, shadowOffset), halfBox, CornerRadius);
        float maxShadowDist = max(ShadowPadding - shadowOffset - 1.0, 1.0);
        float edgeFade = saturate(1.0 - max(shadowD, 0.0) / maxShadowDist);
        shadowAlpha = (edgeFade * edgeFade * edgeFade) * (IsDarkMode > 0.5 ? 0.32 : 0.18) * (1.0 - coverage);
    }

    if (coverage <= 0.001)
    {
        return float4(0.0, 0.0, 0.0, shadowAlpha);
    }

    float2 sampleUV = inp.UV;

    // Subtle physical bevel refraction along rounded glass rim (skipped on flat top menu bar)
    float2 grad = float2(0.0, -1.0);
    float edgeDist = max(-d, 0.0);
    if (CornerRadius > 0.5)
    {
        float2 eps = float2(1.0, 0.0);
        grad = normalize(float2(
            sdSquircle(p + eps.xy, halfBox, CornerRadius) - sdSquircle(p - eps.xy, halfBox, CornerRadius),
            sdSquircle(p + eps.yx, halfBox, CornerRadius) - sdSquircle(p - eps.yx, halfBox, CornerRadius)
        ) + 1e-5);

        float edgeFactor = 1.0 - saturate(edgeDist / max(RefractionBand, 1.0));
        float lensCurve = edgeFactor * edgeFactor * (3.0 - 2.0 * edgeFactor);
        sampleUV += -grad * lensCurve * (RefractionStrength * DesktopSize / max(PixelSize, 1.0));
    }

    float3 blurred = SampleVerticalGaussian(sampleUV, scaleY);

    float luma = dot(blurred, float3(0.299, 0.587, 0.114));
    blurred = lerp(float3(luma, luma, luma), blurred, 1.12);

    float3 tintCol = IsDarkMode > 0.5 ? float3(0.11, 0.12, 0.15) : float3(0.97, 0.98, 1.00);
    float3 glassCol = lerp(blurred, tintCol, TintAlpha);

    if (CornerRadius > 0.5)
    {
        float rimMask = smoothstep(1.8, 0.0, edgeDist);
        float lightDirDot = saturate(dot(-grad, normalize(float2(-0.4, -0.92))));
        float bottomBounce = saturate(dot(-grad, normalize(float2(0.2, 0.98)))) * 0.28;
        float rimGlow = rimMask * (0.24 + 0.76 * lightDirDot + bottomBounce) * SpecularStrength;
        glassCol += float3(1.0, 1.0, 1.0) * rimGlow;

        float topSheen = (1.0 - saturate((p.y + halfBox.y) / max(halfBox.y * 2.0, 1.0))) * 0.05 * SpecularStrength;
        glassCol += float3(1.0, 1.0, 1.0) * topSheen;
    }
    else
    {
        // Flush flat top menu bar: crisp 1px subtle bottom hairline divider flush with maximized windows
        if (px.y >= PixelSize.y - 1.2)
        {
            glassCol = lerp(glassCol, IsDarkMode > 0.5 ? float3(1.0, 1.0, 1.0) : float3(0.0, 0.0, 0.0), 0.14);
        }
    }

    float finalAlpha = saturate(coverage + shadowAlpha * (1.0 - coverage));
    float3 premulColor = saturate(glassCol) * coverage;
    return float4(premulColor, finalAlpha);
}
