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
    private readonly ToggleSwitch _bitstream = new() { Header = "Bitstream surround (Dolby Digital / DTS)" };
    private readonly ComboBox _codec = new() { Header = "Transcode surround to", HorizontalAlignment = HorizontalAlignment.Stretch };
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

        _codec.Items.Add("Dolby Digital (AC3)");
        _codec.Items.Add("DTS");
        foreach (var s in new[] { "384 kb/s", "448 kb/s (DVD)", "640 kb/s (Blu-ray, best)" }) _ac3Rate.Items.Add(s);
        foreach (var s in new[] { "1509 kb/s (full rate, best)", "754 kb/s (half rate)" }) _dtsRate.Items.Add(s);

        var panel = new StackPanel { Spacing = 10, Width = 480 };
        panel.Children.Add(Section("Audio Device"));
        panel.Children.Add(_device);
        panel.Children.Add(_deviceNote);
        panel.Children.Add(Section("Surround"));
        panel.Children.Add(_bitstream);
        panel.Children.Add(_codec);
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
        panel.Children.Add(Section("Sync"));
        var lat = new Grid { ColumnSpacing = 8 };
        lat.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        lat.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        lat.Children.Add(_latency);
        Grid.SetColumn(_latencyText, 1);
        lat.Children.Add(_latencyText);
        panel.Children.Add(lat);
        panel.Children.Add(Note("If sound arrives after the picture (the amp's decoding delay), raise this to delay the video by the same amount."));
        Content = new ScrollViewer { Content = panel, MaxHeight = 640 };

        Load();
        _device.SelectionChanged += (_, _) =>
        {
            if (_loading || _device.SelectedIndex < 0) return;
            _m.DeviceUID = _device.SelectedIndex == 0 ? "" : _m.Devices[_device.SelectedIndex - 1].Uid;
            Load();
        };
        _bitstream.Toggled += (_, _) => { if (!_loading) { _m.Ac3Enabled = _bitstream.IsOn; Load(); } };
        _codec.SelectionChanged += (_, _) => { if (!_loading) { _m.BitstreamCodec = _codec.SelectedIndex; Load(); } };
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
        _bitstream.IsOn = _m.Ac3Enabled;
        _codec.SelectedIndex = _m.BitstreamCodec == 1 ? 1 : 0;
        _ac3Rate.SelectedIndex = _m.Ac3Bitrate switch { 384 => 0, 448 => 1, _ => 2 };
        _dtsRate.SelectedIndex = _m.DtsBitrate < 1000 ? 1 : 0;
        bool dts = _m.BitstreamCodec == 1;
        _ac3Rate.Visibility = dts ? Visibility.Collapsed : Visibility.Visible;
        _dtsRate.Visibility = dts ? Visibility.Visible : Visibility.Collapsed;
        _dtsNote.Visibility = dts ? Visibility.Visible : Visibility.Collapsed;
        _passthrough.Content = dts ? "Pass native Dolby Digital and DTS tracks through untouched"
                                   : "Pass native Dolby Digital tracks through untouched";
        _passthrough.IsChecked = _m.Ac3Passthrough;
        _stereo.IsChecked = _m.StereoAsAC3;
        foreach (var c in new Control[] { _codec, _ac3Rate, _dtsRate, _passthrough, _stereo }) c.IsEnabled = _m.Ac3Enabled;
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
