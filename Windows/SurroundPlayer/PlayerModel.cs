// The app model: playlist, transport, settings, devices and hot-plug, bridging the C
// engine (playercore.dll). Port of Sources/SurroundPlayer/PlayerModel.swift; everything
// here runs on the UI thread except the engine callbacks, which are marshalled to it.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace SurroundPlayer;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class Track : Observable
{
    public string Path { get; }
    public string Name { get; }
    public string Folder { get; }
    public string Ext { get; }
    public string SearchText { get; }

    /// Cue sheet tracks: a span of `Path` from `Start` to `End` (null = end of file).
    public CueTrack? Cue { get; }
    public double Start => Cue?.Start ?? 0;
    public double? End => Cue?.End;

    public Track(string path) : this(path, null) { }

    public Track(CueTrack cue) : this(cue.AudioPath, cue) { }

    private Track(string path, CueTrack? cue)
    {
        Path = path;
        Cue = cue;
        Name = cue != null ? $"{cue.Number:00}. {cue.Title}" : System.IO.Path.GetFileNameWithoutExtension(path);
        Folder = new DirectoryInfo(System.IO.Path.GetDirectoryName(path) ?? "").Name;
        Ext = System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        // last 4 path components without extension, case/diacritic-folded (like the Mac app)
        var noExt = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path) ?? "", System.IO.Path.GetFileNameWithoutExtension(path));
        var parts = noExt.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        var search = string.Join(" ", parts.Skip(Math.Max(0, parts.Length - 4)));
        if (cue != null) search += $" {cue.Title} {cue.Performer} {cue.Album}";
        SearchText = Fold(search);
    }

    /// The next item continues this one in the same file (consecutive cue tracks).
    public bool ContinuesInto(Track next) =>
        End is double end && next.Cue != null && next.Path.Equals(Path, StringComparison.OrdinalIgnoreCase)
        && Math.Abs(next.Start - end) < 0.001;

    private string _indicator = "";
    /// Segoe Fluent glyph shown in the playlist row (speaker when current).
    public string Indicator { get => _indicator; set => Set(ref _indicator, value); }

    public bool Matches(string[] terms) => terms.All(t => SearchText.Contains(t, StringComparison.Ordinal));

    public static string Fold(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }
}

public sealed record AudioDevice(string Uid, string Name, int Channels, bool IsHdmi, bool CanBitstream, bool IsDefault)
{
    public string Label => Name + (IsHdmi ? "  (HDMI)" : "") + (CanBitstream ? "  • DD" : "");
}

