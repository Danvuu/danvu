using Danvu.Core;
using Danvu.Windows.Views;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace Danvu.Windows;

public sealed partial class MainWindow
{
    private async Task NavigateAsync(Route route, bool record = true)
    {
        if (_lyricsVisible) ToggleLyrics(false);
        if (route.Kind != "Search") _searchDelay?.Cancel();
        else if (_search.Text != route.Id)
        {
            _syncSearch = true; _search.Text = route.Id; _syncSearch = false;
        }
        if (record && route != _route) { _back.Push(_route); _forward.Clear(); }
        _route = route;
        var version = ++_navigationVersion;
        UpdateNavigation();
        _page.Content = LoadingView();
        try
        {
            var content = await BuildRouteAsync(route);
            if (version != _navigationVersion || _closed) return;
            var scroll = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var container = new Grid();
            container.RowDefinitions.Add(new RowDefinition { Height = Ui.Star });
            container.RowDefinitions.Add(new RowDefinition { Height = Ui.Auto });
            Ui.Add(container, scroll);
            // One status element belongs to the current content tree at a time.
            if (_status.Parent is Panel old) old.Children.Remove(_status);
            _status.Margin = new Thickness(30, 8, 30, 12);
            Ui.Add(container, _status, 1);
            var compactTitle = Ui.WithId(Ui.Text(CompactPageTitle(route), 14, true), "Page.CompactTitle");
            compactTitle.HorizontalAlignment = HorizontalAlignment.Center;
            compactTitle.VerticalAlignment = VerticalAlignment.Center;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHeadingLevel(compactTitle, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
            var compactHeader = Ui.WithId(new Border
            {
                Child = compactTitle, Height = 42, Padding = new Thickness(24, 0, 24, 0),
                VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed,
                // A title-only overlay lets wheel, touch and pointer input reach the scroller.
                IsHitTestVisible = false,
                Background = new AcrylicBrush
                {
                    TintColor = Color.FromArgb(255, 30, 30, 32), TintOpacity = .78,
                    FallbackColor = Color.FromArgb(255, 30, 30, 32),
                },
                BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = Ui.Brush(Color.FromArgb(18, 255, 255, 255)),
            }, "Page.CompactHeader");
            Ui.Add(container, compactHeader);
            var compactVisible = false;
            scroll.ViewChanged += (_, _) =>
            {
                var visible = scroll.VerticalOffset > 100;
                if (visible == compactVisible) return;
                compactVisible = visible;
                compactHeader.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                if (visible) Ui.FadeIn(compactHeader, -5);
            };
            _page.Content = container;
            Ui.FadeIn(content);
            Notify(_music.IsDemo ? "Demo mode · original catalog · simulated playback" : "Connected to Spotify · audio plays on your selected Spotify device");
        }
        catch (Exception ex) when (ex is MusicServiceException or HttpRequestException or IOException)
        {
            if (version != _navigationVersion) return;
            var panel = PageHeading("Something needs attention", ex.Message);
            panel.Children.Add(Ui.Pill("Try again", () => Run(() => NavigateAsync(route, false)), true));
            panel.Children.Add(Ui.Pill("Account & demo mode", () => Run(ShowAccountAsync)));
            _page.Content = panel;
            Notify(ex.Message);
        }
    }

    private static FrameworkElement LoadingView()
    {
        // Composed native shapes have no external control-template URI to resolve
        // while a navigation replaces the loaded content tree.
        var stack = new StackPanel
        {
            Spacing = 13, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var dots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        for (var index = 0; index < 3; index++)
        {
            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 5, Height = 5, Fill = Ui.Brush(Ui.Accent), Opacity = .45 };
            var delay = index * 140;
            dot.Loaded += (_, _) =>
            {
                if (!Ui.AnimationsEnabled) return;
                var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(dot);
                var pulse = visual.Compositor.CreateScalarKeyFrameAnimation();
                pulse.InsertKeyFrame(0, .3f); pulse.InsertKeyFrame(.5f, 1); pulse.InsertKeyFrame(1, .3f);
                pulse.Duration = TimeSpan.FromMilliseconds(900); pulse.DelayTime = TimeSpan.FromMilliseconds(delay);
                pulse.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
                visual.StartAnimation("Opacity", pulse);
            };
            dot.Unloaded += (_, _) => Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(dot).StopAnimation("Opacity");
            dots.Children.Add(dot);
        }
        stack.Children.Add(dots);
        stack.Children.Add(Ui.Text("Loading music", 12, false, Ui.Muted));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(stack, "Loading music");
        return stack;
    }

    private readonly Dictionary<string, string> _artistTitles = [];
    private string CompactPageTitle(Route route) => !string.IsNullOrWhiteSpace(route.Title) ? route.Title : route.Kind switch
    {
        "Home" => "Listen Now",
        "Library" => "Recently Added",
        "Liked" => "Liked Songs",
        "Artist" => _artistTitles.GetValueOrDefault(route.Id, "Artist"),
        _ => route.Kind,
    };

    private void GoHistory(bool forward)
    {
        var from = forward ? _forward : _back;
        var to = forward ? _back : _forward;
        if (from.Count == 0) return;
        to.Push(_route);
        var route = from.Pop();
        Run(() => NavigateAsync(route, false));
    }
    private void UpdateNavigation()
    {
        foreach (var (key, button) in _navigationButtons)
            button.Background = Ui.Brush(key == _route.Kind ? Color.FromArgb(22, 255, 255, 255) : Colors.Transparent);
        _backButton.IsEnabled = _back.Count > 0; _forwardButton.IsEnabled = _forward.Count > 0;
    }
    private async Task<FrameworkElement> BuildRouteAsync(Route route)
    {
        return route.Kind switch
        {
            "Home" => await HomeViewAsync(),
            "Search" => await SearchViewAsync(route.Id),
            "Artist" => await ArtistViewAsync(route.Id),
            "Album" => await CollectionViewAsync(route, false),
            "Playlist" => await CollectionViewAsync(route, true),
            _ => await LibraryViewAsync(route.Kind),
        };
    }

    private static StackPanel PageHeading(string title, string subtitle)
    {
        var panel = new StackPanel { Spacing = 20, Padding = new Thickness(30, 22, 30, 36), HorizontalAlignment = HorizontalAlignment.Stretch };
        if (!string.IsNullOrWhiteSpace(title)) panel.Children.Add(Ui.Text(title, 30, true));
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            var text = Ui.Text(subtitle, 13, false, Ui.Muted);
            text.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(text);
        }
        return panel;
    }

