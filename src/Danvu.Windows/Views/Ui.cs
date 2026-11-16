using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Danvu.Windows.Views;

internal static class Ui
{
    public static Color Accent => Color.FromArgb(255, 250, 65, 87);
    public static Color Muted => Color.FromArgb(255, 157, 156, 167);
    public static SolidColorBrush Brush(Color color) => new(color);
    public static SolidColorBrush White => Brush(Colors.White);
    public static SolidColorBrush Subtle => Brush(Muted);
    public static TextBlock Text(string text, double size = 14, bool bold = false, Color? color = null)
        => new()
        {
            Text = text, FontSize = size, Foreground = Brush(color ?? Colors.White),
            FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontFamily = new FontFamily("Segoe UI Variable"),
        };
    public static FontIcon Icon(string glyph, double size = 18, Color? color = null)
        => new() { Glyph = glyph, FontSize = size, FontFamily = new FontFamily("Segoe Fluent Icons"), Foreground = Brush(color ?? Colors.White) };
    public static Button IconButton(string glyph, string label, Action action, double size = 18)
    {
        var button = new Button
        {
            Content = Icon(glyph, size), Background = Brush(Colors.Transparent),
            BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(10),
            MinWidth = 36, MinHeight = 36, Padding = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, label);
        button.Click += (_, _) => action();
        Tactile(button);
        return button;
    }
    public static Button Pill(string text, Action action, bool filled = false)
    {
        var b = new Button
        {
            Content = Text(text, 13, true), CornerRadius = new CornerRadius(18),
            Background = Brush(filled ? Accent : Color.FromArgb(255, 44, 43, 49)),
            BorderThickness = new Thickness(0), Padding = new Thickness(18, 9, 18, 9),
        };
        b.Click += (_, _) => action();
        Tactile(b);
        return b;
    }
    public static Border Art(string url, double size, double radius = 12)
    {
        var image = new Image { Stretch = Stretch.UniformToFill, Width = size, Height = size };
        var uri = Danvu.Windows.Services.LocalState.ResolveArtworkUri(url);
        if (uri is not null) image.Source = new BitmapImage(uri) { DecodePixelWidth = (int)(size * 2) };
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(radius),
            Background = Brush(Color.FromArgb(255, 44, 39, 48)), Child = image,
        };
    }
    public static Grid Columns(params GridLength[] widths)
    {
        var g = new Grid();
        foreach (var w in widths) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        return g;
    }
    public static GridLength Star => new(1, GridUnitType.Star);
    public static GridLength Auto => GridLength.Auto;
    public static void Add(Grid grid, UIElement element, int row = 0, int column = 0)
    {
        if (element is FrameworkElement framework) { Grid.SetRow(framework, row); Grid.SetColumn(framework, column); }
        grid.Children.Add(element);
    }
    public static bool AnimationsEnabled
    {
        get { try { return new UISettings().AnimationsEnabled; } catch { return true; } }
    }
    public static void FadeIn(UIElement element, double offset = 12)
    {
        if (!AnimationsEnabled) return;
        var v = ElementCompositionPreview.GetElementVisual(element);
        v.Opacity = 0;
        var fade = v.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, 1); fade.Duration = TimeSpan.FromMilliseconds(320);
        v.StartAnimation("Opacity", fade);
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var move = v.Compositor.CreateVector3KeyFrameAnimation();
        move.InsertKeyFrame(0, new Vector3(0, (float)offset, 0));
        move.InsertKeyFrame(1, Vector3.Zero, v.Compositor.CreateCubicBezierEasingFunction(new Vector2(.2f, .8f), new Vector2(.2f, 1)));
        move.Duration = TimeSpan.FromMilliseconds(420);
        v.StartAnimation("Translation", move);
    }
    public static void Tactile(FrameworkElement element)
    {
        element.SizeChanged += (_, _) => ElementCompositionPreview.GetElementVisual(element).CenterPoint = new Vector3((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);
        void Scale(float scale)
        {
            if (!AnimationsEnabled) return;
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
            animation.InsertKeyFrame(1, new Vector3(scale, scale, 1));
            animation.Duration = TimeSpan.FromMilliseconds(160);
            visual.StartAnimation("Scale", animation);
        }
        element.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => Scale(.96f)), true);
        element.AddHandler(UIElement.PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => Scale(1)), true);
        element.PointerCanceled += (_, _) => Scale(1);
        element.PointerCaptureLost += (_, _) => Scale(1);
        element.PointerExited += (_, _) => Scale(1);
    }
}
