using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TahoeDesk.Core.Config;

public sealed class JsonSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;

    public JsonSettingsStore(string? filePath = null)
    {
        _filePath = string.IsNullOrWhiteSpace(filePath)
            ? GetDefaultSettingsPath()
            : filePath;
    }

    public string FilePath => _filePath;

    public static string GetDefaultSettingsPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "TahoeDesk", "settings.json");
    }

    public GlassSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return GlassSettings.Default;
            }

            var json = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<GlassSettings>(json, SerializerOptions);
            return loaded ?? GlassSettings.Default;
        }
        catch (Exception)
        {
            return GlassSettings.Default;
        }
    }

    public void Save(GlassSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(settings, SerializerOptions);
        var tempPath = _filePath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _filePath, overwrite: true);
    }
}
