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
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(initialization =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                _ = new App();
            });
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Record("Startup failed", exception);
            throw;
        }
    }
}

public sealed class App : Application
{
    private MainWindow? _window;
    public App()
    {
        RequestedTheme = ApplicationTheme.Dark;
        Resources.MergedDictionaries.Add(new Microsoft.UI.Xaml.Controls.XamlControlsResources());
        UnhandledException += (_, args) =>
        {
            // Keep unexpected native UI failures visible rather than silently swallowing them.
            StartupDiagnostics.Record("Unhandled WinUI error", args.Exception);
        };
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupDiagnostics.Record("Creating native window");
        _window = new MainWindow();
        _window.Activate();
        StartupDiagnostics.Record("Native window activated");
    }
}
