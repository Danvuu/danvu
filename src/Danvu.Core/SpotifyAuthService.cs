using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Danvu.Core;

public sealed record TokenSet(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, string ClientId);

/// <summary>The Windows host stores tokens with Windows DPAPI. Never save tokens to plain JSON.</summary>
public interface ITokenStore
{
    Task<TokenSet?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(TokenSet tokens, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

public static class Pkce
{
    public static string CreateVerifier() => Base64Url(RandomNumberGenerator.GetBytes(64));
    public static string CreateChallenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Spotify Authorization Code flow with PKCE, system browser, loopback callback and refresh rotation.</summary>
public sealed class SpotifyAuthService(HttpClient http, ITokenStore tokenStore, TimeProvider? clock = null)
{
    public const string RedirectUri = "http://127.0.0.1:5543/callback";
    public const string Scopes = "user-read-private user-library-read user-library-modify playlist-read-private " +
        "playlist-read-collaborative playlist-modify-public playlist-modify-private user-follow-read user-follow-modify " +
        "user-read-playback-state user-modify-playback-state user-read-currently-playing user-read-recently-played user-top-read";
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private TokenSet? _tokens;
    public bool IsAuthenticated => _tokens is not null;
    public string? ClientId => _tokens?.ClientId;

    public async Task<bool> RestoreAsync(CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            var stored = await tokenStore.LoadAsync(cancellationToken);
            _tokens = IsValidSession(stored) ? stored : null;
            return _tokens is not null;
        }
        finally { _refreshLock.Release(); }
    }

    public async Task SignInAsync(string clientId, Action<Uri> openBrowser, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientId)) throw new ArgumentException("Enter your Spotify developer Client ID.", nameof(clientId));
        ArgumentNullException.ThrowIfNull(openBrowser);
        var verifier = Pkce.CreateVerifier();
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = clientId.Trim(), ["response_type"] = "code", ["redirect_uri"] = RedirectUri,
            ["scope"] = Scopes, ["code_challenge_method"] = "S256", ["code_challenge"] = Pkce.CreateChallenge(verifier),
            ["state"] = state
        };
        var authorizationUri = new Uri("https://accounts.spotify.com/authorize?" + string.Join("&",
            parameters.Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}")));
        // TCP loopback avoids HTTP.sys URL reservations and does not require administrator privileges.
        var listener = new TcpListener(IPAddress.Loopback, 5543);
        listener.ExclusiveAddressUse = true;
        try { listener.Start(4); }
        catch (SocketException)
        { listener.Stop(); throw new MusicServiceException("Could not open Spotify sign-in callback on port 5543. Close another Music instance or free that port."); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            openBrowser(authorizationUri);
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                client.NoDelay = true;
                await using var stream = client.GetStream();
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                Uri? callback;
                try { callback = await ReadCallbackUriAsync(stream, readTimeout.Token); }
                catch (OperationCanceledException) when (!timeout.IsCancellationRequested) { continue; }
                catch (IOException) { continue; }
                if (callback?.AbsolutePath != "/callback")
                { await WriteEmptyResponseAsync(stream, timeout.Token); continue; }
                var query = ParseCallbackQuery(callback.Query);
                var returnedState = query.GetValueOrDefault("state");
                var validState = returnedState is not null && CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(returnedState));
                var code = query.GetValueOrDefault("code");
                var denied = query.ContainsKey("error");
                if (!validState)
                {
                    await TryWriteBrowserResponseAsync(stream, false, timeout.Token);
                    throw new MusicServiceException("Spotify sign-in state did not match. Please try signing in again.");
                }
                if (denied || string.IsNullOrWhiteSpace(code))
                {
                    await TryWriteBrowserResponseAsync(stream, false, timeout.Token);
                    throw new MusicServiceException("Spotify sign-in was cancelled or permission was not granted.");
                }
                try
                {
                    await _refreshLock.WaitAsync(timeout.Token);
                    try
                    {
                        var tokens = await RequestTokensAsync(new Dictionary<string, string>
                        {
                            ["grant_type"] = "authorization_code", ["client_id"] = clientId.Trim(),
                            ["code"] = code, ["redirect_uri"] = RedirectUri, ["code_verifier"] = verifier
                        }, clientId.Trim(), null, timeout.Token);
                        await tokenStore.SaveAsync(tokens, timeout.Token);
                        _tokens = tokens;
                    }
                    finally { _refreshLock.Release(); }
                    // The session is committed. Closing the browser tab must not invalidate a successful sign-in.
                    await TryWriteBrowserResponseAsync(stream, true, timeout.Token);
                }
                catch
                {
                    await TryWriteBrowserResponseAsync(stream, false, timeout.Token);
                    throw;
                }
                return;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new MusicServiceException("Spotify sign-in timed out. Try again and complete sign-in in your browser."); }
        finally { listener.Stop(); }
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (_tokens is null)
            {
                var stored = await tokenStore.LoadAsync(cancellationToken);
                _tokens = IsValidSession(stored) ? stored : null;
            }
            if (_tokens is null) throw new MusicServiceException("Connect your Spotify account in Settings.", 401);
            if (_tokens.ExpiresAt > _clock.GetUtcNow().AddSeconds(60)) return _tokens.AccessToken;
            if (string.IsNullOrEmpty(_tokens.RefreshToken)) throw new MusicServiceException("Sign in to Spotify again to renew access.", 401);
            var refreshed = await RequestTokensAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token", ["refresh_token"] = _tokens.RefreshToken,
                ["client_id"] = _tokens.ClientId
            }, _tokens.ClientId, _tokens.RefreshToken, cancellationToken);
            await tokenStore.SaveAsync(refreshed, cancellationToken);
            _tokens = refreshed;
            return refreshed.AccessToken;
        }
        finally { _refreshLock.Release(); }
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try { await tokenStore.ClearAsync(cancellationToken); _tokens = null; }
        finally { _refreshLock.Release(); }
    }

    private static bool IsValidSession(TokenSet? tokens) => tokens is not null &&
        !string.IsNullOrWhiteSpace(tokens.AccessToken) && !string.IsNullOrWhiteSpace(tokens.RefreshToken) &&
        !string.IsNullOrWhiteSpace(tokens.ClientId) && tokens.ExpiresAt > DateTimeOffset.MinValue;

    private async Task<TokenSet> RequestTokensAsync(Dictionary<string, string> form, string clientId,
        string? existingRefreshToken, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await http.PostAsync("https://accounts.spotify.com/api/token", content, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new MusicServiceException(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized
                ? "Spotify could not authorize this session. Check the Client ID and registered redirect URI, then sign in again."
                : $"Spotify authentication is unavailable (HTTP {(int)response.StatusCode}). Try again later.", (int)response.StatusCode);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = json.RootElement;
        var accessToken = root.GetProperty("access_token").GetString();
        var refreshToken = root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : existingRefreshToken;
        var expiresIn = root.TryGetProperty("expires_in", out var expiry) ? expiry.GetInt32() : 3600;
        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(refreshToken))
            throw new MusicServiceException("Spotify returned an incomplete session. Please sign in again.");
        return new TokenSet(accessToken, refreshToken, _clock.GetUtcNow().AddSeconds(expiresIn), clientId);
    }

    private static async Task<Uri?> ReadCallbackUriAsync(Stream stream, CancellationToken cancellationToken)
    {
        // This listener is a single-purpose OAuth callback, not a general web server.
        var buffer = new byte[8192];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken);
            if (read == 0) return null;
            count += read;
            var header = Encoding.ASCII.GetString(buffer, 0, count);
            if (!header.Contains("\r\n\r\n", StringComparison.Ordinal)) continue;
            var lineEnd = header.IndexOf("\r\n", StringComparison.Ordinal);
            if (lineEnd < 0) return null;
            var request = header[..lineEnd].Split(' ');
            if (request.Length != 3 || request[0] != "GET" || request[2] is not ("HTTP/1.1" or "HTTP/1.0") ||
                !request[1].StartsWith('/') || request[1].StartsWith("//", StringComparison.Ordinal)) return null;
            if (!Uri.TryCreate(new Uri(RedirectUri), request[1], out var callback) ||
                callback.Host != "127.0.0.1" || callback.Port != 5543 || callback.Fragment.Length > 0) return null;
            return callback;
        }
        return null;
    }

    private static Dictionary<string, string> ParseCallbackQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = item.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
            var value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : "";
            // Reject ambiguous duplicate parameters rather than selecting a potentially attacker-controlled value.
            if (!result.TryAdd(key, value)) return [];
        }
        return result;
    }

    private static async Task WriteEmptyResponseAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        try { await stream.WriteAsync(bytes, cancellationToken); }
        catch (IOException) { }
    }

    private static async Task TryWriteBrowserResponseAsync(Stream stream, bool success, CancellationToken cancellationToken)
    {
        try { await WriteBrowserResponseAsync(stream, success, cancellationToken); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException) { }
    }

    private static async Task WriteBrowserResponseAsync(Stream stream, bool success, CancellationToken cancellationToken)
    {
        const string style = "body{background:#101014;color:#f4f1f3;font:18px system-ui;display:grid;place-content:center;min-height:90vh;text-align:center}h1{color:#fa5471}p{color:#aaa}";
        var message = success ? "Connected to Spotify" : "Spotify connection could not finish";
        var html = $"<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><title>Music</title><style>{style}</style><h1>{message}</h1><p>You can close this tab and return to Music.</p></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        var status = success ? "200 OK" : "400 Bad Request";
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nContent-Security-Policy: default-src 'none'; style-src 'unsafe-inline'\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
