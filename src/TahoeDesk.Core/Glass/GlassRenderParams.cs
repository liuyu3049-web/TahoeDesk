using TahoeDesk.Core.Config;

namespace TahoeDesk.Core.Glass;

public readonly record struct GlassRenderParams(
    float CornerRadiusPx,
    float RefractionBandPx,
    float RefractionStrength,
    float BlurRadius,
    float TintAlpha,
    float SpecularStrength,
    bool IsDarkMode)
{
    public static GlassRenderParams FromPreset(
        GlassOpacityPreset preset,
        bool isDarkMode,
        float cornerRadiusPx,
        bool isOverlayCard = false)
    {
        if (isOverlayCard)
        {
            // Menus, Calendar, and Settings panels sit over arbitrary busy/white windows:
            // use stronger blur & richer adaptive tint with clean subtle bevel refraction.
            return preset switch
            {
                GlassOpacityPreset.Clear => new GlassRenderParams(
                    CornerRadiusPx: cornerRadiusPx,
                    RefractionBandPx: 8f,
                    RefractionStrength: 0.0028f,
                    BlurRadius: 18f,
                    TintAlpha: isDarkMode ? 0.62f : 0.72f,
                    SpecularStrength: isDarkMode ? 0.34f : 0.55f,
                    IsDarkMode: isDarkMode),

                GlassOpacityPreset.Balanced => new GlassRenderParams(
                    CornerRadiusPx: cornerRadiusPx,
                    RefractionBandPx: 7f,
                    RefractionStrength: 0.0022f,
                    BlurRadius: 22f,
                    TintAlpha: isDarkMode ? 0.72f : 0.80f,
                    SpecularStrength: isDarkMode ? 0.30f : 0.48f,
                    IsDarkMode: isDarkMode),

                _ => new GlassRenderParams(
                    CornerRadiusPx: cornerRadiusPx,
                    RefractionBandPx: 6f,
                    RefractionStrength: 0.0016f,
                    BlurRadius: 26f,
                    TintAlpha: isDarkMode ? 0.82f : 0.88f,
                    SpecularStrength: isDarkMode ? 0.24f : 0.40f,
                    IsDarkMode: isDarkMode)
            };
        }

        // Dock & Top Menu Bar Liquid Glass presets
        return preset switch
        {
            GlassOpacityPreset.Clear => new GlassRenderParams(
                CornerRadiusPx: cornerRadiusPx,
                RefractionBandPx: 11f,
                RefractionStrength: 0.0042f,
                BlurRadius: 16f,
                TintAlpha: isDarkMode ? 0.22f : 0.28f,
                SpecularStrength: isDarkMode ? 0.46f : 0.68f,
                IsDarkMode: isDarkMode),

            GlassOpacityPreset.Balanced => new GlassRenderParams(
                CornerRadiusPx: cornerRadiusPx,
                RefractionBandPx: 9f,
                RefractionStrength: 0.0032f,
                BlurRadius: 20f,
                TintAlpha: isDarkMode ? 0.38f : 0.46f,
                SpecularStrength: isDarkMode ? 0.38f : 0.55f,
                IsDarkMode: isDarkMode),

            _ => new GlassRenderParams(
                CornerRadiusPx: cornerRadiusPx,
                RefractionBandPx: 8f,
                RefractionStrength: 0.0022f,
                BlurRadius: 24f,
                TintAlpha: isDarkMode ? 0.56f : 0.64f,
                SpecularStrength: isDarkMode ? 0.30f : 0.44f,
                IsDarkMode: isDarkMode)
        };
    }
}
