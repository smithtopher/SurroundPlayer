// Main window: port of ContentView.swift (stage, controls bar, playlist with search,
// device picker, output badge), the single-key shortcuts and full screen.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace SurroundPlayer;

public sealed partial class MainWindow : Window
{
    public PlayerModel Model { get; }
    private readonly VideoRenderer? _renderer;
    private readonly DispatcherQueueTimer _hideTimer;
    private bool _fullScreen, _controlsHidden, _cursorHidden, _updatingSlider, _scrubbing;
    private string[] _terms = Array.Empty<string>();
    private List<Track>? _filtered;
    private Visualizer.VisualizerRenderer? _vis;
    private bool _visAttached, _visRendering, _visFailed;
    private readonly DispatcherQueueTimer _titleTimer, _presetTimer;

    public MainWindow(string[] files)
    {
        DebugTrace.Log("window: xaml");
        InitializeComponent();
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);

        DebugTrace.Log("window: model");
        Model = new PlayerModel(DispatcherQueue);
        PlaceWindow();
        DebugTrace.Log("window: renderer");
        try
        {
            // decoded video lands on our device, so it must exist before the first file opens
            _renderer = new VideoRenderer();
            Native.pc_set_d3d11_device(_renderer.DevicePointer);
            Model.Renderer = _renderer;
        }
        catch (Exception e)
        {
            Model.ErrorMessage = "Video output unavailable: " + e.Message;
        }
        VideoPanel.Loaded += (_, _) =>
        {
            DebugTrace.Log("panel loaded: attach");
            try { _renderer?.Attach(VideoPanel); } catch (Exception e) { Model.ErrorMessage = "Video: " + e.Message; }
            DebugTrace.Log("panel attached");
        };

        Model.PropertyChanged += Model_PropertyChanged;
        _subTimer = DispatcherQueue.CreateTimer();
        _subTimer.Interval = TimeSpan.FromMilliseconds(40);
        _subTimer.Tick += (_, _) => { if (Model.PollSubtitle() is { } f) ShowSubtitle(f); };
        _subTimer.Start();
        Stage.SizeChanged += (_, _) => LayoutSubtitle();
        Model.Playlist.CollectionChanged += (_, _) => RefreshPlaylistView();
        Model.Devices.CollectionChanged += (_, _) => UpdateDeviceButton();
        PlaylistView.ItemsSource = Model.Playlist;

        PositionSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => BeginScrub()), true);
        Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Root_PointerPressedAnywhere), true);
        PositionSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => EndScrub()), true);
        PositionSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => EndScrub()), true);
        PositionSlider.ValueChanged += (_, e) => { if (!_updatingSlider) PositionText.Text = TimeString(e.NewValue); };

        _hideTimer = DispatcherQueue.CreateTimer();
        _hideTimer.Interval = TimeSpan.FromSeconds(3);
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) => HideControls();

        // visualiser overlays fade (MilkDrop-style: title on track change, preset name on change)
        VisTitle.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(900) };
        VisPresetPill.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(400) };
        _titleTimer = DispatcherQueue.CreateTimer();
        _titleTimer.Interval = TimeSpan.FromSeconds(5);
        _titleTimer.IsRepeating = false;
        _titleTimer.Tick += (_, _) => VisTitle.Opacity = 0;
        _presetTimer = DispatcherQueue.CreateTimer();
        _presetTimer.Interval = TimeSpan.FromSeconds(2.5);
        _presetTimer.IsRepeating = false;
        _presetTimer.Tick += (_, _) => { if (!Model.VisLocked) VisPresetPill.Opacity = 0; };
        VisToggle.IsChecked = Model.VisualizerEnabled;
        Model.SyncVideo();
        Model.VisDebug = () => _vis?.DebugStatus ?? "vis unavailable";

        Root.DragLeave += (_, _) => DropHighlight.Visibility = Visibility.Collapsed;
        Closed += (_, _) =>
        {
            SaveWindowSize();
            ShowCursor();
            StopVisRendering();
            Model.Shutdown();
            _renderer?.Dispose();
            _vis?.Dispose();
        };

        UpdateAll();
        UpdateVisualizer();
        DebugTrace.Log("window: ready");
        if (files.Length > 0) Model.Add(files, playFirst: true);
    }

    // ------------------------------------------------------------------ window size

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);

    private double DpiScale => GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;

    /// AppWindow sizes are physical pixels: scale the size (in DIPs, last used or 1280x800)
    /// by the display's DPI, keep it within the work area and centre it.
    private void PlaceWindow()
    {
        double s = DpiScale > 0 ? DpiScale : 1;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var st = Model.Settings;
        double wDip = st.windowWidth >= 760 ? st.windowWidth : 1280, hDip = st.windowHeight >= 480 ? st.windowHeight : 800;
        int w = Math.Min((int)(wDip * s), (int)(area.Width * 0.95));
        int h = Math.Min((int)(hDip * s), (int)(area.Height * 0.95));
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
    }

    private void SaveWindowSize()
    {
        if (_fullScreen || AppWindow.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Restored }) return;
        double s = DpiScale > 0 ? DpiScale : 1;
        Model.Settings.windowWidth = Math.Round(AppWindow.Size.Width / s);
        Model.Settings.windowHeight = Math.Round(AppWindow.Size.Height / s);
        Model.Settings.Save();
    }

    // ------------------------------------------------------------------ model -> UI

    private void Model_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerModel.Position): UpdatePosition(); break;
            case nameof(PlayerModel.Duration): UpdatePosition(); break;
            case nameof(PlayerModel.IsPlaying):
                PlayGlyph.Glyph = Model.IsPlaying ? "" : "";   // Pause / Play
                break;
            case nameof(PlayerModel.ErrorMessage):
                ErrorText.Text = Model.ErrorMessage ?? "";
                ErrorBanner.Visibility = string.IsNullOrEmpty(Model.ErrorMessage) ? Visibility.Collapsed : Visibility.Visible;
                break;
            case nameof(PlayerModel.Artwork): UpdateArtwork(); break;
            case nameof(PlayerModel.IsScanning): UpdateCounts(); break;
            case nameof(PlayerModel.SelectedDevice): UpdateDeviceButton(); break;
            case nameof(PlayerModel.AudioTracks):
            case nameof(PlayerModel.CurrentAudioTrack):
                AudioTrackButton.Visibility = Model.AudioTracks.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
                AudioTrackLabel.Text = Model.CurrentAudioTrackLabel;
                ToolTipService.SetToolTip(AudioTrackButton, $"Audio track: {Model.CurrentAudioTrackLabel} (A: next track)");
                FitRightControls();
                break;
            case nameof(PlayerModel.VisualizerEnabled):
                VisToggle.IsChecked = Model.VisualizerEnabled;
                UpdateVisualizer();
                break;
            case nameof(PlayerModel.ShowsVisualizer): UpdateVisualizer(); UpdateSubtitleVisibility(); break;
            case nameof(PlayerModel.SubtitleTracks):
            case nameof(PlayerModel.CurrentSubtitleTrack):
            case nameof(PlayerModel.HasVideo):
                UpdateSubtitleButton();
                UpdateSubtitleVisibility();
                if (e.PropertyName == nameof(PlayerModel.HasVideo)) UpdateInfo();
                break;
            case nameof(PlayerModel.VideoWidth):
            case nameof(PlayerModel.VideoHeight): LayoutSubtitle(); break;
            case nameof(PlayerModel.VisLocked):
                if (_vis != null) _vis.Locked = Model.VisLocked;
                VisLockIcon.Visibility = Model.VisLocked ? Visibility.Visible : Visibility.Collapsed;
                FlashPreset();
                break;
            case nameof(PlayerModel.Title):
                UpdateInfo();
                FlashTitle();
                break;
            default: UpdateInfo(); break;
        }
    }

    private void UpdateAll()
    {
        UpdateInfo();
        UpdatePosition();
        UpdateArtwork();
        UpdateDeviceButton();
        RefreshPlaylistView();
    }

    private void UpdateInfo()
    {
        bool playing = Model.Title.Length > 0;
        BarTitle.Text = playing ? Model.Title : "Not playing";
        BarArtist.Text = Model.Artist;
        BarArtist.Visibility = Model.Artist.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        StageTitle.Text = playing ? Model.Title : "Drop music or video files here";
        StageTitle.FontSize = playing ? 22 : 15;
        StageTitle.Foreground = new SolidColorBrush(playing ? Microsoft.UI.Colors.White : Windows.UI.Color.FromArgb(0x80, 255, 255, 255));
        StageSubtitle.Text = string.Join(" — ", new[] { Model.Artist, Model.Album }.Where(s => s.Length > 0));
        ArtGlyph.Glyph = playing ? "" : "";   // MusicNote / Speakers
        NowPlaying.Visibility = Model.HasVideo || (Model.ShowsVisualizer && _vis != null) ? Visibility.Collapsed : Visibility.Visible;
        VisSubtitleText.Text = StageSubtitle.Text;
        VisTitleText.Text = Model.Title;

        OutputBadge.Visibility = Model.OutputMode >= 0 ? Visibility.Visible : Visibility.Collapsed;
        bool bs = Model.IsBitstreaming;
        OutputBadgeText.Text = (bs ? (Model.BitstreamCodecName.Length == 0 ? "BITSTREAM" : Model.BitstreamCodecName.ToUpperInvariant()) : Model.PcmBadge)
                               + (Model.UpmixActive ? " · UPMIX" : "");
        OutputBadge.Background = bs ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
                                    : (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"];
        OutputBadgeText.Foreground = bs ? (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"]
                                        : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        ToolTipService.SetToolTip(OutputBadge, Model.OutputDescription);
        DetailSource.Text = Model.SourceAudio;
        SourceFormatText.Text = Model.SourceFormat;
        FitRightControls();
        DetailOutput.Text = Model.OutputDescription;
        DetailVideo.Text = Model.VideoDescription;
        DetailVideoPanel.Visibility = Model.HasVideo ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdatePosition()
    {
        DurationText.Text = TimeString(Model.Duration);
        PositionSlider.IsEnabled = Model.Duration > 0;
        if (_scrubbing) return;
        _updatingSlider = true;
        PositionSlider.Maximum = Math.Max(Model.Duration, 1);
        PositionSlider.Value = Math.Min(Model.Position, PositionSlider.Maximum);
        _updatingSlider = false;
        PositionText.Text = TimeString(Model.Position);
    }

    private async void UpdateArtwork()
    {
        var bytes = Model.Artwork;
        if (bytes == null) { ArtImage.Source = null; ArtPlaceholder.Visibility = Visibility.Visible; return; }
        try
        {
            var bmp = new BitmapImage();
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            await bmp.SetSourceAsync(stream);
            if (Model.Artwork != bytes) return;   // changed meanwhile
            ArtImage.Source = bmp;
            ArtPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch { ArtImage.Source = null; ArtPlaceholder.Visibility = Visibility.Visible; }
    }

    private void UpdateDeviceButton()
    {
        var d = Model.SelectedDevice;
        DeviceLabel.Text = d?.Name ?? "Output";
        DeviceIcon.Glyph = d?.IsHdmi == true ? "" : "";   // TVMonitor / Speakers
    }

    private static string TimeString(double t)
    {
        if (!double.IsFinite(t) || t <= 0) return "0:00";
        int s = (int)Math.Floor(t);
        return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }

    // ------------------------------------------------------------------ visualiser

    private void VisToggle_Click(object sender, RoutedEventArgs e) => Model.VisualizerEnabled = VisToggle.IsChecked == true;

    /// Show/hide the visualiser stage; it renders on CompositionTarget.Rendering while shown.
    private void UpdateVisualizer()
    {
        bool show = Model.ShowsVisualizer && EnsureVis();
        VisStage.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            TryAttachVis();
            if (!_visRendering) { CompositionTarget.Rendering += OnRendering; _visRendering = true; }
            Model.SetVisualizerActive(true);
            FlashTitle();
        }
        else
        {
            StopVisRendering();
            Model.SetVisualizerActive(false);
        }
        UpdateInfo();
    }

    private bool EnsureVis()
    {
        if (_vis != null) return true;
        if (_visFailed) return false;
        try
        {
            _vis = new Visualizer.VisualizerRenderer();
            unsafe { _vis.FetchSamples = (buf, n) => Model.VisSamples(buf, n); }
            _vis.Locked = Model.VisLocked;
            _vis.PresetChanged += _ => FlashPreset();
            VisPresetText.Text = _vis.PresetName;
            return true;
        }
        catch (Exception e)
        {
            _visFailed = true;
            Model.ErrorMessage = "Visualiser unavailable: " + e.Message;
            return false;
        }
    }

    private void TryAttachVis()
    {
        if (_vis == null || _visAttached || VisPanel.ActualWidth < 1 || VisPanel.ActualHeight < 1) return;
        try { _vis.Attach(VisPanel); _visAttached = true; }
        catch (Exception e) { Model.ErrorMessage = "Visualiser: " + e.Message; }
    }

    private void StopVisRendering()
    {
        if (!_visRendering) return;
        CompositionTarget.Rendering -= OnRendering;
        _visRendering = false;
    }

    private void OnRendering(object? sender, object e)
    {
        if (!_visAttached) { TryAttachVis(); return; }
        _vis?.RenderToPanel();
    }

    private void VisPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_visAttached) TryAttachVis();
        else _vis?.Resize(VisPanel.ActualWidth, VisPanel.ActualHeight, VisPanel.CompositionScaleX, VisPanel.CompositionScaleY);
    }

    private void VisPanel_CompositionScaleChanged(SwapChainPanel sender, object args) =>
        _vis?.Resize(VisPanel.ActualWidth, VisPanel.ActualHeight, VisPanel.CompositionScaleX, VisPanel.CompositionScaleY);

    private void FlashTitle()
    {
        if (!Model.ShowsVisualizer || Model.Title.Length == 0) return;
        VisTitle.Opacity = 1;
        _titleTimer.Stop();
        _titleTimer.Start();
    }

    private void FlashPreset()
    {
        if (_vis == null) return;
        VisPresetText.Text = _vis.PresetName;
        VisPresetPill.Opacity = 1;
        _presetTimer.Stop();
        _presetTimer.Start();
    }

    // ------------------------------------------------------------------ scrubbing

    private void BeginScrub()
    {
        if (!PositionSlider.IsEnabled) return;
        _scrubbing = true;
        Model.IsScrubbing = true;
    }

    private void EndScrub()
    {
        if (!_scrubbing) return;
        _scrubbing = false;
        Model.Seek(PositionSlider.Value);
        Model.IsScrubbing = false;
    }

    // ------------------------------------------------------------------ transport / toolbar

    private void Play_Click(object sender, RoutedEventArgs e) => Model.TogglePlay();
    private void Previous_Click(object sender, RoutedEventArgs e) => Model.Previous();
    private void Next_Click(object sender, RoutedEventArgs e) => Model.Next();
    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();
    private void Stage_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => ToggleFullScreen();
    private void Clear_Click(object sender, RoutedEventArgs e) => Model.ClearPlaylist();

    private async void LoadPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".m3u8");
        picker.FileTypeFilter.Add(".m3u");
        var file = await picker.PickSingleFileAsync();
        FocusPlayer();
        if (file != null) Model.LoadPlaylist(file.Path);
    }

    private async void SavePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (Model.Playlist.Count == 0) return;
        var picker = new FileSavePicker { SuggestedFileName = "Playlist" };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeChoices.Add("Playlist", new List<string> { ".m3u8" });
        var file = await picker.PickSaveFileAsync();
        FocusPlayer();
        if (file != null) Model.SavePlaylist(file.Path);
    }

    private void PlaylistToggle_Click(object sender, RoutedEventArgs e) => UpdatePlaylistVisibility();

    private void UpdatePlaylistVisibility()
    {
        bool show = PlaylistToggle.IsChecked == true && !_fullScreen;
        PlaylistPane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PlaylistColumn.Width = show ? new GridLength(300) : new GridLength(0);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var exts = string.Join(";", PlayerModel.MediaExtensions.Select(x => "*." + x)
                                 .Concat(new[] { "*.cue", "*.m3u", "*.m3u8" }));
        AddFromDialog(folders: false, "Add Files", new[] { ("Music, video and playlists", exts), ("All files", "*.*") });
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => AddFromDialog(folders: true, "Add Folders");

    /// Windows' open dialog (several files, or several folders), then add what was chosen.
    private void AddFromDialog(bool folders, string title, (string, string)[]? filters = null)
    {
        List<string> paths;
        try { paths = FileDialogs.Open(WinRT.Interop.WindowNative.GetWindowHandle(this), title, folders, multiple: true, filters); }
        catch (Exception ex)
        {
            DebugTrace.Log($"open dialog failed: 0x{ex.HResult:X8} {ex.Message}");
            Model.ErrorMessage = "Could not open the dialog: " + ex.Message;
            return;
        }
        FocusPlayer();
        DebugTrace.Log($"open dialog: {paths.Count} {(folders ? "folders" : "files")}");
        if (paths.Count > 0)
            Model.Add(paths.OrderBy(p => p, Comparer<string>.Create(Native.StrCmpLogicalW)),
                      playFirst: Model.Current == null || !Model.IsPlaying);
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        await new SettingsDialog(Model, Root.XamlRoot).ShowAsync();
        FocusPlayer();
    }

    /// The search box is the only control that keeps keyboard focus, and while it has it, Space
    /// and the other single keys type. Move focus to the playlist after dialogs (WinUI hands it
    /// to the first focusable control, the search box), clicks elsewhere, Enter and Esc.
    private void FocusPlayer()
    {
        if (IsInSearchBox(FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject))
            PlaylistView.Focus(FocusState.Programmatic);
    }

    private bool IsInSearchBox(DependencyObject? d)
    {
        for (; d != null; d = VisualTreeHelper.GetParent(d))
            if (d == SearchBox) return true;
        return false;
    }

    private void Root_PointerPressedAnywhere(object sender, PointerRoutedEventArgs e)
    {
        if (!IsInSearchBox(e.OriginalSource as DependencyObject)) FocusPlayer();
    }

    private void RecentMenu_Opening(object sender, object e)
    {
        RecentMenu.Items.Clear();
        foreach (var path in Model.RecentFiles.ToList())
        {
            string glyph = Directory.Exists(path) ? ""                                   // Folder
                : PlayerModel.IsCue(path) ? ""                                          // Document
                : PlaylistFile.IsPlaylist(path) ? ""                                    // BulletedList
                : "";                                                                   // Audio
            var item = new MenuFlyoutItem { Text = Model.RecentTitle(path), Icon = new FontIcon { Glyph = glyph } };
            ToolTipService.SetToolTip(item, path);
            item.Click += (_, _) => Model.OpenRecent(path);
            RecentMenu.Items.Add(item);
        }
        if (Model.RecentFiles.Count == 0)
            RecentMenu.Items.Add(new MenuFlyoutItem { Text = "No recent items", IsEnabled = false });
        RecentMenu.Items.Add(new MenuFlyoutSeparator());
        var clear = new MenuFlyoutItem { Text = "Clear Menu", IsEnabled = Model.RecentFiles.Count > 0 };
        clear.Click += (_, _) => Model.ClearRecents();
        RecentMenu.Items.Add(clear);
    }

    private void DeviceMenu_Opening(object sender, object e)
    {
        DeviceMenu.Items.Clear();
        var def = new ToggleMenuFlyoutItem { Text = "System Default", IsChecked = Model.DeviceUID.Length == 0 };
        def.Click += (_, _) => Model.DeviceUID = "";
        DeviceMenu.Items.Add(def);
        DeviceMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (var d in Model.Devices)
        {
            var item = new ToggleMenuFlyoutItem { Text = d.Label, IsChecked = Model.DeviceUID == d.Uid };
            item.Click += (_, _) => Model.DeviceUID = d.Uid;
            DeviceMenu.Items.Add(item);
        }
        DeviceMenu.Items.Add(new MenuFlyoutSeparator());
        var refresh = new MenuFlyoutItem { Text = "Refresh Devices" };
        refresh.Click += (_, _) => Model.RefreshDevices();
        DeviceMenu.Items.Add(refresh);
    }

    // ------------------------------------------------------------------ subtitles

    private readonly DispatcherQueueTimer _subTimer;
    private SubtitleFrame _subFrame = SubtitleFrame.None;

    private void ShowSubtitle(SubtitleFrame f)
    {
        _subFrame = f;
        if (f.Kind == Native.PC_SUB_BITMAP && f.Bgra != null)
        {
            var wb = new WriteableBitmap(f.W, f.H);
            using (var s = wb.PixelBuffer.AsStream()) s.Write(f.Bgra, 0, f.Bgra.Length);
            wb.Invalidate();
            SubImage.Source = wb;
        }
        else SubImage.Source = null;
        SubText.Text = f.Kind == Native.PC_SUB_TEXT ? f.Text : "";
        SubImage.Visibility = f.Kind == Native.PC_SUB_BITMAP ? Visibility.Visible : Visibility.Collapsed;
        SubTextBox.Visibility = f.Kind == Native.PC_SUB_TEXT && f.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        LayoutSubtitle();
        UpdateSubtitleVisibility();
    }

    /// The video's letterboxed rectangle in the stage (it is drawn "uniform").
    private (double X, double Y, double W, double H) VideoRect()
    {
        double sw = Stage.ActualWidth, sh = Stage.ActualHeight;
        int vw = Model.VideoWidth, vh = Model.VideoHeight;
        if (vw <= 0 || vh <= 0 || sw <= 0 || sh <= 0) return (0, 0, sw, sh);
        double s = Math.Min(sw / vw, sh / vh);
        return ((sw - vw * s) / 2, (sh - vh * s) / 2, vw * s, vh * s);
    }

    private void LayoutSubtitle()
    {
        var r = VideoRect();
        var f = _subFrame;
        if (f.Kind == Native.PC_SUB_BITMAP && f.RefW > 0 && f.RefH > 0)
        {
            double sx = r.W / f.RefW, sy = r.H / f.RefH;
            SubImage.Width = f.W * sx;
            SubImage.Height = f.H * sy;
            SubImage.Margin = new Thickness(r.X + f.X * sx, r.Y + f.Y * sy, 0, 0);
        }
        SubText.FontSize = Math.Clamp(r.H * 0.05, 14, 72);
        SubTextBox.MaxWidth = Math.Max(100, r.W * 0.9);
        SubTextBox.Margin = new Thickness(0, 0, 0, Math.Max(0, Stage.ActualHeight - (r.Y + r.H)) + r.H * 0.06);
    }

    private void UpdateSubtitleVisibility()
    {
        bool show = Model.HasVideo && !(Model.ShowsVisualizer && _vis != null) && _subFrame.Kind != Native.PC_SUB_NONE;
        SubtitleLayer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSubtitleButton()
    {
        SubtitleButton.Visibility = Model.HasVideo ? Visibility.Visible : Visibility.Collapsed;
        int i = Model.CurrentSubtitleTrack;
        SubtitleLabel.Text = Model.SubtitleShortLabel;
        ToolTipService.SetToolTip(SubtitleButton,
            $"Subtitles: {(i >= 0 && i < Model.SubtitleTracks.Count ? Model.SubtitleTracks[i] : "off")} (S: next)");
        FitRightControls();
    }

    private void BarGrid_SizeChanged(object sender, SizeChangedEventArgs e) => FitRightControls();

    /// The right-hand group (CC, audio track, format, badge) is right-aligned in its half of the
    /// bar, so when too wide it would be clipped from the left, hiding the buttons. Drop the
    /// labels instead, least useful first; the buttons and their menus stay.
    private void FitRightControls()
    {
        if (BarGrid.ActualWidth <= 0) return;
        double avail = (BarGrid.ActualWidth - TransportPanel.ActualWidth) / 2 - BarGrid.ColumnSpacing;
        SourceFormatText.Visibility = Model.SourceFormat.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AudioTrackLabel.Visibility = Visibility.Visible;
        SubtitleLabel.Visibility = Visibility.Visible;
        foreach (var label in new FrameworkElement[] { AudioTrackLabel, SubtitleLabel, SourceFormatText })
        {
            RightControls.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            if (RightControls.DesiredSize.Width <= avail) break;
            label.Visibility = Visibility.Collapsed;
        }
    }

    private void SubtitleMenu_Opening(object sender, object e)
    {
        SubtitleMenu.Items.Clear();
        var off = new ToggleMenuFlyoutItem { Text = "Off", IsChecked = Model.CurrentSubtitleTrack < 0 };
        off.Click += (_, _) => Model.SelectSubtitleTrack(-1);
        SubtitleMenu.Items.Add(off);
        for (int i = 0; i < Model.SubtitleTracks.Count; i++)
        {
            int index = i;
            var item = new ToggleMenuFlyoutItem { Text = Model.SubtitleTracks[i], IsChecked = i == Model.CurrentSubtitleTrack };
            item.Click += (_, _) => Model.SelectSubtitleTrack(index);
            SubtitleMenu.Items.Add(item);
        }
        SubtitleMenu.Items.Add(new MenuFlyoutSeparator());
        var load = new MenuFlyoutItem { Text = "Load Subtitle File…" };
        load.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            foreach (var ext in PlayerModel.SubtitleExtensions) picker.FileTypeFilter.Add(ext);
            var file = await picker.PickSingleFileAsync();
            FocusPlayer();
        FocusPlayer();
            if (file != null) Model.LoadSubtitleFile(file.Path);
        };
        SubtitleMenu.Items.Add(load);
    }

    private void AudioTrackMenu_Opening(object sender, object e)
    {
        AudioTrackMenu.Items.Clear();
        for (int i = 0; i < Model.AudioTracks.Count; i++)
        {
            int index = i;
            var item = new ToggleMenuFlyoutItem { Text = Model.AudioTracks[i], IsChecked = i == Model.CurrentAudioTrack };
            item.Click += (_, _) => Model.SelectAudioTrack(index);
            AudioTrackMenu.Items.Add(item);
        }
    }

    // ------------------------------------------------------------------ playlist + search

    private void RefreshPlaylistView()
    {
        if (_terms.Length == 0)
        {
            _filtered = null;
            if (PlaylistView.ItemsSource != Model.Playlist) PlaylistView.ItemsSource = Model.Playlist;
            PlaylistView.CanReorderItems = true;
        }
        else
        {
            _filtered = Model.Playlist.Where(t => t.Matches(_terms)).ToList();
            PlaylistView.ItemsSource = _filtered;
            PlaylistView.CanReorderItems = false;
        }
        UpdateCounts();
    }

    private void UpdateCounts()
    {
        int n = Model.Playlist.Count;
        ScanRing.IsActive = Model.IsScanning;
        ScanRing.Visibility = Model.IsScanning ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = Model.IsScanning ? "Scanning…" : _filtered != null ? $"{_filtered.Count} of {n} items" : $"{n} items";
        ClearButton.IsEnabled = n > 0;
        SavePlaylistButton.IsEnabled = n > 0;
        bool empty = n == 0 || (_filtered != null && _filtered.Count == 0);
        EmptyPlaylist.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = n == 0 ? "Drop files or folders here" : "No matches";
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        _terms = Track.Fold(sender.Text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        RefreshPlaylistView();
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var first = (_filtered ?? (IList<Track>)Model.Playlist).FirstOrDefault();
        if (first != null) Model.Play(first);
        FocusPlayer();
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            SearchBox.Text = "";
            PlaylistView.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
    }

    private void PlaylistView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is Track t) Model.Play(t);
    }

    private void PlaylistView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Delete) { Model.Remove(PlaylistView.SelectedItems.Cast<Track>().ToList()); e.Handled = true; }
        else if (e.Key == VirtualKey.Enter && PlaylistView.SelectedItem is Track t) { Model.Play(t); e.Handled = true; }
    }

    private void PlaylistView_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not Track t) return;
        if (!PlaylistView.SelectedItems.Contains(t)) { PlaylistView.SelectedItems.Clear(); PlaylistView.SelectedItems.Add(t); }
        var targets = PlaylistView.SelectedItems.Cast<Track>().ToList();
        var menu = new MenuFlyout();
        var play = new MenuFlyoutItem { Text = "Play" };
        play.Click += (_, _) => Model.Play(t);
        var show = new MenuFlyoutItem { Text = "Show in Explorer" };
        show.Click += (_, _) => Process.Start("explorer.exe", $"/select,\"{t.Path}\"");
        var remove = new MenuFlyoutItem { Text = "Remove" };
        remove.Click += (_, _) => Model.Remove(targets);
        menu.Items.Add(play);
        menu.Items.Add(show);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(remove);
        menu.ShowAt(PlaylistView, e.GetPosition(PlaylistView));
        e.Handled = true;
    }

    // ------------------------------------------------------------------ drag and drop

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (!IsFileDrag(e.DataView)) return;
        if (DropHighlight.Visibility != Visibility.Visible) DebugTrace.Log("drag over: files");
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Add to playlist";
        DropHighlight.Visibility = Visibility.Visible;
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        DropHighlight.Visibility = Visibility.Collapsed;
        if (!IsFileDrag(e.DataView)) return;
        // the data is read asynchronously: hold the drop open until then
        var deferral = e.GetDeferral();
        List<string>? paths = null;
        try
        {
            // The classic file list first. GetStorageItemsAsync, in an unpackaged app, returns only
            // one item for several files and throws for a folder plus files.
            paths = await ReadFileDropAsync(e.DataView);
            if (paths == null && e.DataView.Contains(StandardDataFormats.StorageItems))
                paths = (await e.DataView.GetStorageItemsAsync()).Select(i => i.Path).ToList();
            paths = paths?.Where(p => !string.IsNullOrEmpty(p))
                         .OrderBy(p => p, Comparer<string>.Create(Native.StrCmpLogicalW)).ToList();
            DebugTrace.Log($"drop: {paths?.Count ?? 0} paths");
        }
        catch (Exception ex)
        {
            DebugTrace.Log($"drop failed: 0x{ex.HResult:X8} {ex.Message} formats=[{string.Join(", ", e.DataView.AvailableFormats)}]");
            Model.ErrorMessage = "Could not read the dropped items: " + ex.Message;
        }
        finally { deferral.Complete(); }
        if (paths is { Count: > 0 }) Model.Add(paths, playFirst: !Model.IsPlaying);
    }

    private static bool IsFileDrag(DataPackageView view) =>
        view.Contains(StandardDataFormats.StorageItems) || view.AvailableFormats.Contains("FileDrop");

    /// The dropped paths from CF_HDROP ("FileDrop"): a DROPFILES header, then the paths as
    /// null-terminated strings ending with an empty one. Null when the format isn't there.
    private static async System.Threading.Tasks.Task<List<string>?> ReadFileDropAsync(DataPackageView view)
    {
        if (!view.AvailableFormats.Contains("FileDrop")) return null;
        var data = await view.GetDataAsync("FileDrop");
        var stream = data as IRandomAccessStream ?? WinRT.CastExtensions.As<IRandomAccessStream>(data);
        byte[] bytes;
        using (var s = stream.AsStreamForRead())
        using (var ms = new MemoryStream())
        {
            await s.CopyToAsync(ms);
            bytes = ms.ToArray();
        }
        if (bytes.Length < 20) return null;
        int offset = BitConverter.ToInt32(bytes, 0);       // DROPFILES.pFiles
        bool wide = BitConverter.ToInt32(bytes, 16) != 0;  // DROPFILES.fWide
        if (offset < 20 || offset >= bytes.Length) return null;
        string list = wide ? System.Text.Encoding.Unicode.GetString(bytes, offset, bytes.Length - offset)
                           : System.Text.Encoding.Default.GetString(bytes, offset, bytes.Length - offset);
        var paths = new List<string>();
        foreach (var p in list.Split('\0'))
        {
            if (p.Length == 0) break;   // the empty string ends the list
            paths.Add(p);
        }
        return paths.Count > 0 ? paths : null;
    }

    // ------------------------------------------------------------------ keys

    /// Single-key controls; skipped while typing in the search box.
    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool alt = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (ctrl && e.Key == VirtualKey.F) { SearchBox.Focus(FocusState.Keyboard); e.Handled = true; return; }
        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (ctrl && shift && e.Key == VirtualKey.O) { OpenFolder_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && e.Key == VirtualKey.O) { Open_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && e.Key == VirtualKey.S) { SavePlaylist_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && e.Key == VirtualKey.L) { LoadPlaylist_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && e.Key == VirtualKey.R) { RecentButton.Flyout.ShowAt(RecentButton); e.Handled = true; return; }
        if (ctrl && (int)e.Key == 188) { Settings_Click(this, new RoutedEventArgs()); e.Handled = true; return; }   // Ctrl+,
        var focused = FocusManager.GetFocusedElement(Root.XamlRoot);
        if (focused is TextBox || focused is AutoSuggestBox)
        {
            // an empty search box has nothing to type a space into: Space still plays/pauses
            if (e.Key == VirtualKey.Space && !ctrl && !alt && SearchBox.Text.Length == 0)
            {
                Model.TogglePlay();
                e.Handled = true;
            }
            return;
        }
        if (ctrl && e.Key == VirtualKey.Left) { Model.Previous(); e.Handled = true; return; }
        if (ctrl && e.Key == VirtualKey.Right) { Model.Next(); e.Handled = true; return; }
        if (ctrl || alt) return;
        switch (e.Key)
        {
            case VirtualKey.Space: Model.TogglePlay(); break;
            case VirtualKey.Left: Model.Skip(-10); break;
            case VirtualKey.Right: Model.Skip(10); break;
            case VirtualKey.Up when focused is not ListViewItem: Model.Skip(60); break;
            case VirtualKey.Down when focused is not ListViewItem: Model.Skip(-60); break;
            case VirtualKey.F: ToggleFullScreen(); break;
            case VirtualKey.V: Model.VisualizerEnabled = !Model.VisualizerEnabled; break;
            case (VirtualKey)221 or VirtualKey.N: _vis?.NextPreset(); break;       // ]
            case (VirtualKey)219 or VirtualKey.P: _vis?.PreviousPreset(); break;   // [
            case VirtualKey.L: Model.VisLocked = !Model.VisLocked; break;
            case VirtualKey.A: Model.CycleAudioTrack(); break;
            case VirtualKey.S: Model.CycleSubtitleTrack(); break;
            case VirtualKey.U: Model.ToggleUpmix(); break;
            case VirtualKey.Escape when _fullScreen: ToggleFullScreen(); break;
            default: return;
        }
        e.Handled = true;
    }

    // ------------------------------------------------------------------ full screen

    private void ToggleFullScreen()
    {
        _fullScreen = !_fullScreen;
        AppWindow.SetPresenter(_fullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
        // full screen = just the picture: no toolbar, no playlist, controls float over it
        Toolbar.Visibility = _fullScreen ? Visibility.Collapsed : Visibility.Visible;
        UpdatePlaylistVisibility();
        Grid.SetRowSpan(Stage, _fullScreen ? 2 : 1);
        ControlsBar.VerticalAlignment = _fullScreen ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
        ControlsBar.Margin = _fullScreen ? new Thickness(24) : new Thickness(0);
        ControlsBar.CornerRadius = _fullScreen ? new CornerRadius(14) : new CornerRadius(0);
        ControlsBar.MaxWidth = _fullScreen ? 900 : double.PositiveInfinity;
        VisTitle.Margin = _fullScreen ? new Thickness(48, 48, 48, 160) : new Thickness(20);
        VisPresetPill.Margin = _fullScreen ? new Thickness(48) : new Thickness(20);
        if (_fullScreen) PokeControls();
        else { _hideTimer.Stop(); ShowControls(); }
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_fullScreen) PokeControls();
    }

    private void ControlsBar_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_fullScreen) { ShowControls(); _hideTimer.Stop(); }
    }

    /// Full screen: show the controls on mouse movement, hide them (and the cursor) after 3 s.
    private void PokeControls()
    {
        ShowControls();
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void ShowControls()
    {
        if (_controlsHidden) { ControlsBar.Opacity = 1; _controlsHidden = false; }
        ShowCursor();
    }

    private void HideControls()
    {
        if (!_fullScreen) return;
        ControlsBar.Opacity = 0;
        _controlsHidden = true;
        if (!_cursorHidden) { Native.ShowCursor(false); _cursorHidden = true; }
    }

    private void ShowCursor()
    {
        if (_cursorHidden) { Native.ShowCursor(true); _cursorHidden = false; }
    }

    // ------------------------------------------------------------------ video panel

    private void VideoPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        DebugTrace.Log($"panel resize {VideoPanel.ActualWidth:F0}x{VideoPanel.ActualHeight:F0}");
        _renderer?.Resize(VideoPanel.ActualWidth, VideoPanel.ActualHeight, VideoPanel.CompositionScaleX, VideoPanel.CompositionScaleY);
        DebugTrace.Log("panel resized");
    }

    private void VideoPanel_CompositionScaleChanged(SwapChainPanel sender, object args)
    {
        DebugTrace.Log($"panel scale {VideoPanel.CompositionScaleX}");
        _renderer?.Resize(VideoPanel.ActualWidth, VideoPanel.ActualHeight, VideoPanel.CompositionScaleX, VideoPanel.CompositionScaleY);
        DebugTrace.Log("panel scaled");
    }
}
