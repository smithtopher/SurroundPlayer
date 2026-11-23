// Stereo upmix to 5.1 (port of the Mac app's Upmix.swift): presets, the user's settings
// (settings.json "upmix*" keys, same names as the Mac's UserDefaults) and pushing them to the
// engine (pc_upmix.c does the work). Sliders apply live; on/off and "include video" reopen.
// Tone (bass + treble boost) travels in the same engine struct but isn't part of the upmix: it
// applies to everything that's decoded (upmixed, native multichannel, stereo), and not to tracks
// passed through as Dolby Digital / DTS.
using System;

namespace SurroundPlayer;

public sealed partial class PlayerModel
{
    public static readonly string[] UpmixModeTitles = { "Off", "Music", "Movies & TV", "Wide", "Custom" };
    public static readonly string[] UpmixModeBlurbs =
    {
        "Stereo stays stereo.",
        "Gentle room sound in the rears; vocals stay between the front speakers.",
        "Dialogue locked to the centre speaker, ambience behind you.",
        "Bigger, more enveloping sound with stronger rears and a wider front.",
        "Your own settings.",
    };

    private bool _upmixActive;
    /// The open file is being upmixed right now (stereo source, surround output, upmix on).
    public bool UpmixActive { get => _upmixActive; private set => Set(ref _upmixActive, value); }

    /// Upmix only acts when the output is surround (Dolby Digital, DTS or multichannel PCM).
    public bool UpmixAvailable => MultichannelPcm || Ac3Enabled;

    public UpmixMode UpmixMode
    {
        get => (UpmixMode)Math.Clamp(_settings.upmixMode, 0, 4);
        set
        {
            if (value == UpmixMode) return;
            bool wasOn = UpmixMode != UpmixMode.Off;
            UpmixProfiles.SetMode(_settings, value);   // presets set the sliders; Custom keeps its own
            UpmixChanged(reopen: wasOn != (value != UpmixMode.Off));
        }
    }

    public bool UpmixIncludeVideo
    {
        get => _settings.upmixVideo;
        set { if (value != _settings.upmixVideo) { _settings.upmixVideo = value; UpmixChanged(reopen: UpmixMode != UpmixMode.Off); } }
    }

    public double UpmixSurroundDb { get => _settings.upmixSurroundDb; set => SetUpmixValue(() => _settings.upmixSurroundDb = value, _settings.upmixSurroundDb != value); }
    public double UpmixCenter { get => _settings.upmixCenter; set => SetUpmixValue(() => _settings.upmixCenter = value, _settings.upmixCenter != value); }
    public double UpmixWidth { get => _settings.upmixWidth; set => SetUpmixValue(() => _settings.upmixWidth = value, _settings.upmixWidth != value); }
    public double UpmixLfeDb { get => _settings.upmixLfeDb; set => SetUpmixValue(() => _settings.upmixLfeDb = value, _settings.upmixLfeDb != value); }
    public int UpmixCrossoverHz { get => _settings.upmixCrossoverHz; set => SetUpmixValue(() => _settings.upmixCrossoverHz = value, _settings.upmixCrossoverHz != value); }
    public bool UpmixLfeMove { get => _settings.upmixLfeMove; set => SetUpmixValue(() => _settings.upmixLfeMove = value, _settings.upmixLfeMove != value); }
    public double UpmixRearDelayMs { get => _settings.upmixRearDelayMs; set => SetUpmixValue(() => _settings.upmixRearDelayMs = value, _settings.upmixRearDelayMs != value); }
    public int UpmixRearHighcutHz { get => _settings.upmixRearHighcutHz; set => SetUpmixValue(() => _settings.upmixRearHighcutHz = value, _settings.upmixRearHighcutHz != value); }

    // tone: kept out of the presets, so changing it leaves the mode alone; applies live
    public double BassDb { get => _settings.upmixBassDb; set => SetTone(() => _settings.upmixBassDb = value, _settings.upmixBassDb != value); }
    public double TrebleDb { get => _settings.upmixTrebleDb; set => SetTone(() => _settings.upmixTrebleDb = value, _settings.upmixTrebleDb != value); }

    private void SetTone(Action assign, bool changed)
    {
        if (!changed) return;
        assign();
        _settings.Save();
        PushUpmix();
        Raise(nameof(UpmixSettings));
    }

    /// Moving a setting turns a preset into Custom (the Mac does the same).
    private void SetUpmixValue(Action assign, bool changed)
    {
        if (!changed) return;
        assign();
        UpmixProfiles.ValueChanged(_settings);   // a preset becomes Custom; Custom remembers the values
        UpmixChanged(reopen: false);
    }

    private void UpmixChanged(bool reopen)
    {
        _settings.Save();
        PushUpmix();
        Raise(nameof(UpmixMode));
        Raise(nameof(UpmixSettings));
        // on/off changes the output format: reopen the current item at the same position
        if (reopen && _isOpen && Current != null) Play(Current, Position, !IsPlaying, CurrentAudioTrack);
    }

    /// Raised for any slider change (the settings dialog refreshes its labels).
    public object? UpmixSettings => null;

    private void PushUpmix()
    {
        var u = new Native.PCUpmix
        {
            enabled = UpmixMode != UpmixMode.Off ? 1 : 0,
            include_video = _settings.upmixVideo ? 1 : 0,
            surround_db = (float)_settings.upmixSurroundDb,
            center = (float)_settings.upmixCenter,
            width = (float)_settings.upmixWidth,
            lfe_db = (float)_settings.upmixLfeDb,
            crossover_hz = _settings.upmixCrossoverHz,
            lfe_move = _settings.upmixLfeMove ? 1 : 0,
            bass_db = (float)_settings.upmixBassDb,
            rear_delay_ms = (float)_settings.upmixRearDelayMs,
            rear_highcut_hz = _settings.upmixRearHighcutHz,
            treble_db = (float)_settings.upmixTrebleDb,
        };
        Native.pc_player_set_upmix(_player, ref u);
    }

    /// U key: upmix on (the last mode used) or off.
    public void ToggleUpmix()
    {
        if (UpmixMode != UpmixMode.Off)
        {
            _settings.upmixLastMode = (int)UpmixMode;
            UpmixMode = UpmixMode.Off;   // leaving Custom saves its values
        }
        else
        {
            // back on in the last mode (Custom comes back with its saved values)
            var last = (UpmixMode)Math.Clamp(_settings.upmixLastMode, 0, 4);
            UpmixMode = last == UpmixMode.Off ? UpmixMode.Music : last;
        }
    }
}
