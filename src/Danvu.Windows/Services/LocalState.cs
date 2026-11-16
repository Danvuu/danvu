using System.Security.Cryptography;
using System.Text.Json;
using Danvu.Core;

namespace Danvu.Windows.Services;

internal static class LocalState
{
    public static string DirectoryPath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Music");
    public static string FilePath(string name)
    {
        Directory.CreateDirectory(DirectoryPath);
        return Path.Combine(DirectoryPath, name);
    }
    public static string ClientId
    {
        get
        {
            try { return File.ReadAllText(FilePath("spotify-client-id.txt")).Trim(); }
            catch (FileNotFoundException) { return ""; }
            catch (DirectoryNotFoundException) { return ""; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new MusicServiceException("Music could not read its saved Spotify Client ID. Check access to your Windows user profile folder."); }
        }
        set
        {
            string? temporary = null;
            try
            {
                var path = FilePath("spotify-client-id.txt");
                temporary = path + $".{Guid.NewGuid():N}.tmp";
                File.WriteAllText(temporary, value.Trim());
                File.Move(temporary, path, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new MusicServiceException("Music could not save the Spotify Client ID. Check access to your Windows user profile folder."); }
            finally { TryDelete(temporary); }
        }
    }

    /// <summary>Only web artwork and files inside this installation's Assets directory may be rendered.</summary>
    public static Uri? ResolveArtworkUri(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 4096 || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme == Uri.UriSchemeHttps)
            return string.IsNullOrEmpty(uri.UserInfo) && !string.IsNullOrEmpty(uri.Host) ? uri : null;
        if (uri.Scheme != "ms-appx" || uri.Host.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0) return null;
        try
        {
            var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
            if (!relative.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                relative.Contains('\\') || relative.Contains(':') || relative.Contains('\0') ||
                relative.Contains('%') || relative.Contains('?') || relative.Contains('#')) return null;
            var assets = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Assets")) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(assets, StringComparison.OrdinalIgnoreCase)) return null;
            var resolved = new Uri(path, UriKind.Absolute);
            if (!Path.GetFullPath(resolved.LocalPath).StartsWith(assets, StringComparison.OrdinalIgnoreCase)) return null;
            return resolved;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        { return null; }
    }

    internal static void TryDelete(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

internal sealed class WindowsTokenStore : ITokenStore
{
    private const int MaxSessionBytes = 65_536;
    public async Task<TokenSet?> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var stream = new FileStream(LocalState.FilePath("spotify-session.dpapi"), FileMode.Open,
                FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            if (stream.Length is <= 0 or > MaxSessionBytes) return null;
            var bytes = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(bytes, cancellationToken);
            var plaintext = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            try { return JsonSerializer.Deserialize<TokenSet>(plaintext); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (CryptographicException) { return null; }
        catch (JsonException) { return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new MusicServiceException("Music could not read its saved Spotify session. Check your Windows profile permissions, then reconnect in Settings. Demo mode is still available."); }
    }
    public async Task SaveAsync(TokenSet tokens, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(tokens);
        string? temporary = null;
        try
        {
            var path = LocalState.FilePath("spotify-session.dpapi");
            var bytes = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
            temporary = path + $".{Guid.NewGuid():N}.tmp";
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        { throw new MusicServiceException("Music could not securely save the Spotify session. Check your Windows profile permissions and try connecting again."); }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            LocalState.TryDelete(temporary);
        }
    }
    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { File.Delete(LocalState.FilePath("spotify-session.dpapi")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new MusicServiceException("Music could not remove its saved Spotify session. Check access to your Windows user profile folder and try again."); }
        return Task.CompletedTask;
    }
}
