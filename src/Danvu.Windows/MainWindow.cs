using System.Collections.ObjectModel;
using System.Diagnostics;
using Danvu.Core;
using Danvu.Windows.Services;
using Danvu.Windows.Views;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace Danvu.Windows;

public sealed partial class MainWindow : Window
{
    private readonly Grid _root = new();
    private readonly Grid _workspace = new();
    private readonly Grid _body = new();
    private readonly StackPanel _sidebar = new() { Spacing = 5, Padding = new Thickness(17, 20, 17, 16) };
    private readonly ContentControl _page = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly ContentControl _queueHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly TextBlock _status = Ui.Text("Demo mode · original music catalog · simulated playback", 11, false, Ui.Muted);
    private readonly TextBox _search = new() { PlaceholderText = "Search artists, songs, albums", MaxWidth = 540, Height = 38, CornerRadius = new CornerRadius(19), Padding = new Thickness(16, 8, 16, 8) };
    private readonly ContentControl _trackArt = new();
    private readonly TextBlock _trackTitle = Ui.Text("Choose something you love", 13, true);
    private readonly TextBlock _trackArtist = Ui.Text("Music", 12, false, Ui.Muted);
    private readonly TextBlock _time = Ui.Text("0:00", 11, false, Ui.Muted);
    private readonly TextBlock _remaining = Ui.Text("0:00", 11, false, Ui.Muted);
    private readonly Slider _progress = new() { Minimum = 0, Maximum = 100, MinWidth = 120, VerticalAlignment = VerticalAlignment.Center };
    private readonly Slider _volume = new() { Minimum = 0, Maximum = 100, Value = 65, Width = 100, VerticalAlignment = VerticalAlignment.Center };
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SpotifyAuthService _auth;
    private readonly AlbumPalette _palette;
    private readonly LyricsPanel _lyrics;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _seekDebounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _volumeDebounce = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly Dictionary<string, LyricsDocument> _importedLyrics = [];
    private readonly Stack<Route> _back = [];
    private readonly Stack<Route> _forward = [];
    private readonly Dictionary<string, Button> _navigationButtons = [];
    private IMusicService _music = new DemoMusicService();
    private PlaybackSnapshot? _playback;
    private long _snapshotTime;
    private Route _route = new("Home");
    private int _navigationVersion, _tick;
    private bool _sidebarVisible = true;
    private bool _syncSearch;
    private Grid? _header;
    private bool _syncPlayer, _pollBusy, _queueVisible, _lyricsVisible, _closed;
    private string? _lyricsTrack;
    private Button _play = null!, _shuffle = null!, _repeat = null!, _like = null!, _backButton = null!, _forwardButton = null!;
    private CancellationTokenSource? _searchDelay;
    private sealed record Route(string Kind, string Id = "", string Title = "", string Artwork = "", string ArtistId = "");

