// Subtitles: the engine's tracks (embedded streams + files next to the video + files the user
// loads), the remembered language, and polling what to show.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SurroundPlayer;

/// What the subtitle overlay should show now. Kind: Native.PC_SUB_*.
public sealed record SubtitleFrame(int Kind, string Text, byte[]? Bgra, int X, int Y, int W, int H, int RefW, int RefH)
{
    public static readonly SubtitleFrame None = new(Native.PC_SUB_NONE, "", null, 0, 0, 0, 0, 0, 0);
}

public sealed partial class PlayerModel
{
    public static readonly string[] SubtitleExtensions = { ".srt", ".ass", ".ssa", ".vtt" };

    private IReadOnlyList<string> _subtitleTracks = Array.Empty<string>();
    public IReadOnlyList<string> SubtitleTracks { get => _subtitleTracks; private set => Set(ref _subtitleTracks, value); }
    private int _currentSubtitleTrack = -1;
    public int CurrentSubtitleTrack { get => _currentSubtitleTrack; private set => Set(ref _currentSubtitleTrack, value); }
    private List<string> _subtitleLangs = new();   // normalised 2-letter codes ("" = unknown), per track

    private int _videoWidth, _videoHeight;
    public int VideoWidth { get => _videoWidth; private set => Set(ref _videoWidth, value); }
    public int VideoHeight { get => _videoHeight; private set => Set(ref _videoHeight, value); }

    private string? _subFilePath;                  // the media file the next two belong to
    private readonly List<string> _userSubFiles = new();   // loaded with "Load subtitle file…"
    private int? _keepSubtitle;                    // selection to restore when the same file reopens
    private int _subSerial;

    /// Called in Play before the engine reopens: same file (audio track change, device
    /// reopen) keeps the subtitle choice and loaded files; another file starts fresh.
    private void PrepareSubtitlesForOpen(Track track)
    {
        bool same = string.Equals(_subFilePath, track.Path, StringComparison.OrdinalIgnoreCase);
        _keepSubtitle = same ? CurrentSubtitleTrack : null;
        if (!same) _userSubFiles.Clear();
        _subFilePath = track.Path;
    }

    /// After open: add sidecar files and the user's files, list the tracks, select one.
    private unsafe void LoadSubtitles(Track track)
    {
        if (HasVideo)
        {
            foreach (var f in FindSidecarSubtitles(track.Path)) AddSubtitleFileToEngine(f);
            foreach (var f in _userSubFiles) AddSubtitleFileToEngine(f);
        }
        RefreshSubtitleTracks();
        int pick = _keepSubtitle is int k && k < SubtitleTracks.Count ? k : PreferredSubtitleTrack();
        Native.pc_player_set_subtitle_track(_player, pick);
        CurrentSubtitleTrack = Native.pc_player_current_subtitle_track(_player);
    }

    private unsafe void RefreshSubtitleTracks()
    {
        var names = new List<string>();
        var langs = new List<string>();
        int count = Native.pc_player_subtitle_track_count(_player);
        var buf = new byte[256];
        for (int i = 0; i < count; i++)
        {
            fixed (byte* b = buf) { Native.pc_player_subtitle_track_name(_player, i, b, 256); names.Add(Native.Str(b, 256)); }
            fixed (byte* b = buf) { Native.pc_player_subtitle_track_language(_player, i, b, 256); langs.Add(NormalizeLanguage(Native.Str(b, 256))); }
        }
        // external files: language from the name, e.g. "Movie.en.srt" or "Movie.eng.forced.srt"
        for (int i = 0; i < count; i++)
            if (langs[i].Length == 0 && names[i].Contains(": ", StringComparison.Ordinal))
            {
                var file = names[i][(names[i].IndexOf(": ", StringComparison.Ordinal) + 2)..];
                var parts = Path.GetFileNameWithoutExtension(file).Split('.');
                for (int j = parts.Length - 1; j >= 1 && langs[i].Length == 0; j--) langs[i] = NormalizeLanguage(parts[j]);
            }
        _subtitleLangs = langs;
        SubtitleTracks = names;
    }

    /// Off unless the user turned subtitles on before (Settings.subtitleLanguage): then a
    /// track in that language, else one of unknown language. A forced track (foreign-language
    /// parts only) is shown even when subtitles are off.
    private int PreferredSubtitleTrack()
    {
        string pref = _settings.subtitleLanguage ?? "";
        bool Forced(int i) => SubtitleTracks[i].Contains("(forced)", StringComparison.OrdinalIgnoreCase)
                              || SubtitleTracks[i].Contains(".forced.", StringComparison.OrdinalIgnoreCase);
        var all = Enumerable.Range(0, SubtitleTracks.Count).ToList();
        if (pref.Length == 0) return all.FirstOrDefault(Forced, -1);
        if (pref == "*") return all.Where(i => !Forced(i)).DefaultIfEmpty(all.FirstOrDefault(-1)).First();
        var lang = all.Where(i => _subtitleLangs[i] == pref).ToList();
        if (lang.Count > 0) return lang.Where(i => !Forced(i)).DefaultIfEmpty(lang[0]).First();
        return all.FirstOrDefault(i => _subtitleLangs[i].Length == 0 && !Forced(i), -1);
    }

    /// The user's choice (-1 = off); also remembered for the next files.
    public void SelectSubtitleTrack(int index)
    {
        if (!_isOpen) return;
        Native.pc_player_set_subtitle_track(_player, index);
        CurrentSubtitleTrack = Native.pc_player_current_subtitle_track(_player);
        _settings.subtitleLanguage = CurrentSubtitleTrack < 0 ? ""
            : _subtitleLangs[CurrentSubtitleTrack] is { Length: > 0 } l ? l : "*";
        _settings.Save();
    }

