using System.Numerics;
using Danvu.Core;
using Danvu.Windows.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Danvu.Windows.Views;

/// <summary>
/// A native, keyboard-accessible synchronized lyrics surface. The playback clock is
/// supplied by the music service; there is no separate lyrics timer to drift out of sync.
/// </summary>
public sealed class LyricsPanel : Grid
{
    private readonly Func<int, Task> _seek;
    private readonly Action<string> _report;
    private readonly Grid _atmosphere = new();
    private readonly Grid _body = new();
    private readonly ColumnDefinition _artColumn = new() { Width = new GridLength(0.88, GridUnitType.Star) };
    private readonly ScrollViewer _scroll = new();
    private readonly StackPanel _lineStack = new() { Spacing = 14 };
    private readonly Border _topSpace = new();
    private readonly Border _bottomSpace = new();
    private readonly StackPanel _artPanel = new() { Spacing = 18, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _artBorder = new() { CornerRadius = new CornerRadius(14), Height = 310, Width = 310 };
    private readonly Image _art = new() { Stretch = Stretch.UniformToFill };
    private readonly TextBlock _title = Label("", 28, 1);
    private readonly TextBlock _artist = Label("", 19, 0.64);
    private readonly TextBlock _source = Label("", 11, 0.52);
    private readonly Button _jump;
    private readonly List<LyricRow> _rows = [];
    private readonly UISettings _uiSettings = new();
    private readonly Grid _lyricRegion = new();
    private readonly Button _import;
    private LyricsDocument? _document;
    private string? _trackId;
    private int _activeIndex = -1;
    private int _position;
    private bool _following = true;
    private bool _isPlaying;
    private bool _loaded;
    private ConnectedAnimation? _artworkAnimation;

    public event EventHandler? CloseRequested;
    public event EventHandler? ImportRequested;

    public LyricsPanel(Func<int, Task> seek, Action<string> report)
    {
        _seek = seek;
        _report = report;
        RequestedTheme = ElementTheme.Dark;
        Background = new SolidColorBrush(Color.FromArgb(255, 25, 21, 30));
        CornerRadius = new CornerRadius(16);
        Children.Add(_atmosphere);
        Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), IsHitTestVisible = false });

        var layout = new Grid { Padding = new Thickness(34, 24, 34, 18), RowSpacing = 14 };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Children.Add(layout);

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { Label("♫", 20, 0.85), Label("LYRICS", 12, 0.65) },
            VerticalAlignment = VerticalAlignment.Center
        });
        var close = QuietButton("⌄", "Close lyrics", 26);
        AutomationProperties.SetAutomationId(close, "Lyrics.Close");
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        layout.Children.Add(header);

        _body.ColumnSpacing = 62;
        _body.ColumnDefinitions.Add(_artColumn);
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
        Grid.SetRow(_body, 1);
        layout.Children.Add(_body);

        _artBorder.Child = _art;
        _artBorder.Background = new LinearGradientBrush
        {
            StartPoint = new global::Windows.Foundation.Point(0, 0), EndPoint = new global::Windows.Foundation.Point(1, 1),
            GradientStops = { new GradientStop { Color = Color.FromArgb(255, 117, 56, 77), Offset = 0 }, new GradientStop { Color = Color.FromArgb(255, 31, 36, 73), Offset = 1 } }
        };
        _artPanel.Children.Add(_artBorder);
        _title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _title.TextWrapping = TextWrapping.WrapWholeWords;
        _artPanel.Children.Add(new StackPanel { Spacing = 5, Children = { _title, _artist } });
        _artPanel.Children.Add(Label("Follow every word.", 13, 0.42));
        _body.Children.Add(_artPanel);

        Grid.SetColumn(_lyricRegion, 1);
        _body.Children.Add(_lyricRegion);
        _scroll.Content = _lineStack;
        _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _scroll.HorizontalScrollMode = ScrollMode.Disabled;
        _scroll.VerticalScrollMode = ScrollMode.Enabled;
        _scroll.ZoomMode = ZoomMode.Disabled;
        _scroll.IsTabStop = true;
        AutomationProperties.SetAutomationId(_scroll, "Lyrics.Scroll");
        AutomationProperties.SetName(_scroll, "Synchronized lyrics. Scroll to browse; choose Jump to current to resume following.");
        _lyricRegion.Children.Add(_scroll);

        // Fade the viewport edges rather than rasterizing every line into a blur
        // surface. Text remains accessible and all per-line motion is composited.
        var topFade = EdgeFade(true);
        var bottomFade = EdgeFade(false);
        _lyricRegion.Children.Add(topFade);
        _lyricRegion.Children.Add(bottomFade);

        _scroll.AddHandler(PointerWheelChangedEvent, new PointerEventHandler((_, _) => PauseFollowing()), true);
        _scroll.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => PauseFollowing()), true);
        _scroll.AddHandler(KeyDownEvent, new KeyEventHandler((_, args) =>
        {
            if (args.Key is VirtualKey.Up or VirtualKey.Down or VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Home or VirtualKey.End)
                PauseFollowing();
        }), true);
        _scroll.SizeChanged += (_, _) =>
        {
            _topSpace.Height = Math.Max(40, _scroll.ViewportHeight * 0.39);
            _bottomSpace.Height = Math.Max(80, _scroll.ViewportHeight * 0.48);
            if (_following) FollowCurrent(false);
        };

        var footer = new Grid { ColumnSpacing = 14 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _source.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(_source, "Lyrics.Source");
        _source.TextWrapping = TextWrapping.WrapWholeWords;
        footer.Children.Add(_source);
        _jump = QuietButton("↑  Jump to current", "Jump to current lyric", 13);
        _jump.Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
        _jump.CornerRadius = new CornerRadius(20);
        _jump.Visibility = Visibility.Collapsed;
        _jump.Click += (_, _) => ResumeFollowing();
        Grid.SetColumn(_jump, 1);
        footer.Children.Add(_jump);
        _import = QuietButton("Import .lrc", "Import a synchronized LRC lyrics file for this track", 12);
        _import.Click += (_, _) => ImportRequested?.Invoke(this, EventArgs.Empty);
        Grid.SetColumn(_import, 2);
        footer.Children.Add(_import);
        Grid.SetRow(footer, 2);
        layout.Children.Add(footer);

        SizeChanged += (_, args) =>
        {
            _atmosphere.Clip = new RectangleGeometry
            {
                Rect = new global::Windows.Foundation.Rect(0, 0, args.NewSize.Width, args.NewSize.Height)
            };
            var compact = args.NewSize.Width < 900;
            _artPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            _artColumn.Width = compact ? new GridLength(0) : new GridLength(0.88, GridUnitType.Star);
            _body.ColumnSpacing = compact ? 0 : 62;
            var coverSize = Math.Clamp(args.NewSize.Width * 0.28, 180, 360);
            _artBorder.Width = coverSize;
            _artBorder.Height = coverSize;
            foreach (var row in _rows) row.Text.FontSize = compact ? 34 : 42;
        };
        Loaded += (_, _) =>
        {
            _loaded = true;
            UpdateArtworkScale();
            FollowCurrent(false);
        };
        Unloaded += (_, _) =>
        {
            _loaded = false;
            CancelArtworkAnimation();
        };
    }

    public void SetContent(Track track, LyricsDocument document, IReadOnlyList<Color> colors)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(document);
        if (_trackId == track.Id && Equals(_document, document)) return;
        _trackId = track.Id;
        _document = document;
        _title.Text = track.Title;
        _artist.Text = track.Artist;
        _source.Text = document.Source;
        AutomationProperties.SetName(_art, $"{track.Album} album artwork");
        try
        {
            var artworkUri = LocalState.ResolveArtworkUri(track.ArtworkUrl);
            _art.Source = artworkUri is null ? null : new BitmapImage(artworkUri);
        }
        catch (Exception error) when (error is UriFormatException or ArgumentException) { _art.Source = null; }
        CrossfadeAtmosphere(colors);

        _rows.Clear();
        _lineStack.Children.Clear();
        _lineStack.Children.Add(_topSpace);
        _activeIndex = -2; // Force initial styling, including an instrumental introduction.
        _position = 0;
        _following = true;
        _jump.Visibility = Visibility.Collapsed;

        foreach (var line in document.Lines.OrderBy(line => line.TimeMs))
        {
            var text = Label(string.IsNullOrWhiteSpace(line.Text) ? "•••" : line.Text, ActualWidth < 900 ? 34 : 42, 1);
            text.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
            text.TextWrapping = TextWrapping.WrapWholeWords;
            text.LineHeight = ActualWidth < 900 ? 42 : 52;
            var button = new Button
            {
                Content = text,
                Padding = new Thickness(3, 10, 10, 10),
                Margin = new Thickness(5, 0, 12, 0),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                CornerRadius = new CornerRadius(8),
                Opacity = 1
            };
            AutomationProperties.SetName(button, $"Seek to {TimeSpan.FromMilliseconds(line.TimeMs):m\\:ss}: {line.Text}");
            AutomationProperties.SetAutomationId(button, $"Lyrics.Line.{_rows.Count}");
            button.Click += async (_, _) =>
            {
                try
                {
                    await _seek(line.TimeMs);
                    _position = line.TimeMs;
                    UpdatePlayback(line.TimeMs, _isPlaying);
                    ResumeFollowing();
                }
                catch (OperationCanceledException) { /* Closing the app cancels pending seeks. */ }
                catch (Exception error) { _report(error.Message); }
            };
            _rows.Add(new LyricRow(line, button, text));
            _lineStack.Children.Add(button);
        }
        if (_rows.Count == 0)
        {
            var empty = new StackPanel { Spacing = 18, Margin = new Thickness(5, 0, 15, 0) };
            var emptyTitle = Label("The words are yours.", 38, 0.95);
            emptyTitle.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
            emptyTitle.TextWrapping = TextWrapping.WrapWholeWords;
            empty.Children.Add(emptyTitle);
            var description = Label("Spotify doesn't provide synchronized lyrics through its public API. Import an LRC file for this song to follow along.", 17, 0.6);
            description.TextWrapping = TextWrapping.WrapWholeWords;
            empty.Children.Add(description);
            _lineStack.Children.Add(empty);
        }
        _lineStack.Children.Add(_bottomSpace);
        _scroll.ChangeView(null, 0, null, true);
        UpdatePlayback(0, _isPlaying);
    }

    /// <summary>Connects the compact player's cover to the large Now Playing artwork.</summary>
    public void AnimateArtworkFrom(FrameworkElement source)
    {
        CancelArtworkAnimation();
        if (!AnimationsEnabled || Visibility != Visibility.Visible ||
            _artPanel.Visibility != Visibility.Visible || !source.IsLoaded ||
            source.Visibility != Visibility.Visible || source.ActualWidth <= 0 || source.ActualHeight <= 0)
            return;
        var animation = ConnectedAnimationService.GetForCurrentView()
            .PrepareToAnimate("NowPlayingArtwork", source);
        animation.Configuration = new BasicConnectedAnimationConfiguration();
        _artworkAnimation = animation;
        _artBorder.LayoutUpdated += ArtworkLayoutUpdated;
        // Visibility changes can arrange the cover after the caller's continuation.
        // Starting at the first completed layout avoids animating to stale bounds.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => TryStartArtworkAnimation());
    }

    private void ArtworkLayoutUpdated(object? sender, object args) => TryStartArtworkAnimation();

    private void TryStartArtworkAnimation()
    {
        if (_artworkAnimation is null) return;
        if (!AnimationsEnabled || Visibility != Visibility.Visible || _artPanel.Visibility != Visibility.Visible)
        {
            CancelArtworkAnimation();
            return;
        }
        if (!_artBorder.IsLoaded || _artBorder.ActualWidth <= 0 || _artBorder.ActualHeight <= 0) return;
        _artBorder.LayoutUpdated -= ArtworkLayoutUpdated;
        var animation = _artworkAnimation;
        _artworkAnimation = null;
        if (!animation.TryStart(_artBorder)) animation.Cancel();
    }

    private void CancelArtworkAnimation()
    {
        _artBorder.LayoutUpdated -= ArtworkLayoutUpdated;
        _artworkAnimation?.Cancel();
        _artworkAnimation = null;
    }

    public void UpdatePlayback(int positionMs, bool isPlaying)
    {
        _position = Math.Max(0, positionMs);
        if (_isPlaying != isPlaying)
        {
            _isPlaying = isPlaying;
            UpdateArtworkScale();
        }
        var next = -1;
        for (var index = 0; index < _rows.Count; index++)
        {
            if (_rows[index].Line.TimeMs > _position) break;
            next = index;
        }
        if (next == _activeIndex) return;
        _activeIndex = next;
        for (var index = 0; index < _rows.Count; index++)
            AnimateRow(_rows[index], index == _activeIndex, index < _activeIndex);
        if (_following) FollowCurrent(true);
    }

    private void PauseFollowing()
    {
        if (_rows.Count == 0) return;
        _following = false;
        _jump.Visibility = Visibility.Visible;
    }

    private void ResumeFollowing()
    {
        _following = true;
        _jump.Visibility = Visibility.Collapsed;
        FollowCurrent(true);
    }

    private void FollowCurrent(bool animate)
    {
        if (!_loaded || _activeIndex < 0 || _activeIndex >= _rows.Count || _scroll.ViewportHeight <= 0) return;
        var row = _rows[_activeIndex].Button;
        if (row.ActualHeight <= 0) return;
        var position = row.TransformToVisual(_lineStack).TransformPoint(new global::Windows.Foundation.Point(0, 0));
        var target = Math.Clamp(position.Y - _scroll.ViewportHeight * 0.38, 0, Math.Max(0, _scroll.ScrollableHeight));
        _scroll.ChangeView(null, target, null, !animate || !AnimationsEnabled);
    }

    private bool AnimationsEnabled => _uiSettings.AnimationsEnabled;

    private void AnimateRow(LyricRow row, bool active, bool previous)
    {
        var visual = ElementCompositionPreview.GetElementVisual(row.Button);
        visual.CenterPoint = new Vector3(0, (float)row.Button.ActualHeight / 2, 0);
        var opacity = active ? 1f : previous ? 0.25f : 0.40f;
        var scale = active ? Vector3.One : new Vector3(0.94f, 0.94f, 1);
        if (!AnimationsEnabled || !_loaded)
        {
            visual.Opacity = opacity;
            visual.Scale = scale;
            return;
        }
        var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, opacity);
        fade.Duration = TimeSpan.FromMilliseconds(420);
        visual.StartAnimation("Opacity", fade);
        var spring = visual.Compositor.CreateSpringVector3Animation();
        spring.FinalValue = scale;
        spring.DampingRatio = 0.84f;
        spring.Period = TimeSpan.FromMilliseconds(320);
        visual.StartAnimation("Scale", spring);
    }

    private void UpdateArtworkScale()
    {
        var visual = ElementCompositionPreview.GetElementVisual(_artBorder);
        visual.CenterPoint = new Vector3((float)_artBorder.ActualWidth / 2, (float)_artBorder.ActualHeight / 2, 0);
        var target = _isPlaying ? Vector3.One : new Vector3(0.92f, 0.92f, 1);
        if (!AnimationsEnabled || !_loaded) { visual.Scale = target; return; }
        var spring = visual.Compositor.CreateSpringVector3Animation();
        spring.FinalValue = target;
        spring.DampingRatio = 0.85f;
        spring.Period = TimeSpan.FromMilliseconds(420);
        visual.StartAnimation("Scale", spring);
    }

    private void CrossfadeAtmosphere(IReadOnlyList<Color> colors)
    {
        Color[] fallback = [Color.FromArgb(255, 100, 44, 72), Color.FromArgb(255, 38, 41, 84), Color.FromArgb(255, 101, 69, 45)];
        var palette = colors.Count >= 3 ? colors : fallback;
        var layer = new Grid { IsHitTestVisible = false };
        layer.Background = new LinearGradientBrush
        {
            StartPoint = new global::Windows.Foundation.Point(0, 0), EndPoint = new global::Windows.Foundation.Point(1, 1),
            GradientStops =
            {
                new GradientStop { Color = Shade(palette[0], 0.43), Offset = 0 },
                new GradientStop { Color = Shade(palette[1], 0.44), Offset = 0.55 },
                new GradientStop { Color = Shade(palette[2], 0.36), Offset = 1 }
            }
        };
        // Large radial light pools avoid bitmap effects and remain vector based at
        // every window size. Only their composition transforms animate.
        for (var index = 0; index < 3; index++)
        {
            var color = palette[index];
            var glow = new Ellipse
            {
                Width = 1050, Height = 1050, Opacity = 0.45,
                HorizontalAlignment = index == 1 ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                VerticalAlignment = index == 0 ? VerticalAlignment.Top : VerticalAlignment.Bottom,
                Margin = index == 0 ? new Thickness(-350, -450, 0, 0) : index == 1 ? new Thickness(0, 0, -420, -350) : new Thickness(-200, 0, 0, -650),
                Fill = new RadialGradientBrush
                {
                    GradientStops =
                    {
                        new GradientStop { Color = Color.FromArgb(180, color.R, color.G, color.B), Offset = 0 },
                        new GradientStop { Color = Color.FromArgb(0, color.R, color.G, color.B), Offset = 1 }
                    }
                }
            };
            layer.Children.Add(glow);
            if (AnimationsEnabled)
            {
                var visual = ElementCompositionPreview.GetElementVisual(glow);
                ElementCompositionPreview.SetIsTranslationEnabled(glow, true);
                visual.Properties.InsertVector3("Translation", Vector3.Zero);
                var drift = visual.Compositor.CreateVector3KeyFrameAnimation();
                drift.InsertKeyFrame(0, Vector3.Zero);
                drift.InsertKeyFrame(0.5f, new Vector3(index == 1 ? -80 : 110, index == 0 ? 80 : -70, 0));
                drift.InsertKeyFrame(1, Vector3.Zero);
                drift.Duration = TimeSpan.FromSeconds(24 + index * 7);
                drift.IterationBehavior = AnimationIterationBehavior.Forever;
                visual.StartAnimation("Translation", drift);
            }
        }
        var oldLayers = _atmosphere.Children.ToArray();
        _atmosphere.Children.Add(layer);
        if (!AnimationsEnabled || !_loaded)
        {
            foreach (var old in oldLayers) _atmosphere.Children.Remove(old);
            return;
        }
        var layerVisual = ElementCompositionPreview.GetElementVisual(layer);
        layerVisual.Opacity = 0;
        var compositor = layerVisual.Compositor;
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, 1);
        fade.Duration = TimeSpan.FromMilliseconds(1200);
        layerVisual.StartAnimation("Opacity", fade);
        batch.Completed += (_, _) =>
        {
            foreach (var old in oldLayers) _atmosphere.Children.Remove(old);
        };
        batch.End();
    }

    private static Color Shade(Color color, double amount) => Color.FromArgb(255,
        (byte)(color.R * amount), (byte)(color.G * amount), (byte)(color.B * amount));

    private static Border EdgeFade(bool top) => new()
    {
        Height = 72,
        VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom,
        IsHitTestVisible = false,
        Background = new LinearGradientBrush
        {
            StartPoint = new global::Windows.Foundation.Point(0, top ? 0 : 1),
            EndPoint = new global::Windows.Foundation.Point(0, top ? 1 : 0),
            GradientStops =
            {
                new GradientStop { Color = Color.FromArgb(130, 20, 17, 27), Offset = 0 },
                new GradientStop { Color = Color.FromArgb(0, 20, 17, 27), Offset = 1 }
            }
        }
    };

    private static TextBlock Label(string text, double size, double opacity) => new()
    {
        Text = text, FontSize = size, Opacity = opacity,
        Foreground = new SolidColorBrush(Colors.White),
        FontFamily = new FontFamily("Segoe UI Variable Display")
    };

    private static Button QuietButton(string text, string accessibleName, double size)
    {
        var button = new Button
        {
            Content = Label(text, size, 0.9),
            Background = new SolidColorBrush(Color.FromArgb(0, 255, 255, 255)),
            BorderThickness = new Thickness(0), Padding = new Thickness(14, 9, 14, 9),
            CornerRadius = new CornerRadius(12), VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(button, accessibleName);
        ToolTipService.SetToolTip(button, accessibleName);
        return button;
    }

    private sealed record LyricRow(LyricLine Line, Button Button, TextBlock Text);
}
