using System;
using Microsoft.Win32;

namespace GlassShell.Core.Shell;

/// <summary>
/// Manages standard-user Windows autostart via HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
/// Requires zero administrator privileges and never touches HKLM.
/// </summary>
public static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "GlassShell";

    public static string BuildRunCommand(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return string.Empty;
        string trimmed = exePath.Trim().Trim('"');
        return $"\"{trimmed}\"";
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            string? val = key?.GetValue(ValueName) as string;
            return !string.IsNullOrWhiteSpace(val);
        }
        catch
        {
            return false;
        }
    }

    public static bool SetEnabled(bool enabled, string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null) return false;

            if (enabled)
            {
                string cmd = BuildRunCommand(exePath);
                if (string.IsNullOrEmpty(cmd)) return false;
                key.SetValue(ValueName, cmd, RegistryValueKind.String);
            }
            else
            {
                if (key.GetValue(ValueName) is not null)
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
