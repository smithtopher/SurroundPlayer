// Settings: port of SettingsView.swift (device, surround, sync). Changes apply at once,
// like the macOS app (the model reopens the current file at the same position).
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace SurroundPlayer;

public sealed class SettingsDialog : ContentDialog
{
    private readonly PlayerModel _m;
    private readonly ComboBox _device = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _deviceNote = Note("");
    private readonly ComboBox _codec = new() { Header = "Surround output", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _mcNote = Note(
        "Multichannel PCM: surround is decoded and sent as uncompressed PCM in its own channel layout and sample rate " +
        "(up to 7.1, 24-bit/192 kHz), with exclusive access, bit-for-bit, nothing re-encoded. Needs HDMI straight into " +
        "an AV receiver (or a TV that passes it on over HDMI eARC): optical/S/PDIF can't carry it, so a TV feeding an " +
        "amp over optical downmixes it to stereo. If the device refuses the layout, the player falls back to stereo PCM.");
    private readonly ComboBox _ac3Rate = new() { Header = "Dolby Digital bitrate", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _dtsRate = new() { Header = "DTS bitrate", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _passthrough = new();
    private readonly CheckBox _stereo = new() { Content = "Also encode stereo sources" };
    private readonly TextBlock _dtsNote = Note(
        "DTS: in this mode native DTS tracks (and the DTS core of DTS-HD) also pass through untouched. The DTS encoder is " +
        "FFmpeg's, which is marked experimental and is less refined than its AC3 encoder. Some TVs don't accept or forward " +
        "DTS; if the device refuses it the player falls back to PCM, and if the amp goes silent use Dolby Digital.");
    private readonly Slider _latency = new() { Minimum = -500, Maximum = 1000, StepFrequency = 10, Header = "Audio latency" };
    private readonly TextBlock _latencyText = new() { VerticalAlignment = VerticalAlignment.Center, Width = 64, TextAlignment = TextAlignment.Right };
    private bool _loading;

    // stereo upmix (port of the Mac's Settings > Stereo Upmix)
    private readonly ComboBox _upmixMode = new() { Header = "Upmix stereo to 5.1", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _upmixBlurb = Note("");
    private readonly StackPanel _upmixControls = new() { Spacing = 10 };
    private readonly CheckBox _upmixVideo = new() { Content = "Also for videos with stereo sound" };
    private readonly List<(Slider Slider, TextBlock Label, Func<double> Get, Func<double, string> Format)> _upmixSliders = new();
    private readonly ComboBox _upmixXover = new() { Header = "Bass crossover", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _upmixMove = new() { Header = "Bass to subwoofer", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _upmixHighcut = new() { Header = "Rear high-cut", HorizontalAlignment = HorizontalAlignment.Stretch };
    private static readonly int[] Crossovers = { 60, 80, 100, 120, 150, 200, 250 };
    private static readonly int[] Highcuts = { 0, 4000, 6000, 7000, 8000, 10000, 12000, 16000 };

    public SettingsDialog(PlayerModel model, XamlRoot root)
    {
        _m = model;
        XamlRoot = root;
        Title = "Settings";
        CloseButtonText = "Done";
        DefaultButton = ContentDialogButton.Close;

        _codec.Items.Add("Dolby Digital bitstream (AC3, transcoded)");
        _codec.Items.Add("DTS bitstream (transcoded)");
        _codec.Items.Add("Multichannel PCM (no transcoding, HDMI to an AV receiver)");
        _codec.Items.Add("Off (stereo PCM)");
        foreach (var s in new[] { "384 kb/s", "448 kb/s (DVD)", "640 kb/s (Blu-ray, best)" }) _ac3Rate.Items.Add(s);
        foreach (var s in new[] { "1509 kb/s (full rate, best)", "754 kb/s (half rate)" }) _dtsRate.Items.Add(s);

        var panel = new StackPanel { Spacing = 10, Width = 480 };
        panel.Children.Add(Section("Audio Device"));
        panel.Children.Add(_device);
        panel.Children.Add(_deviceNote);
        panel.Children.Add(Section("Surround"));
        panel.Children.Add(_codec);
        panel.Children.Add(_mcNote);
        panel.Children.Add(_ac3Rate);
        panel.Children.Add(_dtsRate);
        panel.Children.Add(_passthrough);
        panel.Children.Add(_stereo);
        panel.Children.Add(Note(
            "Multichannel audio (E-AC3, DTS-HD, TrueHD, AAC, FLAC, …) is decoded, downmixed to 5.1 when needed and re-encoded " +
            "to the chosen format, then sent as an IEC 61937 bitstream with exclusive access to the device (WASAPI exclusive " +
            "mode, as Kodi does). The TV only has to forward it over optical to the amp. Stereo sources play as PCM unless " +
            "the option above is on; turn it on if your amp drops out when switching between PCM and surround."));
        panel.Children.Add(_dtsNote);
        BuildUpmix(panel);
        BuildTone(panel);
        panel.Children.Add(Section("Visualiser"));
        var vis = new ToggleSwitch { Header = "Show visualiser for music", IsOn = model.VisualizerEnabled };
        vis.Toggled += (_, _) => _m.VisualizerEnabled = vis.IsOn;
        panel.Children.Add(vis);
        panel.Children.Add(Note(
            "A MilkDrop-style feedback visualiser that reacts to the audio actually leaving the speakers. When on, it also " +
            "replaces the picture of files with a video track. Keys: V toggle, ] / [ next / previous preset, L lock preset, " +
            "F full screen."));
        panel.Children.Add(Section("Sync"));
        var lat = new Grid { ColumnSpacing = 8 };
        lat.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        lat.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        lat.Children.Add(_latency);
        Grid.SetColumn(_latencyText, 1);
        lat.Children.Add(_latencyText);
        panel.Children.Add(lat);
        panel.Children.Add(Note("If sound arrives after the picture (the amp's decoding delay), raise this to delay the video by the same amount."));
        var v = typeof(SettingsDialog).Assembly.GetName().Version;
        var about = Note($"Surround Player {v?.ToString(3)}");
        about.Margin = new Thickness(0, 12, 0, 0);
        panel.Children.Add(about);
        Content = new ScrollViewer { Content = panel, MaxHeight = 640 };

        Load();
        _device.SelectionChanged += (_, _) =>
        {
            if (_loading || _device.SelectedIndex < 0) return;
            _m.DeviceUID = _device.SelectedIndex == 0 ? "" : _m.Devices[_device.SelectedIndex - 1].Uid;
            Load();
        };
        _codec.SelectionChanged += (_, _) =>
        {
            if (_loading || _codec.SelectedIndex < 0) return;
            switch (_codec.SelectedIndex)
            {
                case 0 or 1:
                    _m.MultichannelPcm = false;
                    _m.Ac3Enabled = true;
                    _m.BitstreamCodec = _codec.SelectedIndex;
                    break;
                case 2:
                    _m.MultichannelPcm = true;
                    break;
                default:
                    _m.MultichannelPcm = false;
                    _m.Ac3Enabled = false;
                    break;
            }
            Load();
        };
        _ac3Rate.SelectionChanged += (_, _) => { if (!_loading) _m.Ac3Bitrate = new[] { 384, 448, 640 }[_ac3Rate.SelectedIndex]; };
        _dtsRate.SelectionChanged += (_, _) => { if (!_loading) _m.DtsBitrate = _dtsRate.SelectedIndex == 1 ? 754 : 1509; };
        _passthrough.Click += (_, _) => _m.Ac3Passthrough = _passthrough.IsChecked == true;
        _stereo.Click += (_, _) => _m.StereoAsAC3 = _stereo.IsChecked == true;
        _latency.ValueChanged += (_, e) =>
        {
            _latencyText.Text = $"{(int)e.NewValue} ms";
            if (!_loading) _m.AvOffsetMs = e.NewValue;
        };
    }

    private void Load()
    {
        _loading = true;
        _device.Items.Clear();
        _device.Items.Add("System Default");
        foreach (var d in _m.Devices) _device.Items.Add($"{d.Name}{(d.IsHdmi ? " (HDMI)" : "")} — {d.Channels} ch");
        int sel = _m.DeviceUID.Length == 0 ? 0 : _m.Devices.ToList().FindIndex(d => d.Uid == _m.DeviceUID) + 1;
        _device.SelectedIndex = sel;
        var dev = _m.SelectedDevice;
        _deviceNote.Text = dev == null ? "" : dev.CanBitstream
            ? $"{dev.Name} is a digital output: Dolby Digital / DTS bitstreaming is available."
            : $"{dev.Name} is not a digital output, so PCM is always used (bitstream would play as noise).";
        bool mc = _m.MultichannelPcm, off = !mc && !_m.Ac3Enabled;
        _codec.SelectedIndex = mc ? 2 : off ? 3 : _m.BitstreamCodec == 1 ? 1 : 0;
        _ac3Rate.SelectedIndex = _m.Ac3Bitrate switch { 384 => 0, 448 => 1, _ => 2 };
        _dtsRate.SelectedIndex = _m.DtsBitrate < 1000 ? 1 : 0;
        bool dts = !mc && !off && _m.BitstreamCodec == 1, ac3 = !mc && !off && !dts;
        _mcNote.Visibility = mc ? Visibility.Visible : Visibility.Collapsed;
        _ac3Rate.Visibility = ac3 ? Visibility.Visible : Visibility.Collapsed;
        _dtsRate.Visibility = dts ? Visibility.Visible : Visibility.Collapsed;
        _dtsNote.Visibility = dts ? Visibility.Visible : Visibility.Collapsed;
        _stereo.Visibility = mc || off ? Visibility.Collapsed : Visibility.Visible;
        _passthrough.Visibility = off ? Visibility.Collapsed : Visibility.Visible;
        _passthrough.Content = ac3 ? "Pass native Dolby Digital tracks through untouched"
                                   : "Pass native Dolby Digital and DTS tracks through untouched";
        _passthrough.IsChecked = _m.Ac3Passthrough;
        _stereo.IsChecked = _m.StereoAsAC3;
        _latency.Value = _m.AvOffsetMs;
        _latencyText.Text = $"{(int)_m.AvOffsetMs} ms";
        LoadUpmix();
        _loading = false;
    }

    // ------------------------------------------------------------------ stereo upmix

    private void BuildUpmix(StackPanel panel)
    {
        panel.Children.Add(Section("Stereo Upmix"));
        foreach (var t in PlayerModel.UpmixModeTitles) _upmixMode.Items.Add(t);
        panel.Children.Add(_upmixMode);
        panel.Children.Add(_upmixBlurb);
        _upmixControls.Children.Add(_upmixVideo);
        // dB sliders: the left end (-20) means off (-40 in the engine)
        AddSlider("Surround level", -20, 6, 0.5, () => Math.Max(-20, _m.UpmixSurroundDb), v => _m.UpmixSurroundDb = v <= -20 ? -40 : v,
                  v => v <= -20 ? "Off" : $"{v:+0.0;-0.0} dB");
        AddSlider("Centre focus", 0, 1, 0.05, () => _m.UpmixCenter, v => _m.UpmixCenter = v,
                  v => v < 0.05 ? "Phantom" : v > 0.95 ? "Locked" : $"{v * 100:0}%");
        AddSlider("Front width", 0, 1, 0.05, () => _m.UpmixWidth, v => _m.UpmixWidth = v,
                  v => Math.Abs(v - 0.5) < 0.01 ? "As recorded" : $"{v * 100:0}%");
        AddSlider("Subwoofer (LFE)", -20, 6, 0.5, () => Math.Max(-20, _m.UpmixLfeDb), v => _m.UpmixLfeDb = v <= -20 ? -40 : v,
                  v => v <= -20 ? "Off" : $"{v:+0.0;-0.0} dB");

        var adv = new StackPanel { Spacing = 10 };
        foreach (var hz in Crossovers) _upmixXover.Items.Add($"{hz} Hz");
        _upmixMove.Items.Add("Copy (main speakers keep it)");
        _upmixMove.Items.Add("Move (small main speakers)");
        foreach (var hz in Highcuts) _upmixHighcut.Items.Add(hz == 0 ? "Off" : $"{hz / 1000} kHz");
        adv.Children.Add(_upmixXover);
        adv.Children.Add(_upmixMove);
        AddSlider("Rear delay", 0, 30, 1, () => _m.UpmixRearDelayMs, v => _m.UpmixRearDelayMs = v,
                  v => v < 0.5 ? "Off" : $"{v:0} ms", adv);
        adv.Children.Add(_upmixHighcut);
        adv.Children.Add(Note(
            "Crossover: bass below it goes to the subwoofer channel (changing it causes a short dropout). Most AV receivers " +
            "already send the speakers' bass to the subwoofer, so a strong LFE level can double the bass. Rear delay keeps " +
            "sounds anchored at the front; the high-cut makes the rears read as room sound rather than as speakers you can point at."));
        _upmixControls.Children.Add(new Expander
        {
            Header = "Advanced", Content = adv,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        });
        _upmixControls.Children.Add(Note("Changes apply while playing. U turns upmix on and off. The output badge shows UPMIX while it is active."));
        panel.Children.Add(_upmixControls);

        _upmixMode.SelectionChanged += (_, _) =>
        {
            if (_loading || _upmixMode.SelectedIndex < 0) return;
            _m.UpmixMode = (UpmixMode)_upmixMode.SelectedIndex;
            Load();
        };
        _upmixVideo.Click += (_, _) => _m.UpmixIncludeVideo = _upmixVideo.IsChecked == true;
        _upmixXover.SelectionChanged += (_, _) => { if (!_loading && _upmixXover.SelectedIndex >= 0) { _m.UpmixCrossoverHz = Crossovers[_upmixXover.SelectedIndex]; LoadUpmixMode(); } };
        _upmixMove.SelectionChanged += (_, _) => { if (!_loading && _upmixMove.SelectedIndex >= 0) { _m.UpmixLfeMove = _upmixMove.SelectedIndex == 1; LoadUpmixMode(); } };
        _upmixHighcut.SelectionChanged += (_, _) => { if (!_loading && _upmixHighcut.SelectedIndex >= 0) { _m.UpmixRearHighcutHz = Highcuts[_upmixHighcut.SelectedIndex]; LoadUpmixMode(); } };
    }

    // ------------------------------------------------------------------ tone

    private readonly StackPanel _tone = new() { Spacing = 10 };

    private void BuildTone(StackPanel panel)
    {
        panel.Children.Add(Section("Tone"));
        AddSlider("Bass boost (100 Hz)", 0, 12, 0.5, () => _m.BassDb, v => _m.BassDb = v,
                  v => v < 0.25 ? "Off" : $"+{v:0.0} dB", _tone);
        AddSlider("Treble boost (6 kHz)", 0, 12, 0.5, () => _m.TrebleDb, v => _m.TrebleDb = v,
                  v => v < 0.25 ? "Off" : $"+{v:0.0} dB", _tone);
        _tone.Children.Add(Note(
            "Turn both up for listening at low volume: quiet sound loses bass first, and treble too, to the ear (the " +
            "Fletcher-Munson curves). Works on everything the player decodes: stereo, upmixed stereo and native 5.1/7.1, " +
            "whatever the output. Tracks passed through untouched as Dolby Digital or DTS can't be changed; turn " +
            "pass-through off above to apply it to them too. Changes apply while playing."));
        panel.Children.Add(_tone);
    }

    private void AddSlider(string title, double min, double max, double step, Func<double> get, Action<double> set,
                           Func<double, string> format, StackPanel? into = null)
    {
        var slider = new Slider { Header = title, Minimum = min, Maximum = max, StepFrequency = step, IsThumbToolTipEnabled = false };
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 8), Width = 84, TextAlignment = TextAlignment.Right };
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(slider);
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        (into ?? _upmixControls).Children.Add(row);
        _upmixSliders.Add((slider, label, get, format));
        slider.ValueChanged += (_, e) =>
        {
            label.Text = format(e.NewValue);
            if (_loading) return;
            set(e.NewValue);
            LoadUpmixMode();   // moving a slider turns a preset into Custom
        };
    }

    private void LoadUpmix()
    {
        LoadUpmixMode();
        bool wasLoading = _loading;
        _loading = true;
        _upmixVideo.IsChecked = _m.UpmixIncludeVideo;
        foreach (var (slider, label, get, format) in _upmixSliders)
        {
            slider.Value = get();
            label.Text = format(slider.Value);
        }
        _upmixXover.SelectedIndex = Math.Max(0, Array.IndexOf(Crossovers, _m.UpmixCrossoverHz));
        _upmixMove.SelectedIndex = _m.UpmixLfeMove ? 1 : 0;
        _upmixHighcut.SelectedIndex = Math.Max(0, Array.IndexOf(Highcuts, _m.UpmixRearHighcutHz));
        _loading = wasLoading;
    }

    /// The mode box and what depends on it (a slider change can turn a preset into Custom).
    private void LoadUpmixMode()
    {
        bool wasLoading = _loading;
        _loading = true;
        _upmixMode.SelectedIndex = (int)_m.UpmixMode;
        bool on = _m.UpmixMode != UpmixMode.Off;
        _upmixBlurb.Text = PlayerModel.UpmixModeBlurbs[(int)_m.UpmixMode] + (_m.UpmixAvailable ? ""
            : " Needs a surround output above (Dolby Digital, DTS or Multichannel PCM).");
        _upmixControls.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _upmixControls.IsHitTestVisible = _m.UpmixAvailable;
        _upmixControls.Opacity = _m.UpmixAvailable ? 1 : 0.5;
        // with upmix on, stereo is always encoded as 5.1, so "Also encode stereo sources" is moot
        _stereo.IsEnabled = !on;
        _loading = wasLoading;
    }

    private static TextBlock Section(string text) =>
        new() { Text = text, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(0, 8, 0, 0) };

    private static TextBlock Note(string text) =>
        new()
        {
            Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
}
