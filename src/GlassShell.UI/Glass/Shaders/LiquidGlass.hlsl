Texture2D DesktopTex : register(t0);
SamplerState LinearClamp : register(s0);

cbuffer GlassConstants : register(b0)
{
    float4 SurfaceRectNorm;   // x=left/dw, y=top/dh, z=width/dw, w=height/dh
    float2 PixelSize;         // surface pixel width, height
    float2 DesktopSize;       // desktop pixel width, height
    float CornerRadius;       // continuous squircle radius in px
    float ShadowPadding;      // transparent outer margin in px for soft drop shadow
    float RefractionBand;     // edge optical refraction thickness in px
    float RefractionStrength; // UV offset magnitude
    float BlurRadius;         // blur spread in px
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

// Continuous-curvature superellipse-blended rounded box SDF (Apple Squircle approximation)
float sdSquircle(float2 p, float2 halfSize, float radius)
{
    float r = min(radius, min(halfSize.x, halfSize.y));
    float2 q = abs(p) - (halfSize - r);
    float2 c = max(q, 0.0);
    // Superellipse exponent ~2.4 yields smoother G2-like transition than circular arc
    float superDist = pow(pow(c.x, 2.4) + pow(c.y, 2.4), 1.0 / 2.4);
    return superDist + min(max(q.x, q.y), 0.0) - r;
}

float3 SampleBlurredDesktop(float2 desktopUV, float2 texelSize, float spread)
{
    // 13-tap Gaussian-weighted ring kernel for smooth creamy Liquid Glass blur
    float3 col = DesktopTex.Sample(LinearClamp, desktopUV).rgb * 0.16;

    float2 o1 = texelSize * spread * 0.55;
    float2 o2 = texelSize * spread * 1.15;

    col += DesktopTex.Sample(LinearClamp, desktopUV + float2( o1.x,  0.0)).rgb * 0.09;
    col += DesktopTex.Sample(LinearClamp, desktopUV + float2(-o1.x,  0.0)).rgb * 0.09;
    col += DesktopTex.Sample(LinearClamp, desktopUV + float2( 0.0,  o1.y)).rgb * 0.09;
    col += DesktopTex.Sample(LinearClamp, desktopUV + float2( 0.0, -o1.y)).rgb * 0.09;

    col += DesktopTex.Sample(LinearClamp, desktopUV + float2( o1.x,  o1.y)).rgb * 0.07;
    col += DesktopTex.Sample(LinearClamp, desktopUV + float2(-o1.x,  o1.y)).rgb * 0.07;
    col += DesktopTex.Sample(LinearClamp, desktopUV + float2( o1.x, -o1.y)).rgb * 0.07;
    col += DesktopTex.Sample(LinearClamp, desktopUV + float2(-o1.x, -o1.y)).rgb * 0.07;

    col += DesktopTex.Sample(LinearClamp, desktopUV + float2( o2.x,  o2.y * 0.5)).rgb * 0.05;
    col += DesktopTex.Sample(LinearClamp, desktopUV + float2(-o2.x,  o2.y * 0.5)).rgb * 0.05;
    col += DesktopTex.Sample(LinearClamp, desktopUV + float2( o2.x * 0.5, -o2.y)).rgb * 0.05;
    col += DesktopTex.Sample(LinearClamp, desktopUV + float2(-o2.x * 0.5, -o2.y)).rgb * 0.05;

    return col;
}

float4 PSMain(VSOut inp) : SV_Target
{
    float2 px = inp.UV * PixelSize;
    float2 center = PixelSize * 0.5;
    float2 halfBox = max(PixelSize * 0.5 - ShadowPadding, 1.0);
    float2 p = px - center;

    float d = sdSquircle(p, halfBox, CornerRadius);

    // Anti-aliased interior coverage
    float coverage = 1.0 - smoothstep(-0.75, 0.75, d);

    // Soft multi-layer ambient shadow outside capsule
    float shadowD = sdSquircle(p - float2(0.0, 4.0), halfBox, CornerRadius);
    float shadowAlpha = exp(-max(shadowD, 0.0) * 0.14) * (IsDarkMode > 0.5 ? 0.38 : 0.24) * (1.0 - coverage);

    if (coverage <= 0.001)
    {
        return float4(0.0, 0.0, 0.0, shadowAlpha);
    }

    // Approximate inward normal for edge optical refraction
    float2 eps = float2(1.0, 0.0);
    float2 grad = normalize(float2(
        sdSquircle(p + eps.xy, halfBox, CornerRadius) - sdSquircle(p - eps.xy, halfBox, CornerRadius),
        sdSquircle(p + eps.yx, halfBox, CornerRadius) - sdSquircle(p - eps.yx, halfBox, CornerRadius)
    ) + 1e-5);

    float edgeDist = max(-d, 0.0);
    float edgeFactor = 1.0 - saturate(edgeDist / max(RefractionBand, 1.0));
    float lensCurve = edgeFactor * edgeFactor * (3.0 - 2.0 * edgeFactor);

    float2 baseDesktopUV = SurfaceRectNorm.xy + inp.UV * SurfaceRectNorm.zw;
    float2 refractOffset = -grad * lensCurve * RefractionStrength;
    float2 texel = 1.0 / max(DesktopSize, 1.0);

    // Subtle chromatic dispersion along curved glass rim
    float3 refracted;
    refracted.r = SampleBlurredDesktop(saturate(baseDesktopUV + refractOffset * 1.12), texel, BlurRadius).r;
    refracted.g = SampleBlurredDesktop(saturate(baseDesktopUV + refractOffset),        texel, BlurRadius).g;
    refracted.b = SampleBlurredDesktop(saturate(baseDesktopUV + refractOffset * 0.88), texel, BlurRadius).b;

    // Vibrancy boost so refracted wallpaper colors feel luminous like macOS Tahoe
    float luma = dot(refracted, float3(0.299, 0.587, 0.114));
    refracted = lerp(float3(luma, luma, luma), refracted, 1.22);

    // Adaptive glass tint
    float3 tintCol = IsDarkMode > 0.5 ? float3(0.11, 0.12, 0.15) : float3(0.96, 0.97, 1.00);
    float3 glassCol = lerp(refracted, tintCol, TintAlpha);

    // Top-left directional specular rim highlight (Liquid Glass catchlight)
    float rimMask = smoothstep(2.4, 0.0, edgeDist);
    float lightDirDot = saturate(dot(-grad, normalize(float2(-0.45, -0.89))));
    float bottomBounce = saturate(dot(-grad, normalize(float2(0.25, 0.96)))) * 0.35;
    float rimGlow = rimMask * (0.28 + 0.72 * lightDirDot + bottomBounce) * SpecularStrength;
    glassCol += float3(1.0, 1.0, 1.0) * rimGlow;

    // Subtle top sheen gradient across capsule interior
    float topSheen = (1.0 - saturate((p.y + halfBox.y) / max(halfBox.y * 2.0, 1.0))) * 0.08 * SpecularStrength;
    glassCol += float3(1.0, 1.0, 1.0) * topSheen;

    float finalAlpha = saturate(coverage + shadowAlpha * (1.0 - coverage));
    float3 premulColor = glassCol * coverage;
    return float4(premulColor, finalAlpha);
}
