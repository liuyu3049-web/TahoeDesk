namespace TahoeDesk.Core.Glass;

public static class LuminanceClassifier
{
    public const double DarkInkThreshold = 0.56;

    public static double ComputePerceivedLuminance(byte r, byte g, byte b)
        => (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;

    public static bool ShouldUseDarkInk(byte avgR, byte avgG, byte avgB)
        => ComputePerceivedLuminance(avgR, avgG, avgB) >= DarkInkThreshold;
}
