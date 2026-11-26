using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using TahoeDesk.Core.Config;
using TahoeDesk.Core.Glass;
using TahoeDesk.Core.Shell;

namespace TahoeDesk.Core.Diagnostics;

/// <summary>
/// Generates a clean, anonymized plain-text diagnostics report for troubleshooting and GitHub issue reports.
/// </summary>
public static class DiagnosticsReportBuilder
{
    public const string AppVersion = "1.0.1";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string BuildReport(
        GlassSettings settings,
        IReadOnlyList<MonitorContext> monitors,
        SamplerHealth samplerHealth,
        string? crashLogPath = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("========================================");
        sb.AppendLine($"TahoeDesk Diagnostics Report (v{AppVersion})");
        sb.AppendLine("========================================");
        sb.AppendLine($"Generated (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
        sb.AppendLine($"OS Version     : {Environment.OSVersion}");
        sb.AppendLine($".NET Runtime   : {Environment.Version}");
        sb.AppendLine($"64-Bit Process : {Environment.Is64BitProcess}");
        sb.AppendLine($"DXGI Sampler   : {samplerHealth}");
        sb.AppendLine();

        sb.AppendLine($"--- Monitors ({monitors.Count}) ---");
        for (int i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            sb.AppendLine($"[{i}] Primary={m.IsPrimary} Bounds=({m.Bounds.Left},{m.Bounds.Top},{m.Bounds.Width}x{m.Bounds.Height}) WorkArea=({m.WorkArea.Left},{m.WorkArea.Top},{m.WorkArea.Width}x{m.WorkArea.Height}) DPI={m.DpiScale:0.00}x");
        }
        sb.AppendLine();

        sb.AppendLine("--- Settings ---");
        sb.AppendLine(JsonSerializer.Serialize(settings, JsonOptions));
        sb.AppendLine();

        sb.AppendLine("--- Recent Crash Log (tail 60 lines) ---");
        if (!string.IsNullOrWhiteSpace(crashLogPath) && File.Exists(crashLogPath))
        {
            try
            {
                var tail = File.ReadLines(crashLogPath).TakeLast(60).ToList();
                if (tail.Count == 0) sb.AppendLine("(empty)");
                else foreach (var line in tail) sb.AppendLine(line);
            }
            catch (Exception ex)
            {
                sb.AppendLine($"(unable to read crash log: {ex.Message})");
            }
        }
        else
        {
            sb.AppendLine("(no crash log recorded)");
        }

        return sb.ToString();
    }
}
