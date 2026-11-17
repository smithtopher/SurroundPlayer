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

    public Track(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileNameWithoutExtension(path);
        Folder = new DirectoryInfo(System.IO.Path.GetDirectoryName(path) ?? "").Name;
        Ext = System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        // last 4 path components without extension, case/diacritic-folded (like the Mac app)
        var noExt = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path) ?? "", Name);
        var parts = noExt.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        SearchText = Fold(string.Join(" ", parts.Skip(Math.Max(0, parts.Length - 4))));
    }

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

public sealed class PlayerModel : Observable
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
        _player = Native.pc_player_create();
        _videoCb = (ctx, frame, pts) => Renderer?.Present(frame);
        _eventCb = (ctx, ev, msg) =>
        {
            int gen = Volatile.Read(ref _generation);
            if (ev == Native.PC_EVENT_EOF) _ui.TryEnqueue(() => TrackFinished(gen));
        };
        _deviceCb = ctx => _ui.TryEnqueue(RefreshDevices);
        Native.pc_player_set_callbacks(_player, _videoCb, _eventCb, IntPtr.Zero);
        Native.pc_set_device_change_callback(_deviceCb, IntPtr.Zero);
        RefreshDevices();
        _timer = ui.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(200);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
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
        IsScanning = true;
        var list = paths.ToList();
        var files = await Task.Run(() => Expand(list));
        IsScanning = false;
        if (files.Count == 0) return;
        int at = Math.Clamp(insertAt ?? Playlist.Count, 0, Playlist.Count);
        var first = new Track(files[0]);
        Playlist.Insert(at, first);
        for (int i = 1; i < files.Count; i++) Playlist.Insert(at + i, new Track(files[i]));
        if (playFirst) Play(first);
    }

    public static List<string> Expand(IEnumerable<string> paths)
    {
        var files = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                var found = new List<string>();
                try
                {
                    var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true,
                                                        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
                    foreach (var f in Directory.EnumerateFiles(p, "*", opts))
                        if (MediaExtensions.Contains(System.IO.Path.GetExtension(f).TrimStart('.'))) found.Add(f);
                }
                catch { }
                found.Sort(Native.StrCmpLogicalW);   // Explorer's natural order
                files.AddRange(found);
            }
            else if (File.Exists(p)) files.Add(p);
        }
        return files;
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

    public unsafe void Play(Track track, double start = 0, bool paused = false, int audioTrack = -1)
    {
        Interlocked.Increment(ref _generation);
        if (Current != track) Renderer?.Clear();
        ErrorMessage = null;

        var cfg = new Native.PCConfig
        {
            bitstream_enabled = Ac3Enabled ? 1 : 0,
            bitstream_codec = BitstreamCodec == 1 ? Native.PC_CODEC_DTS : Native.PC_CODEC_AC3,
            dts_bitrate_kbps = DtsBitrate,
            ac3_bitrate_kbps = Ac3Bitrate,
            passthrough = Ac3Passthrough ? 1 : 0,
            stereo_bitstream = StereoAsAC3 ? 1 : 0,
            audio_track = audioTrack,
            start_position = start,
            av_offset = AvOffsetMs / 1000,
        };
        string uid = EffectiveDeviceUID;
        IntPtr uidPtr = uid.Length > 0 ? Marshal.StringToCoTaskMemUTF8(uid) : IntPtr.Zero;
        cfg.device_uid = uidPtr;
        var err = new byte[512];
        int rc;
        fixed (byte* e = err) rc = Native.pc_player_open(_player, track.Path, ref cfg, paused ? 1 : 0, e, err.Length);
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
            if (t != null) Play(t, _reopenPending ? Position : 0);
            return;
        }
        IsPlaying = !IsPlaying;
        Native.pc_player_set_paused(_player, IsPlaying ? 0 : 1);
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

    public void Seek(double seconds)
    {
        if (!_isOpen) return;
        Native.pc_player_seek(_player, seconds);
        Position = seconds;
    }

    public void Skip(double delta) => Seek(Math.Max(0, Position + delta));

    public void SelectAudioTrack(int index)
    {
        if (!_isOpen || Current == null || index == CurrentAudioTrack) return;
        Play(Current, Position, !IsPlaying, index);
    }

    public void Shutdown()
    {
        _timer.Stop();
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
                    $"| err={ErrorMessage}\n");
            }
            catch { }
        }
        if (!_isOpen || IsScrubbing) return;
        Position = Native.pc_player_position(_player);
    }

    private void ClearInfo()
    {
        Title = Artist = Album = SourceAudio = OutputDescription = VideoDescription = BitstreamCodecName = "";
        OutputMode = -1;
        HasVideo = false;
        Artwork = null;
        AudioTracks = Array.Empty<string>();
        CurrentAudioTrack = -1;
        Duration = 0;
    }

    private unsafe void LoadInfo(Track track)
    {
        Native.pc_player_get_info(_player, out var info);
        string title = Native.Str(info.title, 256);
        Title = title.Length == 0 ? track.Name : title;
        Artist = Native.Str(info.artist, 256);
        Album = Native.Str(info.album, 256);
        SourceAudio = Native.Str(info.source_audio, 256);
        OutputDescription = Native.Str(info.output, 512);
        VideoDescription = Native.Str(info.video, 256);
        OutputMode = info.mode;
        BitstreamCodecName = Native.Str(info.bitstream_codec, 32);
        HasVideo = info.has_video != 0;
        Duration = info.duration;
        var names = new List<string>();
        int count = Native.pc_player_audio_track_count(_player);
        var buf = new byte[256];
        for (int i = 0; i < count; i++)
            fixed (byte* b = buf) { Native.pc_player_audio_track_name(_player, i, b, 256); names.Add(Native.Str(b, 256)); }
        AudioTracks = names;
        CurrentAudioTrack = Native.pc_player_current_audio_track(_player);
        IntPtr art = Native.pc_player_cover_art(_player, out int size);
        if (art != IntPtr.Zero && size > 0)
        {
            var bytes = new byte[size];
            Marshal.Copy(art, bytes, 0, size);
            Artwork = bytes;
        }
        else Artwork = null;
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
        Native.SetThreadExecutionState(IsPlaying && HasVideo
            ? Native.ES_CONTINUOUS | Native.ES_DISPLAY_REQUIRED | Native.ES_SYSTEM_REQUIRED
            : Native.ES_CONTINUOUS);
    }
}
