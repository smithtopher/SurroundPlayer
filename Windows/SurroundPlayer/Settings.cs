// Persisted settings, same keys and defaults as the macOS app's UserDefaults
// (domain local.surroundplayer), stored as JSON in %LOCALAPPDATA%\SurroundPlayer\settings.json.
using System;
using System.IO;
using System.Text.Json;

namespace SurroundPlayer;

public sealed class Settings
{
    public string deviceUID { get; set; } = "";
    public string deviceName { get; set; } = "";
    public bool ac3Enabled { get; set; } = true;          // bitstream on/off
    public int bitstreamCodec { get; set; } = 0;          // 0 AC3, 1 DTS
    public int ac3Bitrate { get; set; } = 640;
    public int dtsBitrate { get; set; } = 1509;
    public bool ac3Passthrough { get; set; } = true;
    public bool stereoAsAC3 { get; set; } = false;
    public double avOffsetMs { get; set; } = 0;
    public bool visualizerEnabled { get; set; } = false;

    private static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SurroundPlayer", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(Path)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
