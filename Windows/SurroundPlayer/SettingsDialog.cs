// Settings: port of SettingsView.swift (device, surround, sync). Changes apply at once,
// like the macOS app (the model reopens the current file at the same position).
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
        _loading = false;
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
