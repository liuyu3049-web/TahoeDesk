Texture2D SourceTex : register(t0);
SamplerState LinearClamp : register(s0);

cbuffer GlassConstants : register(b0)
{
    float4 SurfaceRectNorm;   // x=left/dw, y=top/dh, z=width/dw, w=height/dh
    float2 PixelSize;         // surface pixel width, height
    float2 DesktopSize;       // desktop pixel width, height
    float CornerRadius;       // continuous squircle radius in px
    float ShadowPadding;      // transparent outer margin in px for soft drop shadow
    float RefractionBand;     // edge optical refraction thickness in px
    float RefractionStrength; // subtle UV offset magnitude
    float BlurRadius;         // Gaussian sigma/spread in px
    float TintAlpha;          // glass tint opacity
    float SpecularStrength;   // top-left rim highlight intensity
    float IsDarkMode;         // 1.0 = dark, 0.0 = light
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

// 15-tap separable Gaussian weights (sigma ~ 3.5, normalized sum = 1.0)
static const float GaussWeights[8] =
{
    0.132980,
    0.125794,
    0.106483,
    0.080657,
    0.054670,
    0.033159,
    0.017997,
    0.008741
};

// Pass 1: Horizontal separable Gaussian blur directly from desktop capture texture
float4 PSBlurH(VSOut inp) : SV_Target
{
    float2 desktopUV = SurfaceRectNorm.xy + inp.UV * SurfaceRectNorm.zw;
    float stepX = (BlurRadius / 7.0) / max(DesktopSize.x, 1.0);

    float3 sum = SourceTex.Sample(LinearClamp, saturate(desktopUV)).rgb * GaussWeights[0];
    float weightSum = GaussWeights[0];

    [unroll]
    for (int i = 1; i < 8; i++)
    {
        float w = GaussWeights[i];
        float2 off = float2(stepX * i, 0.0);
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
    float2 q = abs(p) - (halfSize - r);
    float2 c = max(q, 0.0);
    float superDist = pow(pow(c.x, 2.4) + pow(c.y, 2.4), 1.0 / 2.4);
    return superDist + min(max(q.x, q.y), 0.0) - r;
}

float3 SampleVerticalGaussian(float2 localUV, float stepY)
{
    float3 sum = SourceTex.Sample(LinearClamp, saturate(localUV)).rgb * GaussWeights[0];
    float weightSum = GaussWeights[0];

    [unroll]
    for (int i = 1; i < 8; i++)
    {
        float w = GaussWeights[i];
        float2 off = float2(0.0, stepY * i);
        sum += SourceTex.Sample(LinearClamp, saturate(localUV + off)).rgb * w;
        sum += SourceTex.Sample(LinearClamp, saturate(localUV - off)).rgb * w;
        weightSum += 2.0 * w;
    }

    return sum / weightSum;
}

// Pass 2: Vertical separable Gaussian blur + Squircle SDF + clean Liquid Glass composite
float4 PSComposite(VSOut inp) : SV_Target
{
    float2 px = inp.UV * PixelSize;
    float2 center = PixelSize * 0.5;
    float2 halfBox = max(PixelSize * 0.5 - ShadowPadding, 1.0);
    float2 p = px - center;

    float d = sdSquircle(p, halfBox, CornerRadius);
    float coverage = 1.0 - smoothstep(-0.75, 0.75, d);

    // Soft rounded drop shadow that strictly decays to 0.0 BEFORE reaching ShadowPadding (zero rectangular cutoff!)
    float shadowOffset = min(3.5, ShadowPadding * 0.25);
    float shadowD = sdSquircle(p - float2(0.0, shadowOffset), halfBox, CornerRadius);
    float maxShadowDist = max(ShadowPadding - shadowOffset - 1.0, 1.0);
    float edgeFade = saturate(1.0 - max(shadowD, 0.0) / maxShadowDist);
    float shadowAlpha = (edgeFade * edgeFade * edgeFade) * (IsDarkMode > 0.5 ? 0.34 : 0.20) * (1.0 - coverage);

    if (coverage <= 0.001)
    {
        return float4(0.0, 0.0, 0.0, shadowAlpha);
    }

    // Smooth inward normal for subtle physical bevel refraction along curved glass rim
    float2 eps = float2(1.0, 0.0);
    float2 grad = normalize(float2(
        sdSquircle(p + eps.xy, halfBox, CornerRadius) - sdSquircle(p - eps.xy, halfBox, CornerRadius),
        sdSquircle(p + eps.yx, halfBox, CornerRadius) - sdSquircle(p - eps.yx, halfBox, CornerRadius)
    ) + 1e-5);

    float edgeDist = max(-d, 0.0);
    float edgeFactor = 1.0 - saturate(edgeDist / max(RefractionBand, 1.0));
    float lensCurve = edgeFactor * edgeFactor * (3.0 - 2.0 * edgeFactor);

    // Subtle refraction inside local blurred texture (no rainbow chromatic stripes!)
    float2 uvOffset = -grad * lensCurve * (RefractionStrength * DesktopSize / max(PixelSize, 1.0));
    float stepY = (BlurRadius / 7.0) / max(PixelSize.y, 1.0);
    float3 blurred = SampleVerticalGaussian(inp.UV + uvOffset, stepY);

    // Gentle vibrancy lift so blurred colors stay clean and luminous
    float luma = dot(blurred, float3(0.299, 0.587, 0.114));
    blurred = lerp(float3(luma, luma, luma), blurred, 1.12);

    // Clean adaptive macOS Liquid Glass tint
    float3 tintCol = IsDarkMode > 0.5 ? float3(0.11, 0.12, 0.15) : float3(0.97, 0.98, 1.00);
    float3 glassCol = lerp(blurred, tintCol, TintAlpha);

    // Delicate top-left specular rim highlight (1.5px crisp glass edge)
    float rimMask = smoothstep(1.8, 0.0, edgeDist);
    float lightDirDot = saturate(dot(-grad, normalize(float2(-0.4, -0.92))));
    float bottomBounce = saturate(dot(-grad, normalize(float2(0.2, 0.98)))) * 0.28;
    float rimGlow = rimMask * (0.24 + 0.76 * lightDirDot + bottomBounce) * SpecularStrength;
    glassCol += float3(1.0, 1.0, 1.0) * rimGlow;

    // Subtle luminous top sheen
    float topSheen = (1.0 - saturate((p.y + halfBox.y) / max(halfBox.y * 2.0, 1.0))) * 0.05 * SpecularStrength;
    glassCol += float3(1.0, 1.0, 1.0) * topSheen;

    float finalAlpha = saturate(coverage + shadowAlpha * (1.0 - coverage));
    float3 premulColor = saturate(glassCol) * coverage;
    return float4(premulColor, finalAlpha);
}
