using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Danvu.Windows;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(initialization =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
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
            System.Diagnostics.Trace.TraceError(args.Exception.ToString());
        };
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
