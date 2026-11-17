using Danvu.Windows.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

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

public sealed partial class App : Application
{
    private MainWindow? _window;
    public App()
    {
        StartupDiagnostics.Record("App constructor entered");
        UnhandledException += (_, args) =>
        {
            // Keep unexpected native UI failures visible rather than silently swallowing them.
            StartupDiagnostics.Record("Unhandled WinUI error", args.Exception);
        };
        StartupDiagnostics.Record("Loading compiled application resources");
        InitializeComponent();
        StartupDiagnostics.Record("Compiled application resources loaded");
        StartupDiagnostics.Record($"Application resources: Count={Resources.Count}; MusicButtonStyle={Resources.ContainsKey("MusicButtonStyle")}; MusicSliderStyle={Resources.ContainsKey("MusicSliderStyle")}; App.xbf={File.Exists(Path.Combine(AppContext.BaseDirectory, "App.xbf"))}; PRI files={Directory.EnumerateFiles(AppContext.BaseDirectory, "*.pri").Take(64).Count()}");
        // Force deferred styles to resolve before constructing the native window.
        _ = (Style)Resources["MusicButtonStyle"];
        _ = (Style)Resources["MusicSliderStyle"];
        StartupDiagnostics.Record("Custom native button and slider styles resolved");
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupDiagnostics.Record("Creating native window");
        _window = new MainWindow();
        _window.Activate();
        StartupDiagnostics.Record("Native window activated");
    }
}