    private async Task<FrameworkElement> HomeViewAsync()
    {
        var home = await _music.GetHomeAsync(_lifetime.Token);
        var panel = PageHeading("", "");
        var heading = Ui.Columns(Ui.Star, Ui.Auto);
        heading.ColumnSpacing = 22;
        var headingCopy = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        headingCopy.Children.Add(Ui.Text(_music.IsDemo ? "EXPLORE MUSIC" : "YOUR MUSIC", 10, true, Color.FromArgb(255, 177, 164, 164)));
        headingCopy.Children.Add(Ui.Text("Listen Now", 32, true));
        Ui.Add(heading, headingCopy);
        if (home.FeaturedAlbum is { } featured)
        {
            // The featured record is an invitation beside the title, never a billboard.
            var feature = Ui.Columns(Ui.Auto, Ui.Star, Ui.Auto);
            feature.ColumnSpacing = 12;
            feature.Width = 236;
            Ui.Add(feature, Ui.Art(featured.ArtworkUrl, 52, 8));
            var copy = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            copy.Children.Add(Ui.Text("FEATURED ALBUM", 9, true, Color.FromArgb(255, 202, 178, 173)));
            copy.Children.Add(Ui.Text(featured.Title, 14, true));
            copy.Children.Add(Ui.Text(featured.Artist, 11, false, Ui.Muted));
            Ui.Add(feature, copy, 0, 1);
            Ui.Add(feature, Ui.Icon("\uE76C", 12, Ui.Muted), 0, 2);
            var explore = Ui.WithId(ArtworkButton(feature, () => Run(() => NavigateAsync(AlbumRoute(featured)))), "Home.ExploreAlbum");
            explore.Padding = new Thickness(10);
            explore.CornerRadius = new CornerRadius(13);
            explore.Background = Ui.Brush(Color.FromArgb(72, 91, 62, 54));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(explore, "Explore album " + featured.Title);
            Ui.Add(heading, explore, 0, 1);
        }
        panel.Children.Add(heading);
        if (home.Artists.Count > 0)
            AddSection(panel, _music.IsDemo ? "Artists to know" : "Your artists", "", ArtistSpotlight(home.Artists));

        // Home stays bounded: a first album supplies this small row without fetching an entire library.
        var homeTracks = _libraryCache is { } cached && ReferenceEquals(_libraryOwner, _music)
            ? cached.Tracks
            : home.FeaturedAlbum is { } rotationAlbum
                ? await _music.GetAlbumTracksAsync(rotationAlbum.Id, _lifetime.Token)
                : Array.Empty<Track>();
        if (homeTracks.Count > 0)
            AddSection(panel, _music.IsDemo ? "Songs to discover" : "Songs in your collection", "", CompactSongs(homeTracks.Take(8).ToArray()));
        if (home.Albums.Count > 0)
            AddSection(panel, "Albums for you", "", AlbumShelf(home.Albums));
        if (home.Playlists.Count > 0)
            AddSection(panel, "Your playlists", "", PlaylistShelf(home.Playlists));
        if (home.Albums.Count == 0 && home.Playlists.Count == 0 && home.Artists.Count == 0)
            panel.Children.Add(Empty("Your collection starts here", "Save an album or a playlist in Spotify, or search for something you love."));

        var canvas = new Grid();
        canvas.Children.Add(new Border
        {
            Height = 340, VerticalAlignment = VerticalAlignment.Top,
            Background = new LinearGradientBrush
            {
                StartPoint = new global::Windows.Foundation.Point(.85, 0), EndPoint = new global::Windows.Foundation.Point(.3, 1),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(105, 111, 41, 34), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(38, 90, 56, 25), Offset = .34 },
                    new GradientStop { Color = Colors.Transparent, Offset = 1 },
                },
            },
        });
        canvas.Children.Add(panel);
        return canvas;
    }

    private async Task<FrameworkElement> SearchViewAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            var intro = PageHeading("Search", "Find your next favorite.");
            intro.Children.Add(Empty("What do you want to hear?", "Use the search field above for songs, artists, albums, and playlists. Ctrl+F takes you there."));
            _search.Focus(FocusState.Keyboard);
            return intro;
        }
        var results = await _music.SearchAsync(query, _lifetime.Token);
        var panel = PageHeading("Search", "Results for “" + query + "”");
        if (results.Tracks.Count > 0) AddSection(panel, "Songs", "", TrackList(results.Tracks.Take(10).ToArray()));
        if (results.Artists.Count > 0) AddSection(panel, "Artists", "", ArtistGrid(results.Artists));
        if (results.Albums.Count > 0) AddSection(panel, "Albums", "", AlbumGrid(results.Albums));
        if (results.Playlists.Count > 0) AddSection(panel, "Playlists", "", PlaylistGrid(results.Playlists));
        if (results.Tracks.Count + results.Artists.Count + results.Albums.Count + results.Playlists.Count == 0)
            panel.Children.Add(Empty("No results yet", "Try another artist, song title, or album."));
        return panel;
    }

    private async Task<FrameworkElement> LibraryViewAsync(string kind)
    {
        var library = await GetLibraryCachedAsync();
        var title = kind switch { "Liked" => "Liked songs", "Library" => "Recently added", _ => kind };
        var panel = PageHeading(title, "");
        switch (kind)
        {
            case "Artists": panel.Children.Add(ArtistGrid(library.Artists)); break;
            case "Albums": panel.Children.Add(AlbumGrid(library.Albums)); break;
            case "Songs": panel.Children.Add(TrackList(library.Tracks)); break;
            case "Liked": panel.Children.Add(TrackList(library.Tracks.Where(t => t.IsLiked).ToArray())); break;
            case "Playlists":
                panel.Children.Add(Ui.Pill("+ Create playlist", () => Run(CreatePlaylistAsync), true));
                panel.Children.Add(PlaylistGrid(library.Playlists)); break;
            default:
                AddSection(panel, "Albums", "", AlbumGrid(library.Albums));
                AddSection(panel, "Playlists", "", PlaylistGrid(library.Playlists));
                AddSection(panel, "Saved songs", "", TrackList(library.Tracks)); break;
        }
        return panel;
    }

    private async Task<FrameworkElement> ArtistViewAsync(string artistId)
    {
        var detail = await _music.GetArtistAsync(artistId, _lifetime.Token);
        var artist = detail.Artist;
        _artistTitles[artist.Id] = artist.Name;
        var panel = PageHeading("", "");
        var header = Ui.Columns(Ui.Auto, Ui.Star);
        header.ColumnSpacing = 28;
        var portrait = Ui.Art(artist.ArtworkUrl, 196, 98);
        Ui.Add(header, portrait);
        var intro = new StackPanel { Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        intro.Children.Add(Ui.Text("ARTIST", 11, true, Ui.Accent));
        var name = Ui.Text(artist.Name, 38, true); name.TextWrapping = TextWrapping.Wrap;
        intro.Children.Add(name);
        intro.Children.Add(Ui.Text(artist.Followers > 0 ? $"{artist.Followers:N0} followers" : "Explore the discography", 13, false, Ui.Muted));
        if (detail.TopTracks.Count > 0) intro.Children.Add(Ui.Pill("▶  Play", () => Run(() => PlayTracksAsync(detail.TopTracks)), true));
        Ui.Add(header, intro, 0, 1);
        panel.Children.Add(header);
        if (detail.TopTracks.Count > 0) AddSection(panel, "Popular", "", TrackList(detail.TopTracks));
        else panel.Children.Add(Ui.Text("Popular tracks are unavailable for this Spotify app. Explore the albums below.", 12, false, Ui.Muted));
        AddSection(panel, "Albums & releases", "", AlbumGrid(detail.Albums));
        return panel;
    }

    private async Task<FrameworkElement> CollectionViewAsync(Route route, bool playlist)
    {
        var tracks = playlist ? await _music.GetPlaylistTracksAsync(route.Id, _lifetime.Token) : await _music.GetAlbumTracksAsync(route.Id, _lifetime.Token);
        var panel = PageHeading("", "");
        var header = Ui.Columns(Ui.Auto, Ui.Star);
        header.ColumnSpacing = 28;
        var art = Ui.Art(route.Artwork, 220, 8);
        art.Loaded += (_, _) =>
        {
            if (!Ui.AnimationsEnabled) return;
            Microsoft.UI.Xaml.Media.Animation.ConnectedAnimationService.GetForCurrentView().GetAnimation("AlbumArtwork")?.TryStart(art);
        };
        Ui.Add(header, art);
        var intro = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        intro.Children.Add(Ui.Text(playlist ? "PLAYLIST" : "ALBUM", 10, true, Ui.Muted));
        var title = Ui.WithId(Ui.Text(route.Title, 27, true), "Collection.Title"); title.TextWrapping = TextWrapping.Wrap;
        intro.Children.Add(title);
        var artist = tracks.FirstOrDefault()?.Artist ?? "Your collection";
        if (playlist) intro.Children.Add(Ui.Text(_music.IsDemo ? "Music · Original playlists" : "Spotify playlist", 14, false, Ui.Accent));
        else
        {
            var artistButton = ArtworkButton(Ui.Text(artist, 15, false, Ui.Accent), () =>
            {
                var artistId = route.ArtistId.Length > 0 ? route.ArtistId : tracks.FirstOrDefault()?.ArtistId;
                if (!string.IsNullOrEmpty(artistId)) Run(() => NavigateAsync(new Route("Artist", artistId)));
            });
            intro.Children.Add(artistButton);
        }
        if (playlist && _playlistDetails.TryGetValue(route.Id, out var detail) && ReferenceEquals(detail.Owner, _music) && !string.IsNullOrWhiteSpace(detail.Playlist.Description))
        {
            var description = Ui.Text(System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(detail.Playlist.Description, "<[^>]+>", "")), 12, false, Ui.Muted);
            description.TextWrapping = TextWrapping.Wrap;
            description.MaxLines = 3;
            description.Margin = new Thickness(0, 2, 0, 0);
            intro.Children.Add(description);
        }
        intro.Children.Add(Ui.Text($"{tracks.Count} songs, {tracks.Sum(t => (long)t.DurationMs) / 60000} minutes", 11, false, Ui.Muted));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, Margin = new Thickness(0, 10, 0, 0) };
        var shuffle = Ui.IconButton("\uE8B1", "Shuffle collection", () => Run(async () => { await _music.SetShuffleAsync(true, _lifetime.Token); await PlayTracksAsync(tracks); }), 16);
        shuffle.Background = Ui.Brush(Color.FromArgb(255, 61, 61, 63));
        shuffle.Width = 34; shuffle.Height = 34; shuffle.CornerRadius = new CornerRadius(17);
        actions.Children.Add(shuffle);
        var play = Ui.WithId(Ui.Pill("Play", () => Run(() => PlayTracksAsync(tracks))), "Collection.Play");
        var playContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        playContent.Children.Add(Ui.Icon("\uE768", 13));
        playContent.Children.Add(Ui.Text("Play", 12, true));
        play.Content = playContent;
        play.Background = Ui.Brush(Color.FromArgb(255, 61, 61, 63));
        play.CornerRadius = new CornerRadius(6); play.Height = 34;
        play.Padding = new Thickness(18, 6, 18, 6);
        actions.Children.Add(play);
        var more = new Button
        {
            Content = Ui.Icon("\uE712", 19), Padding = new Thickness(7), Width = 34, Height = 34,
            Background = Ui.Brush(Colors.Transparent), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(17),
            Flyout = CollectionMenu(tracks),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(more, "Collection options");
        ToolTipService.SetToolTip(more, "Collection options");
        actions.Children.Add(more);
        intro.Children.Add(actions);
        Ui.Add(header, intro, 0, 1);
        header.SizeChanged += (_, args) =>
        {
            var size = args.NewSize.Width >= 940 ? 280 : 220;
            if (art.Width == size) return;
            art.Width = size; art.Height = size;
            if (art.Child is Image image) { image.Width = size; image.Height = size; }
        };
        panel.Children.Add(header);
        panel.Children.Add(TrackList(tracks));
        return panel;
    }

    private MenuFlyout CollectionMenu(IReadOnlyList<Track> tracks)
    {
        var menu = new MenuFlyout();
        var play = new MenuFlyoutItem { Text = "Play" };
        play.Click += (_, _) => Run(() => PlayTracksAsync(tracks));
        menu.Items.Add(play);
        var shuffle = new MenuFlyoutItem { Text = "Shuffle" };
        shuffle.Click += (_, _) => Run(async () => { await _music.SetShuffleAsync(true, _lifetime.Token); await PlayTracksAsync(tracks); });
        menu.Items.Add(shuffle);
        var queue = new MenuFlyoutItem { Text = "Add to queue" };
        queue.Click += (_, _) => Run(async () =>
        {
            foreach (var track in tracks) await _music.EnqueueAsync(track, _lifetime.Token);
            if (_queueVisible) await RefreshQueueAsync();
            Notify($"Added {tracks.Count} songs to queue");
        });
        menu.Items.Add(queue);
        return menu;
    }

    private static void AddSection(StackPanel panel, string title, string subtitle, UIElement content)
    {
        var heading = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, -7) };
        heading.Children.Add(Ui.Text(title, 21, true));
        if (subtitle.Length > 0) heading.Children.Add(Ui.Text(subtitle, 12, false, Ui.Muted));
        panel.Children.Add(heading); panel.Children.Add(content);
    }
    private static FrameworkElement Empty(string heading, string message)
    {
        var stack = new StackPanel { Spacing = 10, Padding = new Thickness(24, 36, 24, 36), MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left };
        stack.Children.Add(Ui.Text(heading, 20, true));
        var text = Ui.Text(message, 13, false, Ui.Muted); text.TextWrapping = TextWrapping.Wrap;
        stack.Children.Add(text);
        return stack;
    }
    private LibraryContent? _libraryCache;
    private IMusicService? _libraryOwner;
    private DateTimeOffset _libraryFetched;
    private void InvalidateLibrary() => _libraryCache = null;
    private async Task<LibraryContent> GetLibraryCachedAsync()
    {
        if (_libraryCache is not null && ReferenceEquals(_libraryOwner, _music) && DateTimeOffset.UtcNow - _libraryFetched < TimeSpan.FromMinutes(1))
            return _libraryCache;
        var owner = _music;
        var library = await owner.GetLibraryAsync(_lifetime.Token);
        if (ReferenceEquals(owner, _music))
        {
            _libraryCache = library; _libraryOwner = owner; _libraryFetched = DateTimeOffset.UtcNow;
        }
        return library;
    }
    private GridView Cards()
    {
        var grid = new GridView { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = false, IsTabStop = false, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(-6, 0, -6, 0) };
        ScrollViewer.SetHorizontalScrollBarVisibility(grid, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(grid, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollMode(grid, ScrollMode.Disabled);
        return grid;
    }
    private FrameworkElement Card(string title, string subtitle, string artwork, Action click, bool round = false, Action<FrameworkElement>? beforeClick = null)
    {
        var stack = new StackPanel { Width = 190, Spacing = 7 };
        stack.Children.Add(Ui.Art(artwork, 190, round ? 95 : 11));
        stack.Children.Add(Ui.Text(title, 14, true));
        stack.Children.Add(Ui.Text(subtitle, 12, false, Ui.Muted));
        var button = ArtworkButton(stack, () => { beforeClick?.Invoke((FrameworkElement)stack.Children[0]); click(); });
        button.Padding = new Thickness(6, 6, 6, 10);
        button.Margin = new Thickness(0, 0, 4, 8);
        ToolTipService.SetToolTip(button, title + " · " + subtitle);
        return button;
    }
    private static Button ArtworkButton(UIElement content, Action action)
    {
        var button = new Button
        {
            Content = content, Padding = new Thickness(0), Background = Ui.Brush(Colors.Transparent),
            BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(12),
            HorizontalAlignment = HorizontalAlignment.Left, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        button.Resources["ButtonBackgroundPointerOver"] = Ui.Brush(Color.FromArgb(14, 255, 255, 255));
        button.Resources["ButtonBackgroundPressed"] = Ui.Brush(Color.FromArgb(22, 255, 255, 255));
        button.Resources["ButtonBorderBrushPointerOver"] = Ui.Brush(Colors.Transparent);
        button.Resources["ButtonBorderBrushPressed"] = Ui.Brush(Colors.Transparent);
        button.Click += (_, _) => action();
        Ui.Tactile(button);
        return button;
    }
    private static ScrollViewer ArtworkShelf(IEnumerable<FrameworkElement> cards)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        foreach (var card in cards) row.Children.Add(card);
        return new ScrollViewer
        {
            Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled,
            ZoomMode = ZoomMode.Disabled, Padding = new Thickness(0, 0, 0, 3),
        };
    }
    private FrameworkElement AlbumShelf(IReadOnlyList<Album> albums) => ArtworkShelf(albums.Take(16).Select(album =>
        Card(album.Title, album.Artist, album.ArtworkUrl, () => Run(() => NavigateAsync(AlbumRoute(album))), beforeClick: PrepareAlbumArtwork)));
    private FrameworkElement PlaylistShelf(IReadOnlyList<Playlist> playlists) => ArtworkShelf(playlists.Take(16).Select(playlist =>
        Card(playlist.Name, $"{playlist.TrackCount} songs", playlist.ArtworkUrl,
            () => Run(() => NavigateAsync(PlaylistRoute(playlist))))));
    private FrameworkElement ArtistSpotlight(IReadOnlyList<Artist> artists)
    {
        return ArtworkShelf(artists.Take(12).Select((artist, index) =>
        {
            var image = new Image { Width = 190, Height = 236, Stretch = Stretch.UniformToFill };
            var uri = Danvu.Windows.Services.LocalState.ResolveArtworkUri(artist.ArtworkUrl);
            if (uri is not null) image.Source = new BitmapImage(uri) { DecodePixelWidth = 380 };
            var portrait = new Grid { Width = 190, Height = 236 };
            portrait.Children.Add(image);
            portrait.Children.Add(new Border
            {
                Background = new LinearGradientBrush
                {
                    StartPoint = new global::Windows.Foundation.Point(0, .2), EndPoint = new global::Windows.Foundation.Point(0, 1),
                    GradientStops =
                    {
                        new GradientStop { Color = Colors.Transparent, Offset = 0 },
                        new GradientStop { Color = Color.FromArgb(30, 0, 0, 0), Offset = .4 },
                        new GradientStop { Color = Color.FromArgb(225, 8, 8, 10), Offset = 1 },
                    },
                },
            });
            if (_music.IsDemo)
            {
                var rank = Ui.Text((index + 1).ToString(), 39, true, Color.FromArgb(235, 255, 255, 255));
                rank.Margin = new Thickness(14, 8, 0, 0);
                portrait.Children.Add(rank);
            }
            var label = new StackPanel { Spacing = 4, Margin = new Thickness(16, 0, 16, 17), VerticalAlignment = VerticalAlignment.Bottom };
            label.Children.Add(Ui.Text(artist.Name, 15, true));
            label.Children.Add(Ui.Text("Artist", 11, false, Color.FromArgb(190, 255, 255, 255)));
            portrait.Children.Add(label);
            var surface = new Border
            {
                CornerRadius = new CornerRadius(12), Child = portrait,
                BorderThickness = new Thickness(1), BorderBrush = Ui.Brush(Color.FromArgb(28, 255, 255, 255)),
            };
            var button = ArtworkButton(surface, () => Run(() => NavigateAsync(new Route("Artist", artist.Id))));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, "Artist " + artist.Name);
            ToolTipService.SetToolTip(button, artist.Name);
            return (FrameworkElement)button;
        }));
    }
    private FrameworkElement CompactSongs(IReadOnlyList<Track> tracks)
    {
        var grid = Ui.Columns(Ui.Star, Ui.Star);
        grid.ColumnSpacing = 28;
        var rows = new List<FrameworkElement>();
        for (var index = 0; index < tracks.Count; index++)
        {
            var track = tracks[index];
            var songIndex = index;
            var row = Ui.Columns(Ui.Star, Ui.Auto);
            var content = Ui.Columns(Ui.Auto, Ui.Auto, Ui.Star);
            content.ColumnSpacing = 12;
            content.Margin = new Thickness(0, 7, 0, 7);
            Ui.Add(content, Ui.Art(track.ArtworkUrl, 43, 6));
            var number = Ui.Text((index + 1).ToString(), 12, false, Ui.Muted);
            number.Width = 13; number.VerticalAlignment = VerticalAlignment.Center;
            Ui.Add(content, number, 0, 1);
            var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(Ui.Text(track.Title, 13, true));
            text.Children.Add(Ui.Text(track.Artist, 11, false, Ui.Muted));
            Ui.Add(content, text, 0, 2);
            var play = ArtworkButton(content, () => Run(() => PlayTracksAsync(tracks, songIndex)));
            play.HorizontalAlignment = HorizontalAlignment.Stretch;
            play.ContextFlyout = TrackMenu(track, tracks, songIndex);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(play, "Play " + track.Title + " by " + track.Artist);
            Ui.Add(row, play);
            var options = new Button
            {
                Content = Ui.Icon("\uE712", 15), Background = Ui.Brush(Colors.Transparent), BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(8), Width = 34, Height = 34, Padding = new Thickness(7),
                VerticalAlignment = VerticalAlignment.Center, Flyout = TrackMenu(track, tracks, songIndex),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(options, "Options for " + track.Title);
            ToolTipService.SetToolTip(options, "Song options");
            Ui.Add(row, options, 0, 1);
            var divider = new Border { Child = row, BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = Ui.Brush(Color.FromArgb(22, 255, 255, 255)) };
            rows.Add(divider);
            grid.Children.Add(divider);
        }
        var currentColumns = 0;
        void Arrange(double width)
        {
            var columns = width >= 580 ? 2 : 1;
            if (columns == currentColumns) return;
            currentColumns = columns;
            var rowCount = (tracks.Count + columns - 1) / columns;
            grid.RowDefinitions.Clear();
            for (var row = 0; row < rowCount; row++) grid.RowDefinitions.Add(new RowDefinition { Height = Ui.Auto });
            grid.ColumnDefinitions[1].Width = columns == 2 ? Ui.Star : new GridLength(0);
            for (var index = 0; index < rows.Count; index++)
            {
                Grid.SetRow(rows[index], index % rowCount);
                Grid.SetColumn(rows[index], index / rowCount);
            }
        }
        Arrange(800);
        grid.SizeChanged += (_, args) => Arrange(args.NewSize.Width);
        return grid;
    }
    private FrameworkElement PagedCards<T>(IReadOnlyList<T> entries, Func<T, FrameworkElement> create)
    {
        var panel = new StackPanel { Spacing = 8 };
        var grid = Cards();
        var loaded = 0;
        Button? more = null;
        void Append()
        {
            var end = Math.Min(entries.Count, loaded + 24);
            while (loaded < end) grid.Items.Add(create(entries[loaded++]));
            if (more is not null)
            {
                more.Visibility = loaded < entries.Count ? Visibility.Visible : Visibility.Collapsed;
                more.Content = Ui.Text($"Show more · {loaded} of {entries.Count}", 13, true);
            }
        }
        panel.Children.Add(grid);
        more = Ui.Pill("Show more", Append);
        panel.Children.Add(more);
        Append();
        return panel;
    }
    private FrameworkElement AlbumGrid(IReadOnlyList<Album> albums)
    {
        if (albums.Count == 0) return Empty("No albums yet", "Save some albums in Spotify, or find one through Search.");
        return PagedCards(albums, album => Card(album.Title, album.Artist, album.ArtworkUrl,
            () => Run(() => NavigateAsync(AlbumRoute(album))), beforeClick: PrepareAlbumArtwork));
    }
    private FrameworkElement PlaylistGrid(IReadOnlyList<Playlist> playlists)
    {
        if (playlists.Count == 0) return Empty("Make it yours", "Create a playlist to start your collection.");
        return PagedCards(playlists, playlist => Card(playlist.Name, $"{playlist.TrackCount} tracks", playlist.ArtworkUrl,
            () => Run(() => NavigateAsync(PlaylistRoute(playlist)))));
    }
    private readonly Dictionary<string, (IMusicService Owner, Playlist Playlist)> _playlistDetails = [];
    private Route PlaylistRoute(Playlist playlist)
    {
        _playlistDetails[playlist.Id] = (_music, playlist);
        return new Route("Playlist", playlist.Id, playlist.Name, playlist.ArtworkUrl);
    }
    private FrameworkElement ArtistGrid(IReadOnlyList<Artist> artists)
    {
        if (artists.Count == 0) return Empty("Find your artists", "Your followed artists appear here.");
        return PagedCards(artists, artist => Card(artist.Name, "Artist", artist.ArtworkUrl,
            () => Run(() => NavigateAsync(new Route("Artist", artist.Id))), true));
    }
    private sealed class TrackRow(Track track, int index)
    {
        public Track Track { get; } = track;
        public int Index { get; } = index;
        public string Title => Track.Title;
        public string Artist => Track.Artist;
        public string Album => Track.Album;
        public string Duration => FormatTime(Track.DurationMs);
        public Uri? Artwork => Danvu.Windows.Services.LocalState.ResolveArtworkUri(Track.ArtworkUrl);
    }
    private FrameworkElement TrackList(IReadOnlyList<Track> tracks)
    {
        if (tracks.Count == 0) return Empty("Nothing here yet", "Search for songs, or add something to your collection.");
        // A bounded native ListView recycles its rows even inside the page's outer scroller.
        var list = new ListView
        {
            ItemsSource = tracks.Select((track, index) => new TrackRow(track, index)).ToArray(),
            SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true,
            MaxHeight = 650, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        var rowStyle = new Style(typeof(ListViewItem));
        rowStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        rowStyle.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        rowStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        rowStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 56d));
        list.ItemContainerStyle = rowStyle;
        list.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
             <Border BorderBrush="#12FFFFFF" BorderThickness="0,0,0,1">
              <Grid x:Name="TrackRowLayout" ColumnSpacing="14" Height="56">
                <Grid.ColumnDefinitions>
                  <ColumnDefinition Width="2.8*"/><ColumnDefinition Width="1.6*"/>
                  <ColumnDefinition Width="1.8*"/><ColumnDefinition Width="48"/><ColumnDefinition Width="32"/>
                </Grid.ColumnDefinitions>
                <Grid ColumnSpacing="12">
                  <Grid.ColumnDefinitions><ColumnDefinition Width="40"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                  <Border CornerRadius="4" Width="40" Height="40" Background="#2B2B2D">
                    <Image Source="{Binding Artwork}" Stretch="UniformToFill"/>
                  </Border>
                  <TextBlock Grid.Column="1" Text="{Binding Title}" FontSize="12" TextTrimming="CharacterEllipsis" VerticalAlignment="Center"/>
                </Grid>
                <TextBlock Grid.Column="1" Text="{Binding Artist}" FontSize="12" Opacity="0.58" TextTrimming="CharacterEllipsis" VerticalAlignment="Center"/>
                <TextBlock x:Name="TrackAlbum" Grid.Column="2" Text="{Binding Album}" FontSize="12" Opacity="0.58" TextTrimming="CharacterEllipsis" VerticalAlignment="Center"/>
                <TextBlock Grid.Column="3" Text="{Binding Duration}" FontSize="11" Opacity="0.55" VerticalAlignment="Center" HorizontalAlignment="Right"/>
                <Button x:Name="TrackOptions" Grid.Column="4" Content="•••" Width="32" Height="32" MinWidth="32" Padding="0" Background="Transparent" BorderThickness="0" ToolTipService.ToolTip="Song options" AutomationProperties.Name="Song options"/>
              </Grid>
             </Border>
            </DataTemplate>
            """);
        var headings = TrackTableColumns();
        headings.ColumnSpacing = 14;
        headings.Height = 32;
        var albumHeading = Ui.Text("Album", 11, false, Ui.Muted);
        Ui.Add(headings, Ui.Text("Song", 11, false, Ui.Muted));
        Ui.Add(headings, Ui.Text("Artist", 11, false, Ui.Muted), 0, 1);
        Ui.Add(headings, albumHeading, 0, 2);
        var timeHeading = Ui.Text("Time", 11, false, Ui.Muted);
        timeHeading.HorizontalAlignment = HorizontalAlignment.Right;
        Ui.Add(headings, timeHeading, 0, 3);
        foreach (var label in headings.Children.OfType<TextBlock>()) label.VerticalAlignment = VerticalAlignment.Center;
        var showAlbum = true;
        void LayoutRow(FrameworkElement? template)
        {
            if (template?.FindName("TrackRowLayout") is Grid rowGrid)
            {
                rowGrid.ColumnDefinitions[2].Width = showAlbum ? new GridLength(1.8, GridUnitType.Star) : new GridLength(0);
                if (template.FindName("TrackAlbum") is FrameworkElement album) album.Visibility = showAlbum ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        list.ItemClick += (_, args) => { if (args.ClickedItem is TrackRow row) Run(() => PlayTracksAsync(tracks, row.Index)); };
        list.ContainerContentChanging += (_, args) =>
        {
            if (args.InRecycleQueue || args.Item is not TrackRow row) return;
            args.ItemContainer.ContextFlyout = TrackMenu(row.Track, tracks, row.Index);
            args.RegisterUpdateCallback((_, ready) =>
            {
                if (ready.InRecycleQueue || ready.Item is not TrackRow current) return;
                if (ready.ItemContainer.ContentTemplateRoot is FrameworkElement template)
                {
                    LayoutRow(template);
                    if (template.FindName("TrackOptions") is Button options) options.Flyout = TrackMenu(current.Track, tracks, current.Index);
                }
            });
        };
        var table = new StackPanel { Spacing = 0 };
        table.Children.Add(new Border { Child = headings, BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = Ui.Brush(Color.FromArgb(18, 255, 255, 255)) });
        table.Children.Add(list);
        table.SizeChanged += (_, args) =>
        {
            var next = args.NewSize.Width >= 540;
            if (showAlbum == next) return;
            showAlbum = next;
            headings.ColumnDefinitions[2].Width = showAlbum ? new GridLength(1.8, GridUnitType.Star) : new GridLength(0);
            albumHeading.Visibility = showAlbum ? Visibility.Visible : Visibility.Collapsed;
            for (var index = 0; index < tracks.Count; index++)
                if (list.ContainerFromIndex(index) is ListViewItem item) LayoutRow(item.ContentTemplateRoot as FrameworkElement);
        };
        return table;
    }
    private static Grid TrackTableColumns() => Ui.Columns(new GridLength(2.8, GridUnitType.Star), new GridLength(1.6, GridUnitType.Star), new GridLength(1.8, GridUnitType.Star), new GridLength(48), new GridLength(32));
    private MenuFlyout TrackMenu(Track track, IReadOnlyList<Track> tracks, int index)
    {
        var menu = new MenuFlyout();
        void Item(string text, Func<Task> action)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => Run(action); menu.Items.Add(item);
        }
        Item("Play", () => PlayTracksAsync(tracks, index));
        Item("Add to queue", async () => { await _music.EnqueueAsync(track, _lifetime.Token); if (_queueVisible) await RefreshQueueAsync(); Notify("Added to queue: " + track.Title); });
        Item(track.IsLiked ? "Remove from Liked Songs" : "Add to Liked Songs", async () => { await _music.SetLikedAsync(track.Id, !track.IsLiked, _lifetime.Token); InvalidateLibrary(); await NavigateAsync(_route, false); await PollAsync(); });
        Item("Add to playlist…", () => AddToPlaylistAsync(track));
        menu.Items.Add(new MenuFlyoutSeparator());
        Item("Go to artist", () => NavigateAsync(new Route("Artist", track.ArtistId)));
        Item("Go to album", () => NavigateAsync(new Route("Album", track.AlbumId, track.Album, track.ArtworkUrl, track.ArtistId)));
        return menu;
    }
    private static void PrepareAlbumArtwork(FrameworkElement source)
    {
        if (!Ui.AnimationsEnabled) return;
        var animation = Microsoft.UI.Xaml.Media.Animation.ConnectedAnimationService.GetForCurrentView().PrepareToAnimate("AlbumArtwork", source);
        animation.Configuration = new Microsoft.UI.Xaml.Media.Animation.BasicConnectedAnimationConfiguration();
    }
    private static Route AlbumRoute(Album album) => new("Album", album.Id, album.Title, album.ArtworkUrl, album.ArtistId);
    private async Task PlayAlbumAsync(Album album) => await PlayTracksAsync(await _music.GetAlbumTracksAsync(album.Id, _lifetime.Token));
    private async Task PlayTracksAsync(IReadOnlyList<Track> tracks, int index = 0)
    {
        if (tracks.Count == 0) { Notify("There are no playable tracks in this collection."); return; }
        await _music.PlayAsync(tracks, index, _lifetime.Token);
        await PollAsync();
    }
}
