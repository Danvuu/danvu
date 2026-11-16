using Danvu.Windows.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.XamlTypeInfo;

namespace Danvu.Windows;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
                StartupDiagnostics.Record("Unhandled application error", exception);
        };
        StartupDiagnostics.Record("Starting Music");
        try
        {
            StartupDiagnostics.Record("Initializing COM wrappers");
            WinRT.ComWrappersSupport.InitializeComWrappers();
            StartupDiagnostics.Record("Starting XAML application");
            Application.Start(initialization =>
            {
                StartupDiagnostics.Record("XAML initialization callback entered");
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                StartupDiagnostics.Record("Constructing App");
                _ = new App();
                StartupDiagnostics.Record("App constructed");
            });
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Record("Startup failed", exception);
            throw;
        }
    }
}

public sealed class App : Application, IXamlMetadataProvider
{
    // App.xaml normally generates this provider. This app creates its views from
    // C#, so expose the WinUI metadata contract explicitly for framework XAML
    // templates and XamlControlsResources. Lazy initialization also supports
    // metadata queries made during native base-class construction.
    private XamlControlsXamlMetaDataProvider? _metadata;
    private XamlControlsXamlMetaDataProvider Metadata => _metadata ??= new();
    public IXamlType GetXamlType(Type type) => Metadata.GetXamlType(type);
    public IXamlType GetXamlType(string fullName) => Metadata.GetXamlType(fullName);
    public XmlnsDefinition[] GetXmlnsDefinitions() => Metadata.GetXmlnsDefinitions();

    private MainWindow? _window;
    public App()
    {
        StartupDiagnostics.Record("App constructor entered");
        UnhandledException += (_, args) =>
        {
            // Keep unexpected native UI failures visible rather than silently swallowing them.
            StartupDiagnostics.Record("Unhandled WinUI error", args.Exception);
        };
        StartupDiagnostics.Record("Selecting dark theme");
        RequestedTheme = ApplicationTheme.Dark;
        StartupDiagnostics.Record("Loading WinUI control resources");
        Resources.MergedDictionaries.Add(new Microsoft.UI.Xaml.Controls.XamlControlsResources());
        StartupDiagnostics.Record("WinUI control resources loaded");
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupDiagnostics.Record("Creating native window");
        _window = new MainWindow();
        _window.Activate();
        StartupDiagnostics.Record("Native window activated");
    }
}