    public MainWindow()
    {
        Title = "Music";
        SystemBackdrop = new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };
        _auth = new SpotifyAuthService(_http, new WindowsTokenStore());
        _palette = new AlbumPalette(_http);
        _lyrics = new LyricsPanel(async position => { await _music.SeekAsync(position, _lifetime.Token); await PollAsync(); }, Notify)
        { Visibility = Visibility.Collapsed };
        _lyrics.CloseRequested += (_, _) => ToggleLyrics(false);
        _lyrics.ImportRequested += (_, _) => Run(ImportLyricsAsync);
        ExtendsContentIntoTitleBar = true;
        BuildWindow();
        Content = _root;
        ConfigureWindow();
        RegisterKeyboard();
        _root.SizeChanged += (_, _) => ApplyResponsiveLayout(true);
        _timer.Tick += (_, _) => Tick();
        _seekDebounce.Tick += (_, _) => { _seekDebounce.Stop(); Run(async () => { await _music.SeekAsync((int)_progress.Value, _lifetime.Token); await PollAsync(); }); };
        _volumeDebounce.Tick += (_, _) => { _volumeDebounce.Stop(); Run(async () => await _music.SetVolumeAsync((int)_volume.Value, _lifetime.Token)); };
        Closed += (_, _) =>
        {
            _closed = true; _timer.Stop(); _seekDebounce.Stop(); _volumeDebounce.Stop();
            _searchDelay?.Cancel(); _lifetime.Cancel(); _http.Dispose();
        };
        _root.Loaded += async (_, _) =>
        {
            await ExecuteAsync(InitializeAsync);
            _timer.Start();
        };
    }

    private void ConfigureWindow()
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
        var appWindow = AppWindow.GetFromWindowId(id);
        var area = DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Primary).WorkArea;
        var scale = GetDpiForWindow(handle) / 96.0;
        var width = Math.Min((int)(1480 * scale), area.Width - 40);
        var height = Math.Min((int)(960 * scale), area.Height - 40);
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(880 * scale);
            presenter.PreferredMinimumHeight = (int)(620 * scale);
        }
        appWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Music.ico");
        if (File.Exists(iconPath)) appWindow.SetIcon(iconPath);
        appWindow.TitleBar.ButtonForegroundColor = Colors.White;
        appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        ExtendsContentIntoTitleBar = true;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    private void ApplyResponsiveLayout(bool windowResized = false)
    {
        var width = _root.ActualWidth;
        if (windowResized && width < (_queueVisible ? 1180 : 980)) _sidebarVisible = false;
        var visible = _sidebarVisible;
        _body.ColumnDefinitions[0].Width = new GridLength(visible ? 214 : 0);
        if (_sidebar.Parent is FrameworkElement sidebar) sidebar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _volume.Visibility = width >= 1100 ? Visibility.Visible : Visibility.Collapsed;
        if (_header is not null) _header.ColumnDefinitions[0].Width = new GridLength(visible ? 214 : 132);
    }

    private void BuildWindow()
    {
        _root.Background = Ui.Brush(Color.FromArgb(255, 15, 14, 18));
        _root.RequestedTheme = ElementTheme.Dark;
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
        _root.RowDefinitions.Add(new RowDefinition { Height = Ui.Star });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(108) });
        var titlebar = new Grid { Padding = new Thickness(24, 0, 150, 0), Background = Ui.Brush(Colors.Transparent) };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(Ui.Icon("\uE8D6", 16, Ui.Accent));
        brand.Children.Add(Ui.Text("Music", 13, true));
        titlebar.Children.Add(brand);
        Ui.Add(_root, titlebar);
        SetTitleBar(titlebar);
        _workspace.RowDefinitions.Add(new RowDefinition { Height = new GridLength(66) });
        _workspace.RowDefinitions.Add(new RowDefinition { Height = Ui.Star });
        Ui.Add(_root, _workspace, 1);
        BuildHeader();
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(214) });
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = Ui.Star, MinWidth = 320 });
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Ui.Add(_workspace, _body, 1);
        var side = new Border
        {
            Background = new AcrylicBrush { TintColor = Color.FromArgb(255, 27, 25, 32), TintOpacity = .82, FallbackColor = Color.FromArgb(255, 27, 25, 32) },
            BorderBrush = Ui.Brush(Color.FromArgb(255, 43, 40, 49)), BorderThickness = new Thickness(0, 0, 1, 0), Child = _sidebar,
        };
        Ui.Add(_body, side);
        BuildSidebar();
        Ui.Add(_body, _page, 0, 1);
        _queueHost.Visibility = Visibility.Collapsed;
        Ui.Add(_body, _queueHost, 0, 2);
        Ui.Add(_root, BuildPlayer(), 2);
        Ui.Add(_root, _lyrics, 1);
    }

    private void BuildHeader()
    {
        var header = Ui.Columns(new GridLength(214), Ui.Star, Ui.Auto);
        _header = header;
        header.Padding = new Thickness(16, 8, 24, 10);
        var history = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        _backButton = Ui.IconButton("\uE76B", "Back (Alt+Left)", () => GoHistory(false));
        _forwardButton = Ui.IconButton("\uE76C", "Forward (Alt+Right)", () => GoHistory(true));
        history.Children.Add(Ui.IconButton("\uE700", "Toggle library sidebar", () =>
        {
            _sidebarVisible = !_sidebarVisible;
            if (_sidebarVisible && _queueVisible && _root.ActualWidth < 1180)
            {
                _queueVisible = false; _queueHost.Visibility = Visibility.Collapsed;
            }
            ApplyResponsiveLayout();
        }, 16));
        history.Children.Add(_backButton); history.Children.Add(_forwardButton);
        Ui.Add(header, history);
        _search.HorizontalAlignment = HorizontalAlignment.Center;
        _search.HorizontalContentAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetName(_search, "Search music (Ctrl+F)");
        _search.TextChanged += (_, _) =>
        {
            if (_syncSearch) return;
            _searchDelay?.Cancel(); _searchDelay?.Dispose();
            _searchDelay = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var token = _searchDelay.Token;
            Run(async () =>
            {
                try { await Task.Delay(300, token); } catch (OperationCanceledException) { return; }
                if (!token.IsCancellationRequested && (!string.IsNullOrWhiteSpace(_search.Text) || _route.Kind == "Search"))
                    await NavigateAsync(new Route("Search", _search.Text.Trim()), record: _route.Kind != "Search");
            });
        };
        Ui.Add(header, _search, 0, 1);
        var account = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        account.Children.Add(Ui.Pill("Jam", () => Run(ShowJamAsync)));
        _accountButton = Ui.Pill("Connect Spotify", () => Run(ShowAccountAsync));
        account.Children.Add(_accountButton);
        Ui.Add(header, account, 0, 2);
        Ui.Add(_workspace, header);
    }

    private void BuildSidebar()
    {
        _sidebar.Children.Clear();
        _sidebar.Children.Add(Ui.Text("DISCOVER", 10, true, Ui.Muted));
        Nav("Home", "\uE80F", "Home");
        Nav("Search", "\uE721", "Search");
        _sidebar.Children.Add(new Border { Height = 18 });
        _sidebar.Children.Add(Ui.Text("YOUR LIBRARY", 10, true, Ui.Muted));
        Nav("Recently added", "\uE823", "Library");
        Nav("Artists", "\uE77B", "Artists");
        Nav("Albums", "\uE93C", "Albums");
        Nav("Songs", "\uE8D6", "Songs");
        Nav("Liked songs", "\uEB52", "Liked");
        _sidebar.Children.Add(new Border { Height = 18 });
        var playlistHeading = Ui.Columns(Ui.Star, Ui.Auto);
        Ui.Add(playlistHeading, Ui.Text("PLAYLISTS", 10, true, Ui.Muted));
        Ui.Add(playlistHeading, Ui.IconButton("\uE710", "Create playlist", () => Run(CreatePlaylistAsync), 12), 0, 1);
        _sidebar.Children.Add(playlistHeading);
        Nav("Your playlists", "\uE8F1", "Playlists");
        _sidebar.Children.Add(new Border { Height = 18 });
        _sidebar.Children.Add(Ui.Pill("Account & settings", () => Run(ShowAccountAsync)));
        _sidebar.Children.Add(Ui.Text(_music.IsDemo ? "DEMO COLLECTION" : "SPOTIFY CONNECT", 9, true, Ui.Muted));
        UpdateNavigation();
    }

    private void Nav(string title, string glyph, string kind)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        content.Children.Add(Ui.Icon(glyph, 17, kind == "Liked" ? Ui.Accent : Ui.Muted));
        content.Children.Add(Ui.Text(title, 13));
        var button = new Button { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Background = Ui.Brush(Colors.Transparent), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10) };
        button.Click += (_, _) => Run(() => NavigateAsync(new Route(kind)));
        Ui.Tactile(button);
        _navigationButtons[kind] = button;
        _sidebar.Children.Add(button);
    }

    private UIElement BuildPlayer()
    {
        var outer = new Border { Margin = new Thickness(12, 6, 12, 12), CornerRadius = new CornerRadius(20), Background = new AcrylicBrush { TintColor = Color.FromArgb(255, 39, 35, 44), TintOpacity = .9, FallbackColor = Color.FromArgb(255, 32, 29, 37) }, BorderBrush = Ui.Brush(Color.FromArgb(255, 68, 61, 74)), BorderThickness = new Thickness(1), Padding = new Thickness(18, 10, 18, 10) };
        var grid = Ui.Columns(new GridLength(1, GridUnitType.Star), new GridLength(1.5, GridUnitType.Star), new GridLength(1, GridUnitType.Star));
        var info = Ui.Columns(Ui.Auto, Ui.Star, Ui.Auto);
        _trackArt.Content = Ui.Art("ms-appx:///Assets/Artwork/afterglow.png", 56, 9);
        var artButton = new Button { Content = _trackArt, Padding = new Thickness(0), Background = Ui.Brush(Colors.Transparent), BorderThickness = new Thickness(0) };
        artButton.Click += (_, _) => ToggleLyrics(true);
        ToolTipService.SetToolTip(artButton, "Open Now Playing & lyrics (Ctrl+L)");
        AutomationProperties.SetName(artButton, "Open Now Playing and lyrics");
        Ui.Add(info, artButton);
        var names = new StackPanel { Spacing = 3, Margin = new Thickness(12, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(_trackTitle);
        var artistButton = new Button { Content = _trackArtist, Padding = new Thickness(0), Background = Ui.Brush(Colors.Transparent), BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left };
        artistButton.Click += (_, _) => { if (_playback?.Track is { } t) Run(() => NavigateAsync(new Route("Artist", t.ArtistId))); };
        names.Children.Add(artistButton);
        Ui.Add(info, names, 0, 1);
        _like = Ui.IconButton("\uEB51", "Like current song", () => Run(ToggleLikeAsync), 16);
        Ui.Add(info, _like, 0, 2);
        Ui.Add(grid, info);
        var center = new StackPanel { Spacing = 0, Margin = new Thickness(18, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center };
        _shuffle = Ui.IconButton("\uE8B1", "Shuffle", () => Run(async () => { await _music.SetShuffleAsync(!(_playback?.Shuffle ?? false), _lifetime.Token); await PollAsync(); if (_queueVisible) await RefreshQueueAsync(); }), 15);
        controls.Children.Add(_shuffle);
        controls.Children.Add(Ui.IconButton("\uE892", "Previous track (Ctrl+Left)", () => Run(() => SkipAsync(false)), 19));
        _play = Ui.IconButton("\uE768", "Play or pause (Space)", () => Run(TogglePlayAsync), 26);
        controls.Children.Add(_play);
        controls.Children.Add(Ui.IconButton("\uE893", "Next track (Ctrl+Right)", () => Run(() => SkipAsync(true)), 19));
        _repeat = Ui.IconButton("\uE8EE", "Repeat off", () => Run(async () => { var mode = (RepeatMode)(((int)(_playback?.Repeat ?? RepeatMode.Off) + 1) % 3); await _music.SetRepeatAsync(mode, _lifetime.Token); await PollAsync(); }), 15);
        controls.Children.Add(_repeat);
        center.Children.Add(controls);
        var timeline = Ui.Columns(Ui.Auto, Ui.Star, Ui.Auto);
        _time.Margin = new Thickness(0, 0, 10, 0); _time.VerticalAlignment = VerticalAlignment.Center;
        _remaining.Margin = new Thickness(10, 0, 0, 0); _remaining.VerticalAlignment = VerticalAlignment.Center;
        _progress.ValueChanged += (_, _) => { if (!_syncPlayer && _playback?.Track is not null) { _seekDebounce.Stop(); _seekDebounce.Start(); } };
        _progress.ThumbToolTipValueConverter = new TimeValueConverter();
        AutomationProperties.SetName(_progress, "Playback position");
        Ui.Add(timeline, _time); Ui.Add(timeline, _progress, 0, 1); Ui.Add(timeline, _remaining, 0, 2);
        center.Children.Add(timeline); Ui.Add(grid, center, 0, 1);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(Ui.IconButton("\uE8F2", "Lyrics (Ctrl+L)", () => ToggleLyrics(!_lyricsVisible), 17));
        right.Children.Add(Ui.IconButton("\uE8FD", "Queue", () => Run(ToggleQueueAsync), 17));
        right.Children.Add(Ui.IconButton("\uE7F4", "Spotify playback devices", () => Run(ShowDevicesAsync), 17));
        right.Children.Add(Ui.Icon("\uE767", 16, Ui.Muted));
        _volume.ValueChanged += (_, _) => { if (!_syncPlayer) { _volumeDebounce.Stop(); _volumeDebounce.Start(); } };
        AutomationProperties.SetName(_volume, "Playback volume");
        right.Children.Add(_volume);
        Ui.Add(grid, right, 0, 2);
        outer.Child = grid;
        return outer;
    }

    private async Task InitializeAsync()
    {
        string? restoreMessage = null;
        try
        {
            if (await _auth.RestoreAsync(_lifetime.Token))
            {
                _music = new SpotifyMusicService(_http, _auth);
                _accountButton.Content = Ui.Text("Your account", 13, true);
                BuildSidebar();
            }
        }
        catch (MusicServiceException ex) { restoreMessage = ex.Message; }
        await NavigateAsync(new Route("Home"), false);
        await PollAsync();
        if (restoreMessage is not null) Notify(restoreMessage);
    }

    private async void Run(Func<Task> action) => await ExecuteAsync(action);
    private async Task ExecuteAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is MusicServiceException or HttpRequestException or IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException or System.Runtime.InteropServices.COMException)
        { Notify(ex.Message); }
    }
    private void Notify(string message)
    {
        if (_closed) return;
        DispatcherQueue.TryEnqueue(() => { _status.Text = message; ToolTipService.SetToolTip(_status, message); });
    }

    private async void Tick()
    {
        if (_closed) return;
        if (_playback is { Track: not null } snapshot && !_seekDebounce.IsEnabled)
        {
            var position = snapshot.PositionMs + (snapshot.IsPlaying ? (int)Stopwatch.GetElapsedTime(_snapshotTime).TotalMilliseconds : 0);
            position = Math.Clamp(position, 0, snapshot.Track.DurationMs);
            _syncPlayer = true; _progress.Value = position; _syncPlayer = false;
            _time.Text = FormatTime(position); _remaining.Text = "−" + FormatTime(snapshot.Track.DurationMs - position);
            if (_lyricsVisible) _lyrics.UpdatePlayback(position, snapshot.IsPlaying);
        }
        if (++_tick % 8 == 0 && !_pollBusy) await ExecuteAsync(PollAsync);
    }

    private async Task PollAsync()
    {
        if (_pollBusy || _closed) return;
        _pollBusy = true;
        try
        {
            var service = _music;
            var state = await service.GetPlaybackAsync(_lifetime.Token);
            if (!ReferenceEquals(service, _music) || _closed) return;
            var changed = _playback?.Track?.Id != state.Track?.Id;
            _playback = state; _snapshotTime = Stopwatch.GetTimestamp();
            _syncPlayer = true;
            _progress.Maximum = Math.Max(1, state.Track?.DurationMs ?? 1);
            if (!_seekDebounce.IsEnabled) _progress.Value = state.PositionMs;
            if (!_volumeDebounce.IsEnabled) _volume.Value = state.Volume;
            _syncPlayer = false;
            _trackTitle.Text = state.Track?.Title ?? "Choose something you love";
            _trackArtist.Text = state.Track?.Artist ?? (_music.IsDemo ? "Demo mode · silent playback" : "Select a Spotify device");
            _play.Content = Ui.Icon(state.IsPlaying ? "\uE769" : "\uE768", 26);
            _shuffle.Content = Ui.Icon("\uE8B1", 15, state.Shuffle ? Ui.Accent : Ui.Muted);
            _repeat.Content = Ui.Icon(state.Repeat == RepeatMode.One ? "\uE8ED" : "\uE8EE", 15, state.Repeat != RepeatMode.Off ? Ui.Accent : Ui.Muted);
            ToolTipService.SetToolTip(_repeat, "Repeat " + state.Repeat.ToString().ToLowerInvariant());
            _like.Content = Ui.Icon(state.Track?.IsLiked == true ? "\uEB52" : "\uEB51", 16, state.Track?.IsLiked == true ? Ui.Accent : Ui.Muted);
            if (changed && state.Track is { } track)
            {
                _trackArt.Content = Ui.Art(track.ArtworkUrl, 56, 9);
                Ui.FadeIn(_trackArt, 0);
                if (_lyricsVisible) await LoadLyricsAsync();
                if (_queueVisible) await RefreshQueueAsync();
            }
            if (_lyricsVisible) _lyrics.UpdatePlayback(state.PositionMs, state.IsPlaying);
        }
        finally { _pollBusy = false; }
    }

    private async Task TogglePlayAsync()
    {
        if (_playback?.IsPlaying == true) await _music.PauseAsync(_lifetime.Token);
        else if (_playback?.Track is not null) await _music.ResumeAsync(_lifetime.Token);
        else { var home = await _music.GetHomeAsync(_lifetime.Token); if (home.FeaturedAlbum is { } album) await _music.PlayAsync(await _music.GetAlbumTracksAsync(album.Id, _lifetime.Token), cancellationToken: _lifetime.Token); }
        await PollAsync();
    }
    private async Task SkipAsync(bool next) { await _music.SkipAsync(next, _lifetime.Token); await PollAsync(); }
    private async Task ToggleLikeAsync()
    {
        if (_playback?.Track is not { } track) return;
        await _music.SetLikedAsync(track.Id, !track.IsLiked, _lifetime.Token);
        InvalidateLibrary();
        await PollAsync();
    }
    private void RegisterKeyboard()
    {
        void Bind(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, args) =>
            {
                var focused = FocusManager.GetFocusedElement(_root.XamlRoot);
                if (focused is TextBox && (key is VirtualKey.Space or VirtualKey.Left or VirtualKey.Right)) return;
                if (key == VirtualKey.Space && focused is Control) return;
                action(); args.Handled = true;
            };
            _root.KeyboardAccelerators.Add(accelerator);
        }
        Bind(VirtualKey.F, VirtualKeyModifiers.Control, () => _search.Focus(FocusState.Keyboard));
        Bind(VirtualKey.L, VirtualKeyModifiers.Control, () => ToggleLyrics(!_lyricsVisible));
        Bind(VirtualKey.Space, VirtualKeyModifiers.None, () => Run(TogglePlayAsync));
        Bind(VirtualKey.Right, VirtualKeyModifiers.Control, () => Run(() => SkipAsync(true)));
        Bind(VirtualKey.Left, VirtualKeyModifiers.Control, () => Run(() => SkipAsync(false)));
        Bind(VirtualKey.Left, VirtualKeyModifiers.Menu, () => GoHistory(false));
        Bind(VirtualKey.Right, VirtualKeyModifiers.Menu, () => GoHistory(true));
        Bind(VirtualKey.Escape, VirtualKeyModifiers.None, () => ToggleLyrics(false));
        Bind((VirtualKey)0xB3, VirtualKeyModifiers.None, () => Run(TogglePlayAsync));
        Bind((VirtualKey)0xB0, VirtualKeyModifiers.None, () => Run(() => SkipAsync(true)));
        Bind((VirtualKey)0xB1, VirtualKeyModifiers.None, () => Run(() => SkipAsync(false)));
    }
    private static string FormatTime(int ms) => $"{Math.Max(0, ms) / 60000}:{Math.Max(0, ms) / 1000 % 60:00}";
    private sealed class TimeValueConverter : Microsoft.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) => FormatTime((int)(double)value);
        public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
    }
}
