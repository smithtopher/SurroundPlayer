// Playlists: .m3u/.m3u8 files the user saves or opens, and the session file that restores
// the playlist at the next launch. No UI dependencies.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SurroundPlayer;

public static class PlaylistFile
{
    public static bool IsPlaylist(string path)
    {
        var e = Path.GetExtension(path);
        return e.Equals(".m3u8", StringComparison.OrdinalIgnoreCase) || e.Equals(".m3u", StringComparison.OrdinalIgnoreCase);
    }

    /// The entries of an .m3u/.m3u8 as full paths (relative ones resolved against the
    /// playlist's folder). Comments, URLs and nested playlists are skipped. Never throws.
    public static List<string> ReadM3u(string path)
    {
        var result = new List<string>();
        try
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
            foreach (var raw in CueSheet.ReadText(path).Split('\n'))
            {
                var line = raw.Trim().TrimStart('﻿');
                if (line.Length == 0 || line.StartsWith('#') || line.Contains("://")) continue;
                if (line.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)) line = Uri.UnescapeDataString(line[8..]);
                line = line.Replace('/', Path.DirectorySeparatorChar);
                string full;
                try { full = Path.GetFullPath(Path.IsPathRooted(line) ? line : Path.Combine(dir, line)); }
                catch { continue; }
                if (!IsPlaylist(full)) result.Add(full);
            }
        }
        catch { }
        return result;
    }

    /// Writes an extended M3U in UTF-8. Paths are relative to the playlist's folder when they
    /// share its drive, so a library can be moved with its playlists. Cue sheet tracks are
    /// written as their sheet (once per run of tracks from it), which other players also read.
    public static void WriteM3u(string path, IEnumerable<Track> tracks)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
        var sb = new StringBuilder("#EXTM3U\n");
        string? lastSheet = null;
        foreach (var t in tracks)
        {
            string target, title;
            if (t.Cue is { CuePath.Length: > 0 } cue)
            {
                if (cue.CuePath.Equals(lastSheet, StringComparison.OrdinalIgnoreCase)) continue;
                lastSheet = cue.CuePath;
                target = cue.CuePath;
                title = cue.Album.Length > 0 ? cue.Album : Path.GetFileNameWithoutExtension(cue.CuePath);
            }
            else
            {
                lastSheet = null;
                target = t.Path;
                title = t.Name;
            }
            string rel = Path.GetRelativePath(dir, Path.GetFullPath(target));
            sb.Append("#EXTINF:-1,").Append(title.Replace('\n', ' ')).Append('\n').Append(rel).Append('\n');
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    // ------------------------------------------------------------------ session (restore at launch)

    public sealed record SessionTrack(string Path, CueTrack? Cue);
    public sealed record Session(int Version, List<SessionTrack> Tracks, int Current, double Position);

    private static string SessionPath => Path.Combine(Settings.DataDir, "playlist.json");

    public static void SaveSession(IReadOnlyList<Track> tracks, Track? current, double position)
    {
        try
        {
            var s = new Session(1, tracks.Select(t => new SessionTrack(t.Path, t.Cue)).ToList(),
                                current == null ? -1 : tracks.ToList().IndexOf(current), position);
            Directory.CreateDirectory(Path.GetDirectoryName(SessionPath)!);
            var tmp = SessionPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s));
            File.Move(tmp, SessionPath, overwrite: true);
        }
        catch { }
    }

    public static Session? LoadSession()
    {
        try { return File.Exists(SessionPath) ? JsonSerializer.Deserialize<Session>(File.ReadAllText(SessionPath)) : null; }
        catch { return null; }
    }
}
