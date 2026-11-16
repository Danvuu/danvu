namespace Danvu.Windows.Services;

/// <summary>Bounded local startup diagnostics; never writes exception payloads or authentication data.</summary>
internal static class StartupDiagnostics
{
    private const long MaximumBytes = 64 * 1024;
    private static readonly object Gate = new();

    public static void Record(string phase, Exception? exception = null)
    {
        try
        {
            var directory = Path.Combine(LocalState.DirectoryPath, "Logs");
            lock (Gate)
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "startup.log");
                if (File.Exists(path) && new FileInfo(path).Length >= MaximumBytes)
                    File.Move(path, Path.Combine(directory, "startup.previous.log"), true);
                var detail = exception is null ? "" :
                    $"{exception.GetType().FullName} HRESULT=0x{exception.HResult:X8}{Environment.NewLine}{exception.StackTrace}";
                if (detail.Length > 16 * 1024) detail = detail[..(16 * 1024)];
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {phase}{Environment.NewLine}{detail}{Environment.NewLine}");
            }
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // A diagnostic write must not replace or hide the original startup failure.
        }
    }
}
