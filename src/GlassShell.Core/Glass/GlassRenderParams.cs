using GlassShell.Core.Config;

namespace GlassShell.Core.Glass;

public readonly record struct GlassRenderParams(
    float CornerRadiusPx,
    float RefractionBandPx,
    float RefractionStrength,
    float BlurRadius,
    float TintAlpha,
    float SpecularStrength,
    bool IsDarkMode)
{
    public static GlassRenderParams FromPreset(GlassOpacityPreset preset, bool isDarkMode, float cornerRadiusPx)
    {
        return preset switch
        {
            GlassOpacityPreset.Clear => new GlassRenderParams(
                CornerRadiusPx: cornerRadiusPx,
                RefractionBandPx: 14f,
                RefractionStrength: 0.018f,
                BlurRadius: 10f,
                TintAlpha: isDarkMode ? 0.16f : 0.22f,
                SpecularStrength: isDarkMode ? 0.42f : 0.65f,
                IsDarkMode: isDarkMode),

            GlassOpacityPreset.Balanced => new GlassRenderParams(
                CornerRadiusPx: cornerRadiusPx,
                RefractionBandPx: 12f,
                RefractionStrength: 0.014f,
                BlurRadius: 14f,
                TintAlpha: isDarkMode ? 0.30f : 0.38f,
                SpecularStrength: isDarkMode ? 0.38f : 0.55f,
                IsDarkMode: isDarkMode),

            _ => new GlassRenderParams(
                CornerRadiusPx: cornerRadiusPx,
                RefractionBandPx: 10f,
                RefractionStrength: 0.010f,
                BlurRadius: 18f,
                TintAlpha: isDarkMode ? 0.48f : 0.56f,
                SpecularStrength: isDarkMode ? 0.32f : 0.45f,
                IsDarkMode: isDarkMode)
        };
    }
}
