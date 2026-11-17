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

    public MainWindow(string[] files)
    {
        InitializeComponent();
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);

        Model = new PlayerModel(DispatcherQueue);
        PlaceWindow();
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
        VideoPanel.Loaded += (_, _) => { try { _renderer?.Attach(VideoPanel); } catch (Exception e) { Model.ErrorMessage = "Video: " + e.Message; } };

        Model.PropertyChanged += Model_PropertyChanged;
        Model.Playlist.CollectionChanged += (_, _) => RefreshPlaylistView();
        Model.Devices.CollectionChanged += (_, _) => UpdateDeviceButton();
        PlaylistView.ItemsSource = Model.Playlist;

        PositionSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => BeginScrub()), true);
        PositionSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => EndScrub()), true);
        PositionSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => EndScrub()), true);
        PositionSlider.ValueChanged += (_, e) => { if (!_updatingSlider) PositionText.Text = TimeString(e.NewValue); };

        _hideTimer = DispatcherQueue.CreateTimer();
        _hideTimer.Interval = TimeSpan.FromSeconds(3);
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) => HideControls();

        Root.DragLeave += (_, _) => DropHighlight.Visibility = Visibility.Collapsed;
        Closed += (_, _) =>
        {
            SaveWindowSize();
            ShowCursor();
            Model.Shutdown();
            _renderer?.Dispose();
        };

        UpdateAll();
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
                AudioTrackButton.Visibility = Model.AudioTracks.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
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
        NowPlaying.Visibility = Model.HasVideo ? Visibility.Collapsed : Visibility.Visible;

        OutputBadge.Visibility = Model.OutputMode >= 0 ? Visibility.Visible : Visibility.Collapsed;
        bool bs = Model.IsBitstreaming;
        OutputBadgeText.Text = bs ? (Model.BitstreamCodecName.Length == 0 ? "BITSTREAM" : Model.BitstreamCodecName.ToUpperInvariant()) : "PCM";
        OutputBadge.Background = bs ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
                                    : (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"];
        OutputBadgeText.Foreground = bs ? (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"]
                                        : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        ToolTipService.SetToolTip(OutputBadge, Model.OutputDescription);
        DetailSource.Text = Model.SourceAudio;
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

    private void PlaylistToggle_Click(object sender, RoutedEventArgs e) => UpdatePlaylistVisibility();

    private void UpdatePlaylistVisibility()
    {
        bool show = PlaylistToggle.IsChecked == true && !_fullScreen;
        PlaylistPane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PlaylistColumn.Width = show ? new GridLength(300) : new GridLength(0);
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count > 0) Model.Add(files.Select(f => f.Path).OrderBy(p => p, Comparer<string>.Create(Native.StrCmpLogicalW)),
                                       playFirst: Model.Current == null || !Model.IsPlaying);
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder != null) Model.Add(new[] { folder.Path }, playFirst: Model.Current == null || !Model.IsPlaying);
    }

    private async void Settings_Click(object sender, RoutedEventArgs e) => await new SettingsDialog(Model, Root.XamlRoot).ShowAsync();

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
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Add to playlist";
        DropHighlight.Visibility = Visibility.Visible;
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        DropHighlight.Visibility = Visibility.Collapsed;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p))
                         .OrderBy(p => p, Comparer<string>.Create(Native.StrCmpLogicalW)).ToList();
        Model.Add(paths, playFirst: !Model.IsPlaying);
    }

    // ------------------------------------------------------------------ keys

    /// Single-key controls; skipped while typing in the search box.
    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool alt = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (ctrl && e.Key == VirtualKey.F) { SearchBox.Focus(FocusState.Keyboard); e.Handled = true; return; }
        if (ctrl && e.Key == VirtualKey.O) { Open_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && (int)e.Key == 188) { Settings_Click(this, new RoutedEventArgs()); e.Handled = true; return; }   // Ctrl+,
        var focused = FocusManager.GetFocusedElement(Root.XamlRoot);
        if (focused is TextBox || focused is AutoSuggestBox) return;
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

    private void VideoPanel_SizeChanged(object sender, SizeChangedEventArgs e) =>
        _renderer?.Resize(VideoPanel.ActualWidth, VideoPanel.ActualHeight, VideoPanel.CompositionScaleX, VideoPanel.CompositionScaleY);

    private void VideoPanel_CompositionScaleChanged(SwapChainPanel sender, object args) =>
        _renderer?.Resize(VideoPanel.ActualWidth, VideoPanel.ActualHeight, VideoPanel.CompositionScaleX, VideoPanel.CompositionScaleY);
}