public sealed partial class PlayerModel : Observable
{
    public static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp3", "m4a", "aac", "flac", "wav", "aif", "aiff", "alac", "ogg", "opus", "wma", "ac3", "eac3", "dts", "mka", "ape", "wv", "dsf",
        "mp4", "m4v", "mkv", "mov", "avi", "webm", "ts", "m2ts", "mts", "wmv", "mpg", "mpeg", "vob", "flv",
    };

    private readonly DispatcherQueue _ui;
    private readonly IntPtr _player;
    private readonly Settings _settings = Settings.Load();
    public Settings Settings => _settings;
    private readonly DispatcherQueueTimer _timer;
    // keep the delegates alive for as long as the engine may call them
    private readonly Native.VideoCallback _videoCb;
    private readonly Native.EventCallback _eventCb;
    private readonly Native.DeviceChangeCallback _deviceCb;
    private int _generation;
    private bool _isOpen;
    private string? _openDeviceUid, _openDeviceName;
    private bool _reopenPending;   // the output was lost and the device isn't back yet

    /// Video frames go here (set by the window); called on the engine's presenter thread.
    public VideoRenderer? Renderer { get; set; }

    public ObservableCollection<Track> Playlist { get; } = new();
    public ObservableCollection<AudioDevice> Devices { get; } = new();

    public PlayerModel(DispatcherQueue ui)
    {
        _ui = ui;
        DebugTrace.Log("model: create player");
        _player = Native.pc_player_create();
        _videoCb = (ctx, frame, pts) => Renderer?.Present(frame);
        _eventCb = (ctx, ev, msg) =>
        {
            int gen = Volatile.Read(ref _generation);
            if (ev == Native.PC_EVENT_EOF) _ui.TryEnqueue(() => TrackFinished(gen));
        };
        _deviceCb = ctx => { DebugTrace.Log("device change notification"); _ui.TryEnqueue(RefreshDevices); };
        Native.pc_player_set_callbacks(_player, _videoCb, _eventCb, IntPtr.Zero);
        DebugTrace.Log("model: device callback");
        Native.pc_set_device_change_callback(_deviceCb, IntPtr.Zero);
        DebugTrace.Log("model: list devices");
        RefreshDevices();
        DebugTrace.Log("model: ready");
        _timer = ui.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(200);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        _saveTimer = ui.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromSeconds(2);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => SaveSession();
        RestoreSession();
        Playlist.CollectionChanged += (_, _) => ScheduleSave();
    }

    // ------------------------------------------------------------------ playlist persistence

    private readonly DispatcherQueueTimer _saveTimer;
    private Track? _resumeTrack;     // restored current track: play resumes at _resumeAt
    private double _resumeAt;

    /// Saves the session a couple of seconds after the last change (and at exit), so a
    /// crash or kill loses little.
    private void ScheduleSave() { _saveTimer.Stop(); _saveTimer.Start(); }

    private void SaveSession()
    {
        double pos = _isOpen ? Position : (Current != null && Current == _resumeTrack ? _resumeAt : 0);
        PlaylistFile.SaveSession(Playlist, Current, pos);
    }

    /// Brings back the last session's playlist (entries whose files are gone are dropped),
    /// with the current track selected but not playing.
    private void RestoreSession()
    {
        var s = PlaylistFile.LoadSession();
        if (s?.Tracks == null) return;
        Track? current = null;
        for (int i = 0; i < s.Tracks.Count; i++)
        {
            var e = s.Tracks[i];
            if (string.IsNullOrEmpty(e.Path) || !File.Exists(e.Path)) continue;
            var t = e.Cue != null ? new Track(e.Cue) : new Track(e.Path);
            Playlist.Add(t);
            if (i == s.Current) current = t;
        }
        DebugTrace.Log($"model: restored {Playlist.Count} of {s.Tracks.Count} playlist items");
        if (current == null) return;
        Current = current;
        _resumeTrack = current;
        _resumeAt = Math.Max(0, s.Position);
        // show what Play will resume (the duration is only known once the file is opened)
        Title = current.Cue?.Title ?? current.Name;
        Artist = current.Cue?.Performer ?? "";
        Album = current.Cue?.Album ?? "";
    }

    /// Replaces the playlist with an .m3u/.m3u8 file's entries and plays the first one.
    public void LoadPlaylist(string path)
    {
        if (PlaylistFile.ReadM3u(path).Count == 0)
        {
            ErrorMessage = $"{System.IO.Path.GetFileName(path)} has no playable entries";
            return;
        }
        ClearPlaylist();
        Add(new[] { path }, playFirst: true);
    }

    /// Saves the playlist as an .m3u8 file.
    public void SavePlaylist(string path)
    {
        try { PlaylistFile.WriteM3u(path, Playlist); }
        catch (Exception ex) { ErrorMessage = $"Could not save the playlist: {ex.Message}"; }
    }

    // ------------------------------------------------------------------ state

    private Track? _current;
    public Track? Current
    {
        get => _current;
        private set
        {
            if (_current == value) return;
            if (_current != null) _current.Indicator = "";
            _current = value;
            Raise();
            Raise(nameof(ShowsVisualizer));
            UpdateIndicator();
        }
    }

    private bool _isPlaying;
    public bool IsPlaying { get => _isPlaying; private set { if (Set(ref _isPlaying, value)) { UpdateIndicator(); UpdateKeepAwake(); } } }
    private double _position;
    public double Position { get => _position; private set => Set(ref _position, value); }
    private double _duration;
    public double Duration { get => _duration; private set => Set(ref _duration, value); }
    public bool IsScrubbing { get; set; }

    private string _title = "", _artist = "", _album = "", _sourceAudio = "", _outputDescription = "", _videoDescription = "";
    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Artist { get => _artist; private set => Set(ref _artist, value); }
    public string Album { get => _album; private set => Set(ref _album, value); }
    public string SourceAudio { get => _sourceAudio; private set => Set(ref _sourceAudio, value); }
    private string _sourceFormat = "";
    /// Short source format for the controls bar, e.g. "24-bit / 96 kHz" or "44.1 kHz" (lossy).
    public string SourceFormat { get => _sourceFormat; private set => Set(ref _sourceFormat, value); }

    public static string FormatRate(int rate, int bits)
    {
        if (rate <= 0) return "";
        string khz = (rate / 1000.0).ToString(rate % 1000 == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + " kHz";
        return bits > 0 ? $"{bits}-bit / {khz}" : khz;
    }
    public string OutputDescription { get => _outputDescription; private set => Set(ref _outputDescription, value); }
    public string VideoDescription { get => _videoDescription; private set => Set(ref _videoDescription, value); }
    private int _outputMode = -1;
    public int OutputMode { get => _outputMode; private set { if (Set(ref _outputMode, value)) Raise(nameof(IsBitstreaming)); } }
    private string _bitstreamCodecName = "";
    public string BitstreamCodecName { get => _bitstreamCodecName; private set => Set(ref _bitstreamCodecName, value); }
    private bool _hasVideo;
    public bool HasVideo { get => _hasVideo; private set { if (Set(ref _hasVideo, value)) UpdateKeepAwake(); } }
    private byte[]? _artwork;
    public byte[]? Artwork { get => _artwork; private set => Set(ref _artwork, value); }
    private IReadOnlyList<string> _audioTracks = Array.Empty<string>();
    public IReadOnlyList<string> AudioTracks { get => _audioTracks; private set => Set(ref _audioTracks, value); }
    private int _currentAudioTrack = -1;
    public int CurrentAudioTrack { get => _currentAudioTrack; private set => Set(ref _currentAudioTrack, value); }
    private string? _errorMessage;
    public string? ErrorMessage { get => _errorMessage; set => Set(ref _errorMessage, value); }
    private bool _isScanning;
    public bool IsScanning { get => _isScanning; private set => Set(ref _isScanning, value); }

    public bool IsBitstreaming => OutputMode == Native.PC_MODE_TRANSCODE || OutputMode == Native.PC_MODE_PASSTHROUGH;

    private void UpdateIndicator()
    {
        if (_current != null) _current.Indicator = IsPlaying ? "" : "";   // Volume / Speakers
    }

    // ------------------------------------------------------------------ settings

    public string DeviceUID
    {
        get => _settings.deviceUID;
        set
        {
            if (_settings.deviceUID == value) return;
            _settings.deviceUID = value;
            var d = Devices.FirstOrDefault(x => x.Uid == value);
            if (d != null) _settings.deviceName = d.Name;
            SettingChanged();
            Raise(nameof(SelectedDevice));
        }
    }
    public bool Ac3Enabled { get => _settings.ac3Enabled; set { if (_settings.ac3Enabled != value) { _settings.ac3Enabled = value; SettingChanged(); } } }
    public int BitstreamCodec { get => _settings.bitstreamCodec; set { if (_settings.bitstreamCodec != value) { _settings.bitstreamCodec = value; SettingChanged(); } } }
    public int Ac3Bitrate { get => _settings.ac3Bitrate; set { if (_settings.ac3Bitrate != value) { _settings.ac3Bitrate = value; SettingChanged(); } } }
    public int DtsBitrate { get => _settings.dtsBitrate; set { if (_settings.dtsBitrate != value) { _settings.dtsBitrate = value; SettingChanged(); } } }
    public bool Ac3Passthrough { get => _settings.ac3Passthrough; set { if (_settings.ac3Passthrough != value) { _settings.ac3Passthrough = value; SettingChanged(); } } }
    public bool StereoAsAC3 { get => _settings.stereoAsAC3; set { if (_settings.stereoAsAC3 != value) { _settings.stereoAsAC3 = value; SettingChanged(); } } }
    public bool MultichannelPcm { get => _settings.multichannelPcm; set { if (_settings.multichannelPcm != value) { _settings.multichannelPcm = value; SettingChanged(); } } }
    public double AvOffsetMs
    {
        get => _settings.avOffsetMs;
        set
        {
            if (_settings.avOffsetMs == value) return;
            _settings.avOffsetMs = value;
            _settings.Save();
            Native.pc_player_set_av_offset(_player, value / 1000);
            Raise();
        }
    }

    // ------------------------------------------------------------------ visualiser

    /// Off by default. When on it takes over the stage for every file, including ones with
    /// a video track (the video keeps decoding in sync but isn't drawn).
    public bool VisualizerEnabled
    {
        get => _settings.visualizerEnabled;
        set
        {
            if (_settings.visualizerEnabled == value) return;
            _settings.visualizerEnabled = value;
            _settings.Save();
            Raise();
            Raise(nameof(ShowsVisualizer));
            SyncVideo();
            UpdateKeepAwake();
        }
    }

    public bool ShowsVisualizer => VisualizerEnabled && Current != null;

    private bool _visLocked;
    public bool VisLocked { get => _visLocked; set => Set(ref _visLocked, value); }

    /// The stage tells the engine when the visualiser tap is needed.
    public void SetVisualizerActive(bool active) => Native.pc_player_set_vis_enabled(_player, active ? 1 : 0);

    public unsafe int VisSamples(float* lr, int frames) => Native.pc_player_vis_samples(_player, lr, frames);

    /// Stop drawing video frames while the visualiser is up.
    public void SyncVideo()
    {
        if (Renderer != null) Renderer.Enabled = !VisualizerEnabled;
    }

    private void SettingChanged([CallerMemberName] string? name = null)
    {
        _settings.Save();
        Raise(name);
        // output settings changed: reopen the current item at the same position
        if (_isOpen && Current != null) Play(Current, Position, !IsPlaying, CurrentAudioTrack);
    }

    // ------------------------------------------------------------------ devices

    public AudioDevice? SelectedDevice =>
        DeviceUID.Length == 0 ? Devices.FirstOrDefault(d => d.IsDefault) : Devices.FirstOrDefault(d => d.Uid == EffectiveDeviceUID);

    /// HDMI endpoints can change id when the TV re-sends EDID; fall back to the remembered name.
    public string EffectiveDeviceUID
    {
        get
        {
            if (DeviceUID.Length == 0 || Devices.Any(d => d.Uid == DeviceUID)) return DeviceUID;
            var d = Devices.FirstOrDefault(x => x.Name == _settings.deviceName);
            return d?.Uid ?? "";
        }
    }

    public unsafe void RefreshDevices()
    {
        var raw = new Native.PCDevice[64];
        int n = Native.pc_list_output_devices(raw, raw.Length);
        Devices.Clear();
        for (int i = 0; i < n; i++)
        {
            fixed (byte* uid = raw[i].uid, name = raw[i].name)
                Devices.Add(new AudioDevice(Native.Str(uid, 256), Native.Str(name, 256), raw[i].output_channels,
                                            raw[i].is_hdmi != 0, raw[i].is_digital != 0, raw[i].is_default != 0));
        }
        Raise(nameof(SelectedDevice));
        HandleDeviceChange();
    }

    /// The output died (TV changed input, HDMI re-enumerated) or the device disappeared:
    /// reopen at the same position once it's back, like the macOS app.
    private void HandleDeviceChange()
    {
        if (Current == null) return;
        bool lost = _isOpen && Native.pc_player_output_lost(_player) != 0;
        bool missing = _isOpen && !string.IsNullOrEmpty(_openDeviceUid) && Devices.All(d => d.Uid != _openDeviceUid);
        if (!lost && !missing && !_reopenPending) return;
        double pos = Position;
        bool wasPlaying = IsPlaying || _reopenPending;
        bool available = string.IsNullOrEmpty(_openDeviceName) ? Devices.Any(d => d.IsDefault)
                                                               : Devices.Any(d => d.Name == _openDeviceName);
        if (available)
        {
            _reopenPending = false;
            Play(Current, pos, !wasPlaying, CurrentAudioTrack);
            if (!_isOpen) _reopenPending = true;   // not ready yet; try again on the next change
        }
        else
        {
            var track = Current;
            Stop();
            Current = track;
            Position = pos;
            _reopenPending = true;
            ErrorMessage = $"Audio device {_openDeviceName} disconnected";
        }
    }

    // ------------------------------------------------------------------ playlist

    /// Adds files and folders (folders scanned recursively off the UI thread).
    public async void Add(IEnumerable<string> paths, bool playFirst, int? insertAt = null)
    {
        var list = paths.ToList();
        NoteRecent(list);
        IsScanning = true;
        var tracks = await Task.Run(() => Expand(list));
        IsScanning = false;
        if (tracks.Count == 0) return;
        int at = Math.Clamp(insertAt ?? Playlist.Count, 0, Playlist.Count);
        for (int i = 0; i < tracks.Count; i++) Playlist.Insert(at + i, tracks[i]);
        if (playFirst) Play(tracks[0]);
    }

    public static bool IsCue(string path) => System.IO.Path.GetExtension(path).Equals(".cue", StringComparison.OrdinalIgnoreCase);

    /// Files and folders → playlist items. A .cue sheet becomes its tracks, and the audio
    /// files it covers aren't listed again on their own.
    public static List<Track> Expand(IEnumerable<string> paths)
    {
        // a playlist file stands for its entries (files, cue sheets or folders), in place
        var input = new List<string>();
        foreach (var p in paths)
        {
            if (PlaylistFile.IsPlaylist(p) && File.Exists(p)) input.AddRange(PlaylistFile.ReadM3u(p));
            else input.Add(p);
        }

        var files = new List<string>();
        foreach (var p in input)
        {
            if (Directory.Exists(p))
            {
                var found = new List<string>();
                try
                {
                    var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true,
                                                        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
                    foreach (var f in Directory.EnumerateFiles(p, "*", opts))
                        if (IsCue(f) || MediaExtensions.Contains(System.IO.Path.GetExtension(f).TrimStart('.'))) found.Add(f);
                }
                catch { }
                found.Sort(Native.StrCmpLogicalW);   // Explorer's natural order
                files.AddRange(found);
            }
            else if (File.Exists(p)) files.Add(p);
        }

        var sheets = new Dictionary<string, List<CueTrack>>(StringComparer.OrdinalIgnoreCase);
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files.Where(IsCue))
        {
            if (sheets.ContainsKey(f)) continue;
            var cue = CueSheet.Load(f, MediaExtensions);
            sheets[f] = cue;
            foreach (var t in cue) covered.Add(System.IO.Path.GetFullPath(t.AudioPath));
        }

        var tracks = new List<Track>();
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            if (!emitted.Add(f)) continue;
            if (IsCue(f)) tracks.AddRange(sheets[f].Select(c => new Track(c)));
            else if (!covered.Contains(System.IO.Path.GetFullPath(f))) tracks.Add(new Track(f));
        }
        return tracks;
    }

    public void Remove(IEnumerable<Track> tracks)
    {
        var set = tracks.ToHashSet();
        if (Current != null && set.Contains(Current)) { Stop(); Current = null; }
        foreach (var t in set) Playlist.Remove(t);
    }

    public void ClearPlaylist()
    {
        Stop();
        Current = null;
        Playlist.Clear();
    }

    // ------------------------------------------------------------------ transport

    // ------------------------------------------------------------------ Open Recent

    public const int MaxRecents = 10;

    /// What the user opened (files, folders or cue sheets, via Open, drops or the command
    /// line), newest first. Auto-advance doesn't add to it. Same rules as the Mac app.
    public IReadOnlyList<string> RecentFiles => _settings.recentFiles;

    private void NoteRecent(IEnumerable<string> paths)
    {
        var list = _settings.recentFiles.ToList();
        foreach (var p in paths.Reverse())   // keep the opened order: first path on top
        {
            string full;
            try { full = System.IO.Path.GetFullPath(p); } catch { continue; }
            list.RemoveAll(r => r.Equals(full, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, full);
        }
        SetRecents(list.Take(MaxRecents).ToList());
    }

    private void SetRecents(List<string> list)
    {
        _settings.recentFiles = list;
        _settings.Save();
        Raise(nameof(RecentFiles));
    }

    public void ClearRecents() => SetRecents(new List<string>());

    /// Plays a recent item: a file already in the playlist plays in place; anything else
    /// (a file not in the playlist, a folder, a cue sheet) is added and its first item played.
    public void OpenRecent(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            SetRecents(_settings.recentFiles.Where(r => !r.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList());
            ErrorMessage = $"{System.IO.Path.GetFileName(path)} no longer exists";
            return;
        }
        var existing = Playlist.FirstOrDefault(t => t.Cue == null &&
            string.Equals(System.IO.Path.GetFullPath(t.Path), path, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            NoteRecent(new[] { path });
            Play(existing);
        }
        else Add(new[] { path }, playFirst: true);
    }

    /// Menu title: the name, plus the parent folder when two recents share a name.
    public string RecentTitle(string path)
    {
        string name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        if (name.Length == 0) return path;   // a drive root
        bool clash = _settings.recentFiles.Count(r =>
            System.IO.Path.GetFileName(r.TrimEnd('\\', '/')).Equals(name, StringComparison.OrdinalIgnoreCase)) > 1;
        if (!clash) return name;
        string parent = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path.TrimEnd('\\', '/')) ?? "");
        return parent.Length > 0 ? $"{name} — {parent}" : name;
    }

    public unsafe void Play(Track track, double start = 0, bool paused = false, int audioTrack = -1)
    {
        Interlocked.Increment(ref _generation);
        if (Current != track) Renderer?.Clear();
        ErrorMessage = null;
        _resumeTrack = null;
        ScheduleSave();

        var cfg = new Native.PCConfig
        {
            // multichannel PCM transcodes nothing; bitstream is then only for native passthrough
            bitstream_enabled = MultichannelPcm ? (Ac3Passthrough ? 1 : 0) : Ac3Enabled ? 1 : 0,
            // multichannel PCM: nothing is transcoded, so let native AC3 and DTS both pass through
            bitstream_codec = BitstreamCodec == 1 || MultichannelPcm ? Native.PC_CODEC_DTS : Native.PC_CODEC_AC3,
            multichannel_pcm = MultichannelPcm ? 1 : 0,
            dts_bitrate_kbps = DtsBitrate,
            ac3_bitrate_kbps = Ac3Bitrate,
            passthrough = Ac3Passthrough ? 1 : 0,
            stereo_bitstream = StereoAsAC3 ? 1 : 0,
            audio_track = audioTrack,
            start_position = track.Start + Math.Max(0, start),   // cue tracks: offset into the file
            av_offset = AvOffsetMs / 1000,
        };
        PrepareSubtitlesForOpen(track);
        string uid = EffectiveDeviceUID;
        IntPtr uidPtr = uid.Length > 0 ? Marshal.StringToCoTaskMemUTF8(uid) : IntPtr.Zero;
        cfg.device_uid = uidPtr;
        var err = new byte[512];
        int rc;
        DebugTrace.Log($"play: open {track.Name}");
        fixed (byte* e = err) rc = Native.pc_player_open(_player, track.Path, ref cfg, paused ? 1 : 0, e, err.Length);
        DebugTrace.Log($"play: opened rc={rc}");
        if (uidPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(uidPtr);
        Current = track;
        if (rc != 0)
        {
            _isOpen = false;
            IsPlaying = false;
            fixed (byte* e = err) ErrorMessage = $"{track.Name}: {Native.Str(e, err.Length)}";
            ClearInfo();
            return;
        }
        _isOpen = true;
        IsPlaying = !paused;
        var resolved = uid.Length == 0 ? Devices.FirstOrDefault(d => d.IsDefault) : Devices.FirstOrDefault(d => d.Uid == uid);
        _openDeviceUid = resolved?.Uid;
        _openDeviceName = resolved?.Name;
        LoadInfo(track);
        Tick();
    }

    public void TogglePlay()
    {
        if (!_isOpen)
        {
            var t = Current ?? Playlist.FirstOrDefault();
            if (t != null) Play(t, _reopenPending ? Position : t == _resumeTrack ? _resumeAt : 0);
            return;
        }
        IsPlaying = !IsPlaying;
        Native.pc_player_set_paused(_player, IsPlaying ? 0 : 1);
        ScheduleSave();
    }

    public void Stop()
    {
        Interlocked.Increment(ref _generation);
        Native.pc_player_close(_player);
        _isOpen = false;
        _reopenPending = false;
        IsPlaying = false;
        Position = 0;
        Renderer?.Clear();
        ClearInfo();
    }

    public void Next()
    {
        if (Current == null) return;
        int i = Playlist.IndexOf(Current);
        if (i >= 0 && i + 1 < Playlist.Count) Play(Playlist[i + 1]);
        else Stop();
    }

    public void Previous()
    {
        if (Current == null) return;
        int i = Playlist.IndexOf(Current);
        if (Position > 3 || i <= 0) Seek(0);
        else Play(Playlist[i - 1]);
    }

    /// `seconds` is relative to the current item (a cue track's start).
    public void Seek(double seconds)
    {
        if (!_isOpen || Current == null) return;
        seconds = Math.Max(0, seconds);
        if (Duration > 0) seconds = Math.Min(seconds, Duration);
        Native.pc_player_seek(_player, Current.Start + seconds);
        Position = seconds;
    }

    public void Skip(double delta) => Seek(Math.Max(0, Position + delta));

    public void SelectAudioTrack(int index)
    {
        if (!_isOpen || Current == null || index == CurrentAudioTrack) return;
        Play(Current, Position, !IsPlaying, index);
    }

    /// A key: next audio track (wraps).
    public void CycleAudioTrack()
    {
        if (AudioTracks.Count > 1) SelectAudioTrack((CurrentAudioTrack + 1) % AudioTracks.Count);
    }

    /// Current track without its "N: " prefix, for the controls bar.
    public string CurrentAudioTrackLabel
    {
        get
        {
            if (CurrentAudioTrack < 0 || CurrentAudioTrack >= AudioTracks.Count) return "";
            var s = AudioTracks[CurrentAudioTrack];
            int c = s.IndexOf(": ", StringComparison.Ordinal);
            return c > 0 && c < 4 ? s[(c + 2)..] : s;
        }
    }

    /// Badge text when not bitstreaming: "PCM", or "PCM 5.1" for untranscoded multichannel.
    public string PcmBadge
    {
        get
        {
            if (!OutputDescription.Contains("multichannel", StringComparison.Ordinal)) return "PCM";
            var parts = OutputDescription.Split(' ');
            return parts.Length > 1 ? "PCM " + parts[1].Split('(')[0] : "PCM";
        }
    }

    public void Shutdown()
    {
        _timer.Stop();
        _saveTimer.Stop();
        SaveSession();
        Native.pc_set_device_change_callback(null, IntPtr.Zero);
        Native.pc_player_close(_player);
        Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
    }

    private void TrackFinished(int generation)
    {
        if (generation == Volatile.Read(ref _generation)) Next();
    }

    // ------------------------------------------------------------------ info

    // SURROUND_DEBUG=<file>: status line every second (like the macOS app's stderr log)
    private static readonly string? DebugLog = Environment.GetEnvironmentVariable("SURROUND_DEBUG");
    public Func<string>? VisDebug { get; set; }
    private int _debugTicks;

    private void Tick()
    {
        if (DebugLog != null && ++_debugTicks % 5 == 0)
        {
            try
            {
                File.AppendAllText(DebugLog,
                    $"[app] items={Playlist.Count} {Title} pos={Position:F2}/{Duration:F1} playing={IsPlaying} mode={OutputMode} " +
                    $"| {OutputDescription} | video: {VideoDescription} presented={Renderer?.FramesPresented ?? 0} " +
                    $"| {(ShowsVisualizer ? VisDebug?.Invoke() : "vis off")} " +
                    $"| err={ErrorMessage}\n");
            }
            catch { }
        }
        if (!_isOpen || Current == null) return;
        double abs = Native.pc_player_position(_player);
        // cue track boundary: the engine keeps playing the file, so the model moves on itself
        if (Current.End is double end && abs >= end - 0.02)
        {
            int i = Playlist.IndexOf(Current);
            var next = i >= 0 && i + 1 < Playlist.Count ? Playlist[i + 1] : null;
            if (next != null && Current.ContinuesInto(next))
            {
                Current = next;          // same file, next track: no reopen, no gap
                ApplyTrackInfo(next);
            }
            else
            {
                Next();
                return;
            }
        }
        if (IsScrubbing) return;
        Position = Math.Max(0, abs - Current.Start);
    }

    private double _fileDuration;   // engine duration of the open file

    /// Title/artist/album and duration for `track`: cue metadata over the file's tags.
    private void ApplyTrackInfo(Track track)
    {
        var cue = track.Cue;
        if (cue != null)
        {
            Title = cue.Title;
            if (cue.Performer.Length > 0) Artist = cue.Performer;
            if (cue.Album.Length > 0) Album = cue.Album;
        }
        double end = track.End ?? _fileDuration;
        Duration = Math.Max(0, end - track.Start);
    }

    private void ClearInfo()
    {
        Title = Artist = Album = SourceAudio = SourceFormat = OutputDescription = VideoDescription = BitstreamCodecName = "";
        OutputMode = -1;
        HasVideo = false;
        Artwork = null;
        AudioTracks = Array.Empty<string>();
        CurrentAudioTrack = -1;
        SubtitleTracks = Array.Empty<string>();
        CurrentSubtitleTrack = -1;
        Duration = 0;
    }

    private static readonly string[] ArtExtensions = { ".jpg", ".jpeg", ".png" };
    private static readonly string[] ArtNames = { "cover", "folder", "front", "album", "albumart", "albumartlarge" };
    private string? _folderArtPath;
    private byte[]? _folderArt;

    /// Artwork for a file without embedded art: a .jpg/.jpeg/.png in the same folder.
    /// Preference: an image named like the media file, then the usual names (cover, folder,
    /// front, ...), then names containing them ("Cover (front).jpg"), then the first image
    /// alphabetically. The last image read is cached, so an album reads it once.
    private byte[]? FolderArt(string mediaPath)
    {
        var pick = FindFolderArt(mediaPath);
        if (pick == null) return null;
        if (string.Equals(pick, _folderArtPath, StringComparison.OrdinalIgnoreCase)) return _folderArt;
        byte[]? bytes = null;
        try { if (new FileInfo(pick).Length <= 32 << 20) bytes = File.ReadAllBytes(pick); }
        catch { }
        _folderArtPath = pick;
        _folderArt = bytes;
        return bytes;
    }

    public static string? FindFolderArt(string mediaPath)
    {
        List<string> images;
        try
        {
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(mediaPath));
            if (dir == null) return null;
            images = Directory.EnumerateFiles(dir)
                .Where(f => ArtExtensions.Contains(System.IO.Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch { return null; }
        if (images.Count == 0) return null;
        static string Name(string f) => System.IO.Path.GetFileNameWithoutExtension(f);
        string stem = Name(mediaPath);
        return images.FirstOrDefault(f => Name(f).Equals(stem, StringComparison.OrdinalIgnoreCase))
            ?? ArtNames.Select(n => images.FirstOrDefault(f => Name(f).Equals(n, StringComparison.OrdinalIgnoreCase)))
                       .FirstOrDefault(f => f != null)
            ?? images.FirstOrDefault(f => ArtNames.Take(3).Any(n => Name(f).Contains(n, StringComparison.OrdinalIgnoreCase)))
            ?? images[0];
    }

    private unsafe void LoadInfo(Track track)
    {
        Native.pc_player_get_info(_player, out var info);
        string title = Native.Str(info.title, 256);
        Title = title.Length == 0 ? track.Name : title;
        Artist = Native.Str(info.artist, 256);
        Album = Native.Str(info.album, 256);
        SourceAudio = Native.Str(info.source_audio, 256);
        SourceFormat = FormatRate(info.source_rate, info.source_bits);
        OutputDescription = Native.Str(info.output, 512);
        VideoDescription = Native.Str(info.video, 256);
        OutputMode = info.mode;
        BitstreamCodecName = Native.Str(info.bitstream_codec, 32);
        HasVideo = info.has_video != 0;
        VideoWidth = info.video_width;
        VideoHeight = info.video_height;
        _fileDuration = info.duration;
        ApplyTrackInfo(track);
        var names = new List<string>();
        int count = Native.pc_player_audio_track_count(_player);
        var buf = new byte[256];
        for (int i = 0; i < count; i++)
            fixed (byte* b = buf) { Native.pc_player_audio_track_name(_player, i, b, 256); names.Add(Native.Str(b, 256)); }
        AudioTracks = names;
        CurrentAudioTrack = Native.pc_player_current_audio_track(_player);
        LoadSubtitles(track);
        IntPtr art = Native.pc_player_cover_art(_player, out int size);
        if (art != IntPtr.Zero && size > 0)
        {
            var bytes = new byte[size];
            Marshal.Copy(art, bytes, 0, size);
            Artwork = bytes;
        }
        else Artwork = FolderArt(track.Path);
        // the hardware/software decode note is only known once frames arrive
        int gen = _generation;
        var t = _ui.CreateTimer();
        t.Interval = TimeSpan.FromSeconds(1);
        t.IsRepeating = false;
        t.Tick += (_, _) =>
        {
            if (!_isOpen || gen != _generation) return;
            Native.pc_player_get_info(_player, out var i2);
            VideoDescription = Native.Str(i2.video, 256);
        };
        t.Start();
    }

    private void UpdateKeepAwake()
    {
        // keep the display on while playing video (like the macOS app's activity)
        Native.SetThreadExecutionState(IsPlaying && (HasVideo || ShowsVisualizer)
            ? Native.ES_CONTINUOUS | Native.ES_DISPLAY_REQUIRED | Native.ES_SYSTEM_REQUIRED
            : Native.ES_CONTINUOUS);
    }
}
