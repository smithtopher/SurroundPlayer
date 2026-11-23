// Stereo upmix modes and their slider values. The presets are fixed; Custom is the user's own
// profile, saved when they leave Custom (for a preset, Off or the U key) and restored when they
// come back, so comparing against a preset doesn't lose it. Tone (bass and treble boost) isn't
// part of a profile: it's a listening preference that stays as it is across modes. Pure settings
// logic (no UI, no engine), so it can be tested on its own.
namespace SurroundPlayer;

public enum UpmixMode { Off = 0, Music, Movies, Wide, Custom }

/// One set of upmix slider values (settings.json "upmixCustom" holds the Custom profile).
public sealed class UpmixValues
{
    public double surroundDb { get; set; }
    public double center { get; set; }
    public double width { get; set; }
    public double lfeDb { get; set; }
    public int crossoverHz { get; set; }
    public bool lfeMove { get; set; }
    public double rearDelayMs { get; set; }
    public int rearHighcutHz { get; set; }
}

public static class UpmixProfiles
{
    /// The fixed presets (same values as the Mac app's Upmix.swift); null for Off and Custom.
    public static UpmixValues? Preset(UpmixMode m) => m switch
    {
        UpmixMode.Music => V(-3, 0.4, 0.55, -6, 100, false, 10, 7000),
        UpmixMode.Movies => V(-4, 0.9, 0.5, -3, 120, false, 15, 7000),
        UpmixMode.Wide => V(0, 0.5, 0.8, -3, 100, false, 5, 12000),
        _ => null,
    };

    private static UpmixValues V(double s, double c, double w, double l, int x, bool mv, double d, int h) =>
        new() { surroundDb = s, center = c, width = w, lfeDb = l, crossoverHz = x, lfeMove = mv, rearDelayMs = d, rearHighcutHz = h };

    public static UpmixMode Mode(Settings s) => (UpmixMode)System.Math.Clamp(s.upmixMode, 0, 4);

    /// The sliders as they are now.
    public static UpmixValues Current(Settings s) =>
        V(s.upmixSurroundDb, s.upmixCenter, s.upmixWidth, s.upmixLfeDb, s.upmixCrossoverHz, s.upmixLfeMove,
          s.upmixRearDelayMs, s.upmixRearHighcutHz);

    public static void Apply(Settings s, UpmixValues v)
    {
        (s.upmixSurroundDb, s.upmixCenter, s.upmixWidth, s.upmixLfeDb, s.upmixCrossoverHz, s.upmixLfeMove,
         s.upmixRearDelayMs, s.upmixRearHighcutHz) =
            (v.surroundDb, v.center, v.width, v.lfeDb, v.crossoverHz, v.lfeMove, v.rearDelayMs, v.rearHighcutHz);
    }

    /// Switch mode. Leaving Custom saves its values; a preset loads its own; Custom brings the
    /// saved values back (the first time, it starts from whatever the sliders show); Off leaves
    /// the sliders alone.
    public static void SetMode(Settings s, UpmixMode to)
    {
        var from = Mode(s);
        if (from == to) return;
        if (from == UpmixMode.Custom) s.upmixCustom = Current(s);
        s.upmixMode = (int)to;
        if (to == UpmixMode.Custom) { if (s.upmixCustom != null) Apply(s, s.upmixCustom); }
        else if (Preset(to) is { } p) Apply(s, p);
    }

    /// A slider moved: a preset becomes Custom, and the Custom profile follows the sliders.
    public static void ValueChanged(Settings s)
    {
        var m = Mode(s);
        if (m is UpmixMode.Music or UpmixMode.Movies or UpmixMode.Wide) s.upmixMode = (int)UpmixMode.Custom;
        if (Mode(s) == UpmixMode.Custom) s.upmixCustom = Current(s);
    }
}