    /// S key: off -> track 1 -> track 2 ... -> off.
    public void CycleSubtitleTrack()
    {
        if (SubtitleTracks.Count == 0) return;
        int next = CurrentSubtitleTrack + 1;
        SelectSubtitleTrack(next >= SubtitleTracks.Count ? -1 : next);
    }

    /// Adds a subtitle file for the current video and shows it.
    public void LoadSubtitleFile(string path)
    {
        if (!_isOpen) return;
        int index = AddSubtitleFileToEngine(path);
        if (index < 0) { ErrorMessage = $"Could not read subtitles from {Path.GetFileName(path)}"; return; }
        if (!_userSubFiles.Contains(path, StringComparer.OrdinalIgnoreCase)) _userSubFiles.Add(path);
        RefreshSubtitleTracks();
        SelectSubtitleTrack(index);
    }

    private int AddSubtitleFileToEngine(string path) =>
        Native.pc_player_add_subtitle_file(_player, Utf8Copy(path), Path.GetFileName(path));

    /// "Movie.srt", "Movie.en.srt", "Movie.English.forced.ass"... next to "Movie.mkv".
    public static List<string> FindSidecarSubtitles(string mediaPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(mediaPath));
            if (dir == null) return new();
            string stem = Path.GetFileNameWithoutExtension(mediaPath);
            return Directory.EnumerateFiles(dir)
                .Where(f => SubtitleExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .Where(f =>
                {
                    var n = Path.GetFileNameWithoutExtension(f);
                    return n.Equals(stem, StringComparison.OrdinalIgnoreCase)
                        || n.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase);
                })
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch { return new(); }
    }

    /// FFmpeg reads subtitle text as UTF-8. Files in another encoding (UTF-16, or the ANSI
    /// code page older rippers used) are converted to a UTF-8 copy in %TEMP%.
    private static string Utf8Copy(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            bool utf16 = bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF));
            if (!utf16)
            {
                try { new UTF8Encoding(false, true).GetString(bytes); return path; }
                catch (DecoderFallbackException) { }
            }
            var dir = Path.Combine(Path.GetTempPath(), "SurroundPlayer", "subtitles");
            Directory.CreateDirectory(dir);
            var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..12];
            var copy = Path.Combine(dir, hash + Path.GetExtension(path));
            File.WriteAllText(copy, CueSheet.ReadText(path), new UTF8Encoding(false));
            return copy;
        }
        catch { return path; }
    }

    // ISO 639-2/B codes that differ from the /T ones .NET knows
    private static readonly Dictionary<string, string> BCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fre"] = "fr", ["ger"] = "de", ["dut"] = "nl", ["chi"] = "zh", ["cze"] = "cs", ["gre"] = "el",
        ["per"] = "fa", ["rum"] = "ro", ["slo"] = "sk", ["alb"] = "sq", ["arm"] = "hy", ["baq"] = "eu",
        ["bur"] = "my", ["geo"] = "ka", ["ice"] = "is", ["mac"] = "mk", ["mao"] = "mi", ["may"] = "ms",
        ["tib"] = "bo", ["wel"] = "cy",
    };
    private static Dictionary<string, string>? _langIndex;

    /// "eng" / "en" / "English" -> "en"; "" if not recognised.
    public static string NormalizeLanguage(string code)
    {
        code = code.Trim();
        if (code.Length == 0 || code.Equals("und", StringComparison.OrdinalIgnoreCase)) return "";
        if (BCodes.TryGetValue(code, out var b)) return b;
        if (_langIndex == null)
        {
            var idx = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
            {
                if (c.TwoLetterISOLanguageName.Length != 2) continue;
                idx.TryAdd(c.TwoLetterISOLanguageName, c.TwoLetterISOLanguageName);
                idx.TryAdd(c.ThreeLetterISOLanguageName, c.TwoLetterISOLanguageName);
                idx.TryAdd(c.EnglishName, c.TwoLetterISOLanguageName);
            }
            _langIndex = idx;
        }
        return _langIndex.TryGetValue(code, out var two) ? two : "";
    }

    /// What the overlay should show, when it changed since the last call (else null).
    public unsafe SubtitleFrame? PollSubtitle()
    {
        if (!_isOpen)
        {
            if (_subSerial == -1) return null;
            _subSerial = -1;
            return SubtitleFrame.None;
        }
        Native.pc_player_subtitle_state(_player, out var st);
        if (st.serial == _subSerial) return null;
        SubtitleFrame frame;
        if (st.kind == Native.PC_SUB_TEXT)
        {
            var buf = new byte[st.text_len + 1];
            int serial;
            fixed (byte* b = buf) serial = Native.pc_player_subtitle_text(_player, b, buf.Length);
            if (serial != st.serial) return null;   // changed meanwhile: next poll
            frame = new SubtitleFrame(st.kind, Encoding.UTF8.GetString(buf, 0, st.text_len), null, 0, 0, 0, 0, 0, 0);
        }
        else if (st.kind == Native.PC_SUB_BITMAP && st.w > 0 && st.h > 0)
        {
            var px = new byte[st.w * st.h * 4];
            int serial;
            fixed (byte* b = px) serial = Native.pc_player_subtitle_bitmap(_player, b, px.Length);
            if (serial != st.serial) return null;
            frame = new SubtitleFrame(st.kind, "", px, st.x, st.y, st.w, st.h,
                                      st.ref_w > 0 ? st.ref_w : VideoWidth, st.ref_h > 0 ? st.ref_h : VideoHeight);
        }
        else frame = SubtitleFrame.None;
        _subSerial = st.serial;
        return frame;
    }
}
