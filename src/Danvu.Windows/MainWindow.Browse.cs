using Danvu.Core;
using Danvu.Windows.Views;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
        var loading = new ProgressBar
        {
            IsIndeterminate = true, Width = 180, Height = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(loading, "Loading music");
        _page.Content = loading;
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
            button.Background = Ui.Brush(key == _route.Kind ? Color.FromArgb(255, 63, 39, 48) : Colors.Transparent);
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
        var panel = new StackPanel { Spacing = 18, Padding = new Thickness(32, 22, 32, 24), HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(Ui.Text(title, 32, true));
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
        var panel = PageHeading("Home", _music.IsDemo ? "A little discovery. A lot of feeling." : "Your music, with room to breathe.");
        if (home.FeaturedAlbum is { } featured)
        {
            var hero = Ui.Columns(new GridLength(1.3, GridUnitType.Star), new GridLength(1, GridUnitType.Star));
            hero.MinHeight = 270;
            var copy = new StackPanel { Spacing = 14, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(30) };
            copy.Children.Add(Ui.Text(_music.IsDemo ? "THE DEMO COLLECTION" : "FROM YOUR COLLECTION", 10, true, Color.FromArgb(255, 252, 189, 178)));
            var heading = Ui.Text(featured.Title, 43, true); heading.TextWrapping = TextWrapping.Wrap;
            copy.Children.Add(heading);
            copy.Children.Add(Ui.Text(featured.Artist + "  ·  " + featured.Year, 14, false, Color.FromArgb(255, 239, 203, 204)));
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 6, 0, 0) };
            actions.Children.Add(Ui.Pill("▶  Play album", () => Run(() => PlayAlbumAsync(featured)), true));
            actions.Children.Add(Ui.WithId(Ui.Pill("Explore album", () => Run(() => NavigateAsync(AlbumRoute(featured)))), "Home.ExploreAlbum"));
            copy.Children.Add(actions);
            Ui.Add(hero, copy);
            var art = Ui.Art(featured.ArtworkUrl, 234, 15);
            art.Margin = new Thickness(10, 20, 28, 20);
            art.HorizontalAlignment = HorizontalAlignment.Center;
            art.VerticalAlignment = VerticalAlignment.Center;
            Ui.Add(hero, art, 0, 1);
            panel.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(18), Child = hero,
                Background = new LinearGradientBrush
                {
                    StartPoint = new global::Windows.Foundation.Point(0, 0), EndPoint = new global::Windows.Foundation.Point(1, 1),
                    GradientStops = { new GradientStop { Color = Color.FromArgb(255, 92, 39, 49), Offset = 0 }, new GradientStop { Color = Color.FromArgb(255, 37, 23, 41), Offset = .7 }, new GradientStop { Color = Color.FromArgb(255, 21, 20, 29), Offset = 1 } },
                },
            });
        }
        var pills = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        pills.Children.Add(Ui.Pill("Made for you", () => Run(() => NavigateAsync(new Route("Playlists"))), true));
        pills.Children.Add(Ui.Pill("Albums", () => Run(() => NavigateAsync(new Route("Albums")))));
        pills.Children.Add(Ui.Pill("Artists", () => Run(() => NavigateAsync(new Route("Artists")))));
        panel.Children.Add(pills);
        AddSection(panel, "On repeat", "Albums worth coming back to.", AlbumGrid(home.Albums));
        if (home.Playlists.Count > 0) AddSection(panel, "Your soundtrack", "A mood for every moment.", PlaylistGrid(home.Playlists));
        if (home.Artists.Count > 0) AddSection(panel, "Artists in your orbit", "Go a little deeper.", ArtistGrid(home.Artists));
        if (home.Albums.Count == 0 && home.Playlists.Count == 0) panel.Children.Add(Empty("Your collection starts here", "Save an album or a playlist in Spotify, or search for something you love."));
        return panel;
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
        var panel = PageHeading(title, "Your collection, always close.");
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
        var panel = PageHeading("", "");
        var header = Ui.Columns(Ui.Auto, Ui.Star);
        var portrait = Ui.Art(artist.ArtworkUrl, 210, 105);
        portrait.Margin = new Thickness(0, 0, 30, 0);
        Ui.Add(header, portrait);
        var intro = new StackPanel { Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        intro.Children.Add(Ui.Text("ARTIST", 11, true, Ui.Accent));
        var name = Ui.Text(artist.Name, 42, true); name.TextWrapping = TextWrapping.Wrap;
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
        var art = Ui.Art(route.Artwork, 220, 14);
        art.Loaded += (_, _) =>
        {
            if (!Ui.AnimationsEnabled) return;
            Microsoft.UI.Xaml.Media.Animation.ConnectedAnimationService.GetForCurrentView().GetAnimation("AlbumArtwork")?.TryStart(art);
        };
        art.Margin = new Thickness(0, 0, 30, 0);
        Ui.Add(header, art);
        var intro = new StackPanel { Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        intro.Children.Add(Ui.Text(playlist ? "PLAYLIST" : "ALBUM", 11, true, Ui.Accent));
        var title = Ui.WithId(Ui.Text(route.Title, 38, true), "Collection.Title"); title.TextWrapping = TextWrapping.Wrap;
        intro.Children.Add(title);
        var artist = tracks.FirstOrDefault()?.Artist ?? "Your collection";
        var artistButton = new Button { Content = Ui.Text(playlist ? "Your soundtrack" : artist, 15, true, Ui.Accent), Padding = new Thickness(0), Background = Ui.Brush(Colors.Transparent), BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left };
        artistButton.Click += (_, _) => { if (!playlist && route.ArtistId.Length > 0) Run(() => NavigateAsync(new Route("Artist", route.ArtistId))); };
        intro.Children.Add(artistButton);
        intro.Children.Add(Ui.Text($"{tracks.Count} songs  ·  {tracks.Sum(t => (long)t.DurationMs) / 60000} minutes", 12, false, Ui.Muted));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        actions.Children.Add(Ui.WithId(Ui.Pill("▶  Play", () => Run(() => PlayTracksAsync(tracks)), true), "Collection.Play"));
        actions.Children.Add(Ui.Pill("Shuffle", () => Run(async () => { await _music.SetShuffleAsync(true, _lifetime.Token); await PlayTracksAsync(tracks); })));
        intro.Children.Add(actions);
        Ui.Add(header, intro, 0, 1);
        panel.Children.Add(header);
        panel.Children.Add(TrackList(tracks));
        return panel;
    }

    private static void AddSection(StackPanel panel, string title, string subtitle, UIElement content)
    {
        var heading = new StackPanel { Spacing = 4, Margin = new Thickness(0, 10, 0, 0) };
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
        var stack = new StackPanel { Width = 166, Spacing = 7 };
        stack.Children.Add(Ui.Art(artwork, 166, round ? 83 : 10));
        stack.Children.Add(Ui.Text(title, 13, true));
        stack.Children.Add(Ui.Text(subtitle, 12, false, Ui.Muted));
        var button = new Button { Content = stack, Padding = new Thickness(6, 6, 6, 10), Margin = new Thickness(0, 0, 4, 8), Background = Ui.Brush(Colors.Transparent), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(12), HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += (_, _) => { beforeClick?.Invoke((FrameworkElement)stack.Children[0]); click(); }; Ui.Tactile(button);
        ToolTipService.SetToolTip(button, title + " · " + subtitle);
        return button;
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
            () => Run(() => NavigateAsync(new Route("Playlist", playlist.Id, playlist.Name, playlist.ArtworkUrl)))));
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
        public string Artist => Track.Artist + "  ·  " + Track.Album;
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
            MaxHeight = 650, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        list.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Grid Padding="4,7" ColumnSpacing="14" Height="60">
                <Grid.ColumnDefinitions>
                  <ColumnDefinition Width="42"/><ColumnDefinition Width="*"/>
                  <ColumnDefinition Width="46"/><ColumnDefinition Width="40"/>
                </Grid.ColumnDefinitions>
                <Border CornerRadius="5" Width="42" Height="42" Background="#27232E">
                  <Image Source="{Binding Artwork}" Stretch="UniformToFill"/>
                </Border>
                <StackPanel Grid.Column="1" VerticalAlignment="Center" Spacing="4">
                  <TextBlock Text="{Binding Title}" FontSize="13" FontWeight="SemiBold" TextTrimming="CharacterEllipsis"/>
                  <TextBlock Text="{Binding Artist}" FontSize="11" Opacity="0.55" TextTrimming="CharacterEllipsis"/>
                </StackPanel>
                <TextBlock Grid.Column="2" Text="{Binding Duration}" FontSize="12" Opacity="0.55" VerticalAlignment="Center"/>
                <Button x:Name="TrackOptions" Grid.Column="3" Content="•••" Background="Transparent" BorderThickness="0" ToolTipService.ToolTip="Song options" AutomationProperties.Name="Song options"/>
              </Grid>
            </DataTemplate>
            """);
        list.ItemClick += (_, args) => { if (args.ClickedItem is TrackRow row) Run(() => PlayTracksAsync(tracks, row.Index)); };
        list.ContainerContentChanging += (_, args) =>
        {
            if (args.InRecycleQueue || args.Item is not TrackRow row) return;
            args.ItemContainer.ContextFlyout = TrackMenu(row.Track, tracks, row.Index);
            args.RegisterUpdateCallback((_, ready) =>
            {
                if (ready.InRecycleQueue || ready.Item is not TrackRow current) return;
                if (ready.ItemContainer.ContentTemplateRoot is FrameworkElement template && template.FindName("TrackOptions") is Button options)
                    options.Flyout = TrackMenu(current.Track, tracks, current.Index);
            });
        };
        return list;
    }
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
