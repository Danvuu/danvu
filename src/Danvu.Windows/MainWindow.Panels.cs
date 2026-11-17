using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using Danvu.Core;
using Danvu.Windows.Services;
using Danvu.Windows.Views;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;

namespace Danvu.Windows;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _loginCancellation;
    private sealed class QueueEntry(Track track)
    {
        public Track Track { get; } = track;
        public string Title => Track.Title;
        public string Artist => Track.Artist;
        public Uri? Artwork => LocalState.ResolveArtworkUri(Track.ArtworkUrl);
    }
    private bool _dialogOpen;
    private Button _accountButton = null!;

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (_dialogOpen) return ContentDialogResult.None;
        _dialogOpen = true;
        var id = Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(dialog);
        if (!string.IsNullOrEmpty(id))
            dialog.Opened += (_, _) =>
            {
                static Button? CloseButton(DependencyObject parent)
                {
                    for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
                    {
                        var child = VisualTreeHelper.GetChild(parent, index);
                        if (child is Button { Name: "CloseButton" } button) return button;
                        if (CloseButton(child) is { } found) return found;
                    }
                    return null;
                }
                if (CloseButton(dialog) is { } close) Ui.WithId(close, id + ".Close");
            };
        try
        {
            dialog.XamlRoot = _root.XamlRoot;
            dialog.RequestedTheme = ElementTheme.Dark;
            return await dialog.ShowAsync();
        }
        finally { _dialogOpen = false; }
    }

    private async Task ShowAccountAsync()
    {
        if (_loginCancellation is not null)
        {
            var waiting = new ContentDialog { Title = "Sign in with Spotify", Content = "Finish signing in through your browser. You can cancel here and continue exploring the demo.", PrimaryButtonText = "Cancel sign-in", CloseButtonText = "Keep waiting" };
            if (await ShowDialogAsync(waiting) == ContentDialogResult.Primary) _loginCancellation?.Cancel();
            return;
        }
        var body = new StackPanel { Spacing = 14, MaxWidth = 480 };
        var intro = Ui.Text(_music.IsDemo
            ? "Explore the original demo collection, or connect your Spotify account. Demo playback is silent."
            : "Connected to Spotify. Music controls playback on your selected Spotify device.", 14);
        intro.TextWrapping = TextWrapping.Wrap; body.Children.Add(intro);
        if (!_music.IsDemo)
        {
            try { var profile = await _music.GetProfileAsync(_lifetime.Token); body.Children.Add(Ui.Text(profile.DisplayName + " · " + profile.Product, 16, true)); }
            catch (MusicServiceException ex) { Notify(ex.Message); }
        }
        var clientId = new TextBox { Header = "Spotify developer Client ID", Text = LocalState.ClientId, PlaceholderText = "Your app’s public Client ID" };
        body.Children.Add(clientId);
        var instructions = Ui.Text("Add this exact redirect URI in your Spotify developer app:\n" + SpotifyAuthService.RedirectUri + "\n\nSign-in opens your system browser. No client secret or Spotify password is stored by Music. Tokens are encrypted for your Windows account.", 12, false, Ui.Muted);
        instructions.TextWrapping = TextWrapping.Wrap; instructions.IsTextSelectionEnabled = true;
        body.Children.Add(instructions);
        var limitation = Ui.Text("Spotify Premium and an available Connect device are required for playback controls. Spotify audio plays on that device. Spotify’s API does not provide synced lyrics, offline downloads, or native Jam hosting.", 12, false, Ui.Muted);
        limitation.TextWrapping = TextWrapping.Wrap; body.Children.Add(limitation);
        body.Children.Add(Ui.Pill("Spotify developer dashboard ↗", () => Run(async () => { await Launcher.LaunchUriAsync(new Uri("https://developer.spotify.com/dashboard")); })));
        var dialog = Ui.WithId(new ContentDialog { Title = "Music · Your account", Content = body, PrimaryButtonText = _music.IsDemo ? "Sign in with Spotify" : "Reconnect Spotify", SecondaryButtonText = _music.IsDemo ? "" : "Sign out & use demo", CloseButtonText = "Done", DefaultButton = ContentDialogButton.Primary }, "Account.Dialog");
        dialog.PrimaryButtonClick += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(clientId.Text)) return;
            clientId.Description = "Enter your app’s public Client ID to continue."; e.Cancel = true;
        };
        var result = await ShowDialogAsync(dialog);
        LocalState.ClientId = clientId.Text;
        if (result == ContentDialogResult.Primary) await SignInAsync(clientId.Text);
        else if (result == ContentDialogResult.Secondary)
        {
            await _auth.SignOutAsync(_lifetime.Token);
            await ChangeServiceAsync(new DemoMusicService());
        }
    }

    private async Task SignInAsync(string clientId)
    {
        _loginCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        Notify("Complete Spotify sign-in in your browser. Account settings lets you cancel.");
        _accountButton.Content = Ui.Text("Signing in…", 13, true);
        try
        {
            await _auth.SignInAsync(clientId, uri =>
            {
                // PKCE uses the system browser, never an embedded login or web interface.
                _ = Launcher.LaunchUriAsync(uri);
            }, _loginCancellation.Token);
            await ChangeServiceAsync(new SpotifyMusicService(_http, _auth));
        }
        catch (OperationCanceledException) { Notify("Spotify sign-in cancelled. You can try again in account settings."); }
        finally
        {
            _loginCancellation.Dispose(); _loginCancellation = null;
            _accountButton.Content = Ui.Text(_music.IsDemo ? "Connect Spotify" : "Your account", 13, true);
        }
    }

    private async Task ChangeServiceAsync(IMusicService service)
    {
        _seekDebounce.Stop(); _volumeDebounce.Stop();
        _music = service; InvalidateLibrary(); _playback = null; _lyricsTrack = null;
        _back.Clear(); _forward.Clear(); _searchDelay?.Cancel();
        _syncSearch = true; _search.Text = ""; _syncSearch = false;
        BuildSidebar();
        _accountButton.Content = Ui.Text(service.IsDemo ? "Connect Spotify" : "Your account", 13, true);
        if (_lyricsVisible) ToggleLyrics(false);
        await NavigateAsync(new Route("Home"), false);
        await PollAsync();
        if (_queueVisible) await RefreshQueueAsync();
    }

    private async Task ShowDevicesAsync()
    {
        var devices = await _music.GetDevicesAsync(_lifetime.Token);
        var choices = new ListView { ItemsSource = devices.Where(d => !d.IsRestricted).ToArray(), DisplayMemberPath = "Name", SelectionMode = ListViewSelectionMode.Single, MaxHeight = 280 };
        choices.SelectedItem = devices.FirstOrDefault(d => d.IsActive && !d.IsRestricted);
        var body = new StackPanel { Spacing = 14 };
        var text = Ui.Text(_music.IsDemo ? "Demo playback animates the interface without audio." : "Open Spotify on a computer, phone, or speaker so it appears here. Audio will play on the selected device.", 13, false, Ui.Muted); text.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(text); body.Children.Add(choices);
        if (devices.Count == 0) body.Children.Add(Ui.Text("No available devices. Open Spotify and start a track, then try again.", 13));
        var dialog = Ui.WithId(new ContentDialog { Title = "Listen on", Content = body, PrimaryButtonText = "Use device", IsPrimaryButtonEnabled = choices.SelectedItem is not null, CloseButtonText = "Close" }, "Devices.Dialog");
        choices.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = choices.SelectedItem is PlaybackDevice;
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary && choices.SelectedItem is PlaybackDevice device)
        {
            await _music.TransferAsync(device.Id, _lifetime.Token);
            Notify("Playback device: " + device.Name); await PollAsync();
        }
    }

    private async Task ShowJamAsync()
    {
        var body = new StackPanel { Spacing = 14 };
        body.Children.Add(Ui.Text("Bring everyone into the music.", 21, true));
        var message = Ui.Text("Spotify Jam lets people listen and build a queue together. Spotify’s public API doesn’t let Music create, join, or manage a Jam.\n\nOpen the official Spotify app, open the current track or device menu, and choose “Start a Jam” where available. To join, use the invitation link or QR code from your host.\n\nMusic can control your selected Connect device, but it doesn’t synchronize or host the Jam session. Spotify account and device requirements apply.", 14);
        message.TextWrapping = TextWrapping.Wrap; body.Children.Add(message);
        var dialog = Ui.WithId(new ContentDialog { Title = "Spotify Jam", Content = body, PrimaryButtonText = "Open Spotify", CloseButtonText = "Not now", DefaultButton = ContentDialogButton.Primary }, "Jam.Dialog");
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary)
        {
            var uri = _playback?.Track?.Uri;
            if (_music.IsDemo || uri is null || !uri.StartsWith("spotify:", StringComparison.Ordinal)) uri = "spotify:";
            var launched = await Launcher.LaunchUriAsync(new Uri(uri));
            Notify(launched ? "In Spotify, open the track or device menu to start or join your Jam." : "Spotify could not open. Install the official Spotify Windows app to use Jam.");
        }
    }

    private async Task CreatePlaylistAsync()
    {
        var name = new TextBox { Header = "Name", PlaceholderText = "A soundtrack for…", MaxLength = 100 };
        var description = new TextBox { Header = "Description", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 300, Height = 90 };
        var body = new StackPanel { Spacing = 12, Children = { name, description } };
        var dialog = new ContentDialog { Title = "New playlist", Content = body, PrimaryButtonText = "Create", CloseButtonText = "Cancel", IsPrimaryButtonEnabled = false };
        name.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(name.Text);
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        var playlist = await _music.CreatePlaylistAsync(name.Text.Trim(), description.Text.Trim(), _lifetime.Token);
        InvalidateLibrary();
        await NavigateAsync(PlaylistRoute(playlist));
    }
    private async Task AddToPlaylistAsync(Track track)
    {
        var library = await _music.GetLibraryAsync(_lifetime.Token);
        var choices = new ComboBox { ItemsSource = library.Playlists, DisplayMemberPath = "Name", PlaceholderText = "Choose a playlist", HorizontalAlignment = HorizontalAlignment.Stretch };
        var dialog = new ContentDialog { Title = "Add “" + track.Title + "”", Content = choices, PrimaryButtonText = "Add to playlist", CloseButtonText = "Cancel", IsPrimaryButtonEnabled = false };
        choices.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = choices.SelectedItem is Playlist;
        if (library.Playlists.Count == 0) { Notify("Create a playlist first using the + button in your sidebar."); return; }
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary || choices.SelectedItem is not Playlist playlist) return;
        await _music.AddToPlaylistAsync(playlist.Id, [track], _lifetime.Token);
        InvalidateLibrary();
        Notify("Added to " + playlist.Name);
    }

    private async Task ToggleQueueAsync()
    {
        _queueVisible = !_queueVisible;
        if (_queueVisible && _root.ActualWidth < 1180) _sidebarVisible = false;
        _queueHost.Visibility = _queueVisible ? Visibility.Visible : Visibility.Collapsed;
        ApplyResponsiveLayout();
        if (_queueVisible) { await RefreshQueueAsync(); Ui.FadeIn(_queueHost, 0); }
    }
    private async Task RefreshQueueAsync()
    {
        if (!_queueVisible) return;
        var service = _music;
        var queue = await service.GetQueueAsync(_lifetime.Token);
        if (!ReferenceEquals(service, _music)) return;
        var panel = new Grid
        {
            Width = 285, Padding = new Thickness(20),
            Background = new AcrylicBrush { TintColor = Color.FromArgb(255, 27, 27, 31), TintOpacity = .8, FallbackColor = Color.FromArgb(255, 25, 25, 29) },
            BorderBrush = Ui.Brush(Color.FromArgb(20, 255, 255, 255)), BorderThickness = new Thickness(1, 0, 0, 0),
            Margin = new Thickness(0, 0, 0, 0),
        };
        panel.RowDefinitions.Add(new RowDefinition { Height = Ui.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = Ui.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = Ui.Star });
        var heading = Ui.Columns(Ui.Star, Ui.Auto);
        var headingLabel = Ui.Text("Queue", 20, true); headingLabel.VerticalAlignment = VerticalAlignment.Center;
        Ui.Add(heading, Ui.WithId(headingLabel, "Queue.Title"));
        Ui.Add(heading, Ui.WithId(Ui.IconButton("\uE711", "Close queue", () => Run(ToggleQueueAsync), 14), "Queue.Close"), 0, 1);
        Ui.Add(panel, heading);
        var now = new StackPanel { Spacing = 9, Margin = new Thickness(0, 16, 0, 18) };
        now.Children.Add(Ui.Text("NOW PLAYING", 10, true, Ui.Muted));
        if (_playback?.Track is { } current)
        {
            var currentRow = Ui.Columns(Ui.Auto, Ui.Star); currentRow.ColumnSpacing = 12;
            Ui.Add(currentRow, Ui.Art(current.ArtworkUrl, 46, 7));
            var labels = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            labels.Children.Add(Ui.Text(current.Title, 14, true));
            labels.Children.Add(Ui.Text(current.Artist, 12, false, Ui.Muted));
            Ui.Add(currentRow, labels, 0, 1); now.Children.Add(currentRow);
        }
        now.Children.Add(Ui.Text(_music.CanReorderQueue ? "UP NEXT · DRAG TO REORDER" : "UP NEXT · SPOTIFY QUEUE", 10, true, Ui.Muted));
        Ui.Add(panel, now, 1);
        var items = new ObservableCollection<QueueEntry>(queue.Select(track => new QueueEntry(track)));
        var list = new ListView { ItemsSource = items, SelectionMode = ListViewSelectionMode.None, CanDragItems = _music.CanReorderQueue, CanReorderItems = _music.CanReorderQueue, AllowDrop = _music.CanReorderQueue, IsItemClickEnabled = false };
        // Native DataTemplate supplies accessible text and artwork; the data stays strongly typed.
        list.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <Grid Padding="2,8" ColumnSpacing="11" Width="218">
                <Grid.ColumnDefinitions><ColumnDefinition Width="36"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                <Border Width="36" Height="36" CornerRadius="5" Background="#2B2B2D">
                  <Image Source="{Binding Artwork}" Stretch="UniformToFill"/>
                </Border>
                <StackPanel Grid.Column="1" Spacing="3" VerticalAlignment="Center">
                  <TextBlock Text="{Binding Title}" FontSize="13" FontWeight="SemiBold" TextTrimming="CharacterEllipsis" />
                  <TextBlock Text="{Binding Artist}" FontSize="11" Opacity="0.55" TextTrimming="CharacterEllipsis" />
                </StackPanel>
              </Grid>
            </DataTemplate>
            """);
        int dragFrom = -1; QueueEntry? dragged = null;
        list.DragItemsStarting += (_, args) => { dragged = args.Items.OfType<QueueEntry>().FirstOrDefault(); dragFrom = dragged is null ? -1 : items.IndexOf(dragged); };
        list.DragItemsCompleted += (_, _) =>
        {
            if (dragged is null || dragFrom < 0 || !ReferenceEquals(service, _music)) return;
            var to = items.IndexOf(dragged); var from = dragFrom;
            if (to >= 0 && to != from) Run(async () => { await service.MoveQueueItemAsync(from, to, _lifetime.Token); await RefreshQueueAsync(); });
            dragFrom = -1; dragged = null;
        };
        if (queue.Count == 0) Ui.Add(panel, Ui.Text("A little room for your next favorite.\nAdd songs from their ••• menu.", 12, false, Ui.Muted), 2);
        else Ui.Add(panel, list, 2);
        _queueHost.Content = panel;
    }

    private void ToggleLyrics(bool visible)
    {
        _lyricsVisible = visible;
        _lyrics.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible) { Run(async () => { await LoadLyricsAsync(); if (_lyricsVisible) _lyrics.AnimateArtworkFrom(_trackArt); }); Ui.FadeIn(_lyrics, 0); }
    }
    private async Task LoadLyricsAsync(bool force = false)
    {
        var track = _playback?.Track;
        if (track is null)
        {
            Notify("Play a song first, then open lyrics.");
            ToggleLyrics(false); return;
        }
        if (!force && _lyricsTrack == track.Id) return;
        var palette = await _palette.GetAsync(track.ArtworkUrl, _lifetime.Token);
        if (_playback?.Track?.Id != track.Id) return;
        if (!_importedLyrics.TryGetValue(track.Id, out var document))
        {
            var path = LyricsPath(track.Id);
            if (File.Exists(path))
            {
                document = LrcParser.Parse(await File.ReadAllTextAsync(path, _lifetime.Token), track.Title);
                _importedLyrics[track.Id] = document;
            }
            else document = _music.IsDemo ? DemoLyrics.GetForTrack(track) : new LyricsDocument(track.Title, "Spotify doesn’t provide synced lyrics through its public API. Import an LRC file you have permission to use.", []);
        }
        if (_closed || _playback?.Track?.Id != track.Id) return;
        _lyricsTrack = track.Id;
        _lyrics.SetContent(track, document, palette);
        _lyrics.UpdatePlayback(_playback?.PositionMs ?? 0, _playback?.IsPlaying == true);
    }
    private static string LyricsPath(string trackId)
    {
        var folder = LocalState.FilePath("Lyrics"); Directory.CreateDirectory(folder);
        return Path.Combine(folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trackId))) + ".lrc");
    }
    private async Task ImportLyricsAsync()
    {
        var track = _playback?.Track;
        if (track is null) { Notify("Play a song before importing its lyrics."); return; }
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".lrc");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        var properties = await file.GetBasicPropertiesAsync();
        if (properties.Size > 1_000_000) { Notify("Choose an LRC file smaller than 1 MB."); return; }
        var contents = await global::Windows.Storage.FileIO.ReadTextAsync(file);
        var document = LrcParser.Parse(contents, track.Title);
        if (document.Lines.Count == 0 || document.Lines.Count > 2000) { Notify("Choose a timestamped LRC file with between 1 and 2,000 lyric lines."); return; }
        await File.WriteAllTextAsync(LyricsPath(track.Id), contents, _lifetime.Token);
        _importedLyrics[track.Id] = document;
        Notify("Imported lyrics for " + track.Title);
        if (_playback?.Track?.Id == track.Id) await LoadLyricsAsync(true);
    }
}
