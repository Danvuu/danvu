using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Markup;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Danvu.Windows.Views;

internal static class Ui
{
    public static Color Accent => Color.FromArgb(255, 250, 36, 60);
    public static Color Muted => Color.FromArgb(255, 155, 155, 164);
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
    // Native vector shapes keep navigation and transport optically consistent at every DPI.
    // Unknown symbols fall back to the platform font without changing callers' control semantics.
    public static FrameworkElement Icon(string glyph, double size = 18, Color? color = null)
    {
        var data = glyph switch
        {
            "\uE768" => "M7,3 L21,12 L7,21 Z",
            "\uE769" => "M5,3 L10,3 L10,21 L5,21 Z M14,3 L19,3 L19,21 L14,21 Z",
            "\uE893" => "M3,4 L16,12 L3,20 Z M18,4 L21,4 L21,20 L18,20 Z",
            "\uE892" => "M21,4 L8,12 L21,20 Z M3,4 L6,4 L6,20 L3,20 Z",
            "\uE80F" => "M3,10 L12,3 L21,10 M5,9 L5,21 L10,21 L10,14 L14,14 L14,21 L19,21 L19,9",
            "\uE721" => "M17,10 A7,7 0 1 1 3,10 A7,7 0 1 1 17,10 M15,15 L21,21",
            "\uE76B" => "M15,4 L7,12 L15,20",
            "\uE76C" => "M9,4 L17,12 L9,20",
            "\uE700" => "M3,5 L21,5 M3,12 L21,12 M3,19 L21,19",
            "\uE710" => "M12,4 L12,20 M4,12 L20,12",
            "\uE711" => "M5,5 L19,19 M19,5 L5,19",
            "\uE712" => "M5,12 A1,1 0 1 1 3,12 A1,1 0 1 1 5,12 M13,12 A1,1 0 1 1 11,12 A1,1 0 1 1 13,12 M21,12 A1,1 0 1 1 19,12 A1,1 0 1 1 21,12",
            "\uE823" => "M21,12 A9,9 0 1 1 3,12 A9,9 0 1 1 21,12 M12,6 L12,12 L16,14",
            "\uE8D6" => "M9,17 L9,5 L20,3 L20,15 M9,9 L20,7 M9,17 C9,20 3,22 3,18 C3,15 9,14 9,17 M20,15 C20,18 14,20 14,16 C14,13 20,12 20,15",
            "\uE8F1" => "M5,3 L5,21 M10,3 L10,21 M15,4 L20,3 L23,20 L18,21 Z",
            "\uE93C" => "M4,4 L20,4 L20,20 L4,20 Z M16,12 A4,4 0 1 1 8,12 A4,4 0 1 1 16,12 M12,12 L12.01,12",
            "\uE77B" => "M16,7 A4,4 0 1 1 8,7 A4,4 0 1 1 16,7 M4,21 C4,12 20,12 20,21",
            "\uEB51" or "\uEB52" => "M12,21 L3.5,12.5 C-2,6 6,0 12,7 C18,0 26,6 20.5,12.5 Z",
            "\uE8B1" => "M3,6 L6,6 C11,6 13,18 18,18 L21,18 M17,14 L21,18 L17,22 M3,18 L6,18 C8,18 9,16 10,14 M14,10 C15,7 16,6 18,6 L21,6 M17,2 L21,6 L17,10",
            "\uE8EE" => "M19,7 L6,7 C0,7 0,18 6,18 L8,18 M16,18 L18,18 C24,18 24,7 18,7 M15,3 L19,7 L15,11 M9,14 L5,18 L9,22",
            "\uE8ED" => "M19,7 L6,7 C0,7 0,18 6,18 L8,18 M16,18 L18,18 C24,18 24,7 18,7 M15,3 L19,7 L15,11 M11,11 L13,10 L13,16",
            "\uE767" => "M3,9 L7,9 L12,5 L12,19 L7,15 L3,15 Z M16,8 C19,10 19,14 16,16 M19,4 C24,8 24,16 19,20",
            "\uE8FD" => "M3,5 L21,5 M3,11 L21,11 M3,17 L13,17 M18,15 L23,18 L18,21 Z",
            "\uE8F2" => "M4,3 L20,3 L20,17 L12,17 L7,22 L7,17 L4,17 Z M8,8 L16,8 M8,12 L14,12",
            "\uE7F4" => "M3,3 L21,3 L21,15 L3,15 Z M12,15 L12,20 M7,21 L17,21",
            _ => null,
        };
        if (data is null)
            return new FontIcon { Glyph = glyph, FontSize = size, FontFamily = new FontFamily("Segoe Fluent Icons"), Foreground = Brush(color ?? Colors.White) };

        var filled = glyph is "\uE768" or "\uE769" or "\uE893" or "\uE892" or "\uEB52";
        var path = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), data),
            Width = 24, Height = 24, Stretch = Stretch.None,
            Fill = filled ? Brush(color ?? Colors.White) : null,
            Stroke = filled ? null : Brush(color ?? Colors.White),
            StrokeThickness = 1.7, StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
        };
        return new Viewbox { Width = size, Height = size, Child = path };
    }
    public static Button IconButton(string glyph, string label, Action action, double size = 18)
    {
        var button = new Button
        {
            Content = Icon(glyph, size), Background = Brush(Colors.Transparent),
            BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(8),
            MinWidth = 32, MinHeight = 32, Padding = new Thickness(7),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, label);
        button.Click += (_, _) => action();
        Tactile(button, .88f);
        return button;
    }
    public static Button Pill(string text, Action action, bool filled = false)
    {
        var b = new Button
        {
            Content = Text(text, 13, true), CornerRadius = new CornerRadius(18),
            Background = Brush(filled ? Accent : Color.FromArgb(20, 255, 255, 255)),
            BorderThickness = new Thickness(0), Padding = new Thickness(16, 7, 16, 7),
        };
        b.Click += (_, _) => action();
        Tactile(b);
        return b;
    }
    public static T WithId<T>(T element, string id) where T : DependencyObject
    {
        AutomationProperties.SetAutomationId(element, id);
        return element;
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
    public static void Tactile(FrameworkElement element, float pressScale = .96f)
    {
        element.SizeChanged += (_, _) => ElementCompositionPreview.GetElementVisual(element).CenterPoint = new Vector3((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);
        void Scale(float scale)
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            if (!AnimationsEnabled) { visual.Scale = Vector3.One; return; }
            var animation = visual.Compositor.CreateSpringVector3Animation();
            animation.FinalValue = new Vector3(scale, scale, 1);
            animation.DampingRatio = .78f;
            animation.Period = TimeSpan.FromMilliseconds(260);
            visual.StartAnimation("Scale", animation);
        }
        element.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => Scale(pressScale)), true);
        element.AddHandler(UIElement.PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => Scale(1)), true);
        element.KeyDown += (_, e) => { if (e.Key is global::Windows.System.VirtualKey.Space or global::Windows.System.VirtualKey.Enter) Scale(pressScale); };
        element.KeyUp += (_, e) => { if (e.Key is global::Windows.System.VirtualKey.Space or global::Windows.System.VirtualKey.Enter) Scale(1); };
        element.LostFocus += (_, _) => Scale(1);
        element.PointerCanceled += (_, _) => Scale(1);
        element.PointerCaptureLost += (_, _) => Scale(1);
        element.PointerExited += (_, _) => Scale(1);
    }
}
