// Persisted settings, same keys and defaults as the macOS app's UserDefaults
// (domain local.surroundplayer), stored as JSON in %LOCALAPPDATA%\SurroundPlayer\settings.json.
using System;
using System.Collections.Generic;
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
    public bool multichannelPcm { get; set; } = false;    // don't transcode: multichannel PCM (HDMI to an AVR)
    public double avOffsetMs { get; set; } = 0;
    public bool visualizerEnabled { get; set; } = false;
    public List<string> recentFiles { get; set; } = new();   // Open Recent, newest first (max 10)
    // stereo upmix (same keys and defaults as the Mac app's Upmix.swift)
    public int upmixMode { get; set; } = 0;              // 0 off, 1 music, 2 movies & TV, 3 wide, 4 custom
    public int upmixLastMode { get; set; } = 1;          // what U turns back on
    public bool upmixVideo { get; set; } = false;        // also files with a video track
    public double upmixSurroundDb { get; set; } = -3;    // -40 = rears off
    public double upmixCenter { get; set; } = 0.4;       // 0 phantom .. 1 locked to the centre speaker
    public double upmixWidth { get; set; } = 0.55;       // 0.5 = as recorded
    public double upmixLfeDb { get; set; } = -6;         // -40 = no LFE
    public int upmixCrossoverHz { get; set; } = 100;
    public bool upmixLfeMove { get; set; } = false;      // false copy bass to the sub, true move it
    public double upmixBassDb { get; set; } = 0;
    public double upmixRearDelayMs { get; set; } = 10;
    public int upmixRearHighcutHz { get; set; } = 7000;  // 0 = off
    public string subtitleLanguage { get; set; } = "";       // subtitles: "" follow the file's default track, "off", "*" any, else a 2-letter code (Windows only)
    // Windows only: last window size in DIPs (0 = default 1280x800)
    public double windowWidth { get; set; }
    public double windowHeight { get; set; }

    /// %LOCALAPPDATA%\SurroundPlayer, or SURROUND_DATA_DIR when set (tests use a scratch folder
    /// so they never touch the user's real settings and playlist).
    public static string DataDir =>
        Environment.GetEnvironmentVariable("SURROUND_DATA_DIR") is { Length: > 0 } d ? d
        : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SurroundPlayer");

    private static string Path => System.IO.Path.Combine(DataDir, "settings.json");

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
