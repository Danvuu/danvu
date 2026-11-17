using System.Numerics;
using System.Runtime.InteropServices;
using Danvu.Windows.Services;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace Danvu.Windows.Views;

/// <summary>A native composition path morph; the containing Button owns input and accessibility.</summary>
internal sealed class PlayPauseGlyph : Grid
{
    private CanvasGeometry? _playGeometry;
    private CanvasGeometry? _pauseGeometry;
    private CompositionPath? _playPath;
    private CompositionPath? _pausePath;
    private CompositionPathGeometry? _pathGeometry;
    private CompositionSpriteShape? _shape;
    private CompositionColorBrush? _fill;
    private ShapeVisual? _visual;
    private PathKeyFrameAnimation? _morph;
    private CubicBezierEasingFunction? _easing;
    private bool _isPlaying;
    private bool _usingStaticGlyph;

    public PlayPauseGlyph()
    {
        Width = 25;
        Height = 25;
        IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        Loaded += (_, _) => CreateVisual();
        Unloaded += (_, _) => ReleaseVisual();
    }

    public void SetPlaying(bool isPlaying, bool animate = true)
    {
        if (_isPlaying == isPlaying) return;
        _isPlaying = isPlaying;
        if (_usingStaticGlyph) { ShowStaticGlyph(); return; }
        if (_pathGeometry is null || _visual is null) return;
        try { ApplyPlaying(animate); }
        catch (Exception exception) when (exception is COMException or NotSupportedException)
        { ActivateFallback(exception); }
    }

    private void ApplyPlaying(bool animate)
    {
        var geometry = _pathGeometry;
        if (geometry is null) return;
        var destination = _isPlaying ? _pausePath! : _playPath!;
        if (!animate || !Ui.AnimationsEnabled)
        {
            geometry.StopAnimation(nameof(CompositionPathGeometry.Path));
            geometry.Path = destination;
            return;
        }

        // Omitting frame 0 starts from the compositor's current presentation value.
        // A second click therefore reverses the live shape, including mid-transition.
        _morph!.InsertKeyFrame(1, destination, _easing!);
        geometry.StartAnimation(nameof(CompositionPathGeometry.Path), _morph);
    }

    private void CreateVisual()
    {
        if (_usingStaticGlyph) { ShowStaticGlyph(); return; }
        if (_visual is not null) return;
        try { CreateCompositionVisual(); }
        catch (Exception exception) when (exception is COMException or NotSupportedException)
        { ActivateFallback(exception); }
    }

    private void CreateCompositionVisual()
    {
        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        var device = CanvasDevice.GetSharedDevice();
        // Keep both Win2D source geometries alive until their CompositionPaths are detached.
        _playGeometry = BuildGeometry(device, false);
        _pauseGeometry = BuildGeometry(device, true);
        _playPath = new CompositionPath(_playGeometry);
        _pausePath = new CompositionPath(_pauseGeometry);
        _pathGeometry = compositor.CreatePathGeometry(_isPlaying ? _pausePath : _playPath);
        _fill = compositor.CreateColorBrush(Colors.White);
        _shape = compositor.CreateSpriteShape(_pathGeometry);
        _shape.FillBrush = _fill;
        _visual = compositor.CreateShapeVisual();
        _visual.Size = new Vector2(25, 25);
        _visual.Shapes.Add(_shape);
        _easing = compositor.CreateCubicBezierEasingFunction(new Vector2(.22f, .8f), new Vector2(.25f, 1));
        _morph = compositor.CreatePathKeyFrameAnimation();
        _morph.Duration = TimeSpan.FromMilliseconds(240);
        _morph.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;
        ElementCompositionPreview.SetElementChildVisual(this, _visual);
    }

    private static CanvasGeometry BuildGeometry(CanvasDevice device, bool pause)
    {
        using var builder = new CanvasPathBuilder(device);
        // Both endpoints contain the same two clockwise closed four-point figures.
        // The repeated right-hand play point preserves topology during interpolation.
        if (pause)
        {
            Quad(builder, new(6, 3), new(10, 3), new(10, 21), new(6, 21));
            Quad(builder, new(14, 3), new(18, 3), new(18, 21), new(14, 21));
        }
        else
        {
            Quad(builder, new(6, 3), new(13.5f, 7.5f), new(13.5f, 16.5f), new(6, 21));
            Quad(builder, new(13.5f, 7.5f), new(21, 12), new(21, 12), new(13.5f, 16.5f));
        }
        return CanvasGeometry.CreatePath(builder);
    }

    private static void Quad(CanvasPathBuilder builder, Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        builder.BeginFigure(a);
        builder.AddLine(b);
        builder.AddLine(c);
        builder.AddLine(d);
        builder.EndFigure(CanvasFigureLoop.Closed);
    }

    private void ActivateFallback(Exception exception)
    {
        _usingStaticGlyph = true;
        StartupDiagnostics.Record("Music transport path morph unavailable; using the static native play/pause glyph", exception);
        ReleaseVisual();
        ShowStaticGlyph();
    }

    private void ShowStaticGlyph()
    {
        Children.Clear();
        Children.Add(Ui.Icon(_isPlaying ? "\uE769" : "\uE768", 25));
    }

    private void ReleaseVisual()
    {
        // Device loss can also surface while detaching. Finish releasing the remaining
        // objects and record only the first hardware/unsupported cleanup error.
        Exception? cleanupFailure = null;
        void Release(Action action)
        {
            try { action(); }
            catch (Exception exception) when (exception is COMException or NotSupportedException)
            { cleanupFailure ??= exception; }
        }
        Release(() => ElementCompositionPreview.SetElementChildVisual(this, null));
        Release(() => _pathGeometry?.StopAnimation(nameof(CompositionPathGeometry.Path)));
        Release(() => _visual?.Shapes.Clear());
        foreach (var resource in new IDisposable?[] { _morph, _easing, _visual, _shape, _fill, _pathGeometry, _playGeometry, _pauseGeometry })
            if (resource is not null) Release(resource.Dispose);
        _visual = null;
        _morph = null;
        _easing = null;
        _shape = null;
        _fill = null;
        _pathGeometry = null;
        _playPath = null;
        _pausePath = null;
        _playGeometry = null;
        _pauseGeometry = null;
        if (cleanupFailure is not null)
            StartupDiagnostics.Record("Music transport composition cleanup encountered a device error", cleanupFailure);
    }
}
