// .cue sheet parsing: one big audio file (or several) split into virtual tracks.
// No UI or engine dependencies, so it can be tested on its own.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SurroundPlayer;

public sealed record CueTrack(
    string AudioPath,   // resolved, existing file
    int Number,
    string Title,
    string Performer,
    string Album,
    double Start,       // seconds into AudioPath (INDEX 01)
    double? End,        // seconds; null = to the end of the file
    string CuePath = "");  // the sheet it came from

public static class CueSheet
{
    /// Parses `cuePath` and returns its audio tracks whose files exist. Never throws:
    /// an unreadable or empty sheet gives an empty list.
    public static List<CueTrack> Load(string cuePath, ISet<string> mediaExtensions)
    {
        try { return Parse(ReadText(cuePath), cuePath, mediaExtensions); }
        catch { return new List<CueTrack>(); }
    }

    private sealed class Raw
    {
        public string File = "";
        public int Number;
        public bool Audio = true;
        public string Title = "", Performer = "";
        public double? Index1;
    }

    public static List<CueTrack> Parse(string text, string cuePath, ISet<string> mediaExtensions)
    {
        string dir = Path.GetDirectoryName(cuePath) ?? "";
        string album = "", albumPerformer = "", file = "";
        var raws = new List<Raw>();
        Raw? cur = null;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0) continue;
            int sp = line.IndexOfAny(new[] { ' ', '\t' });
            string key = (sp < 0 ? line : line[..sp]).ToUpperInvariant();
            string rest = sp < 0 ? "" : line[(sp + 1)..].Trim();
            switch (key)
            {
                case "FILE":
                    file = FileName(rest);
                    break;
                case "TRACK":
                {
                    var parts = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    cur = new Raw { File = file };
                    if (parts.Length > 0) int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out cur.Number);
                    cur.Audio = parts.Length < 2 || parts[1].Equals("AUDIO", StringComparison.OrdinalIgnoreCase);
                    raws.Add(cur);
                    break;
                }
                case "TITLE":
                    if (cur == null) album = Unquote(rest); else cur.Title = Unquote(rest);
                    break;
                case "PERFORMER":
                    if (cur == null) albumPerformer = Unquote(rest); else cur.Performer = Unquote(rest);
                    break;
                case "INDEX":
                {
                    if (cur == null) break;
                    var parts = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2 && parts[0].TrimStart('0') == "1" && TryTime(parts[1], out double t))
                    {
                        cur.Index1 = t;
                        cur.File = file;   // a FILE line between TRACK and INDEX 01 belongs to this track
                    }
                    break;
                }
            }
        }

        var resolved = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        int fileCount = raws.Select(r => r.File).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        string? Resolve(string name)
        {
            if (resolved.TryGetValue(name, out var hit)) return hit;
            var r = ResolveFile(dir, name, fileCount == 1 ? cuePath : null, mediaExtensions);
            resolved[name] = r;
            return r;
        }

        var result = new List<CueTrack>();
        var playable = raws.Where(r => r.Index1 != null && r.File.Length > 0).ToList();
        for (int i = 0; i < playable.Count; i++)
        {
            var r = playable[i];
            // the track ends where the next one in the same file starts (data tracks included,
            // so a data track never gets played as audio)
            double? end = null;
            if (i + 1 < playable.Count && playable[i + 1].File.Equals(r.File, StringComparison.OrdinalIgnoreCase))
                end = playable[i + 1].Index1;
            if (!r.Audio) continue;
            if (end != null && end <= r.Index1) continue;
            var path = Resolve(r.File);
            if (path == null) continue;
            result.Add(new CueTrack(path, r.Number,
                r.Title.Length > 0 ? r.Title : $"Track {r.Number:00}",
                r.Performer.Length > 0 ? r.Performer : albumPerformer,
                album, r.Index1!.Value, end, cuePath));
        }
        return result;
    }

    /// The FILE line's name: quoted, or everything before the trailing file type.
    private static string FileName(string rest)
    {
        if (rest.StartsWith('"'))
        {
            int q = rest.IndexOf('"', 1);
            return q > 0 ? rest[1..q] : rest[1..];
        }
        int sp = rest.LastIndexOf(' ');
        return sp > 0 ? rest[..sp].Trim() : rest;
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '"')
        {
            int q = s.LastIndexOf('"');
            return (q > 0 ? s[1..q] : s[1..]).Trim();
        }
        return s;
    }

    /// mm:ss:ff, 75 frames per second (minutes can exceed 99).
    public static bool TryTime(string s, out double seconds)
    {
        seconds = 0;
        var p = s.Split(':');
        if (p.Length != 3) return false;
        if (!int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int m) ||
            !int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sec) ||
            !int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int f)) return false;
        seconds = m * 60 + sec + f / 75.0;
        return true;
    }

    /// The named file, else the same name with another media extension (rippers often write
    /// "x.wav" and the file was later converted to FLAC), else, for a single-file sheet,
    /// an audio file named like the sheet itself.
    private static string? ResolveFile(string dir, string name, string? cuePath, ISet<string> exts)
    {
        name = name.Replace('/', Path.DirectorySeparatorChar);
        var direct = Path.Combine(dir, name);
        if (File.Exists(direct)) return direct;
        foreach (var stem in new[] { Path.Combine(dir, Path.GetFileNameWithoutExtension(name)),
                                     cuePath == null ? null : Path.Combine(dir, Path.GetFileNameWithoutExtension(cuePath)) })
        {
            if (stem == null) continue;
            foreach (var e in exts)
            {
                var c = stem + "." + e;
                if (File.Exists(c)) return c;
            }
        }
        return null;
    }

    /// UTF-8 (with or without BOM) if it decodes cleanly, else the system ANSI code page
    /// (Windows-1252 on Western systems), which older rippers wrote.
    internal static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        int skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        try { return new UTF8Encoding(false, true).GetString(bytes, skip, bytes.Length - skip); }
        catch (DecoderFallbackException) { }
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage).GetString(bytes);
        }
        catch { return Encoding.Latin1.GetString(bytes); }
    }
}
