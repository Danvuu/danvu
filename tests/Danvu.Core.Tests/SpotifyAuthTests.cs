using System.Net;
using System.Text;
using Danvu.Core;

namespace Danvu.Core.Tests;

public sealed class SpotifyAuthTests
{
    [Fact]
    public void PkceChallengeMatchesRfc7636Example()
    {
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            Pkce.CreateChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
        var first = Pkce.CreateVerifier();
        var second = Pkce.CreateVerifier();
        Assert.NotEqual(first, second);
        Assert.InRange(first.Length, 43, 128);
        Assert.Matches("^[A-Za-z0-9_-]+$", first);
    }

    [Fact]
    public async Task ValidStoredSessionDoesNotMakeNetworkRequests()
    {
        var clock = new ManualClock();
        var store = new MemoryTokenStore(new TokenSet("test-access", "test-refresh", clock.GetUtcNow().AddHours(1), "test-client"));
        using var handler = new RecordingHandler(_ => throw new InvalidOperationException("Unexpected network request"));
        using var http = new HttpClient(handler);
        var auth = new SpotifyAuthService(http, store, clock);

        Assert.True(await auth.RestoreAsync());
        Assert.Equal("test-access", await auth.GetAccessTokenAsync());
        Assert.True(auth.IsAuthenticated);
        Assert.Equal("test-client", auth.ClientId);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RefreshPersistsRotatedOrExistingRefreshToken(bool rotate)
    {
        var clock = new ManualClock();
        var store = new MemoryTokenStore(new TokenSet("expired", "previous-refresh", clock.GetUtcNow().AddSeconds(30), "client-123"));
        var response = rotate
            ? "{\"access_token\":\"renewed-access\",\"refresh_token\":\"rotated-refresh\",\"expires_in\":3600}"
            : "{\"access_token\":\"renewed-access\",\"expires_in\":3600}";
        using var handler = new RecordingHandler(_ => RecordingHandler.Json(response));
        using var http = new HttpClient(handler);
        var auth = new SpotifyAuthService(http, store, clock);

        var accessTokens = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => auth.GetAccessTokenAsync()));

        Assert.All(accessTokens, token => Assert.Equal("renewed-access", token));
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://accounts.spotify.com/api/token", request.Uri.ToString());
        var form = TestQuery.Parse(request.Body);
        Assert.Equal("refresh_token", form["grant_type"]);
        Assert.Equal("previous-refresh", form["refresh_token"]);
        Assert.Equal("client-123", form["client_id"]);
        Assert.False(form.ContainsKey("client_secret"));
        Assert.NotNull(store.Tokens);
        Assert.Equal(rotate ? "rotated-refresh" : "previous-refresh", store.Tokens.RefreshToken);
        Assert.Equal(clock.GetUtcNow().AddHours(1), store.Tokens.ExpiresAt);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task MissingOrRejectedSessionProducesActionableError()
    {
        var clock = new ManualClock();
        using var handler = new RecordingHandler(_ => RecordingHandler.Json("{}", HttpStatusCode.BadRequest));
        using var http = new HttpClient(handler);
        var emptyAuth = new SpotifyAuthService(http, new MemoryTokenStore(), clock);
        Assert.False(await emptyAuth.RestoreAsync());
        var missing = await Assert.ThrowsAsync<MusicServiceException>(() => emptyAuth.GetAccessTokenAsync());
        Assert.Equal(401, missing.StatusCode);

        var store = new MemoryTokenStore(new TokenSet("expired", "test-refresh", clock.GetUtcNow().AddHours(-1), "test-client"));
        var expiredAuth = new SpotifyAuthService(http, store, clock);
        var rejected = await Assert.ThrowsAsync<MusicServiceException>(() => expiredAuth.GetAccessTokenAsync());
        Assert.Equal(400, rejected.StatusCode);
        Assert.Contains("sign in again", rejected.Message);
        Assert.Equal("expired", store.Tokens?.AccessToken);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task SignOutClearsStoredAndInMemorySession()
    {
        using var handler = new RecordingHandler(_ => throw new InvalidOperationException("Unexpected request"));
        using var http = new HttpClient(handler);
        var store = new MemoryTokenStore(new TokenSet("test-access", "test-refresh", DateTimeOffset.UtcNow.AddHours(1), "test-client"));
        var auth = new SpotifyAuthService(http, store);
        await auth.RestoreAsync();

        await auth.SignOutAsync();

        Assert.False(auth.IsAuthenticated);
        Assert.Null(store.Tokens);
        Assert.Null(auth.ClientId);
        var error = await Assert.ThrowsAsync<MusicServiceException>(() => auth.GetAccessTokenAsync());
        Assert.Equal(401, error.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task CorruptPersistedSessionsAreRejectedBeforeNetworkUse(int corruptField)
    {
        var tokens = new TokenSet("access", "refresh", DateTimeOffset.UtcNow.AddHours(1), "client");
        tokens = corruptField switch
        {
            0 => tokens with { AccessToken = " " },
            1 => tokens with { RefreshToken = "" },
            2 => tokens with { ClientId = null! },
            _ => tokens with { ExpiresAt = default },
        };
        using var handler = new RecordingHandler(_ => throw new InvalidOperationException("Invalid session reached network"));
        using var http = new HttpClient(handler);
        var auth = new SpotifyAuthService(http, new MemoryTokenStore(tokens));

        Assert.False(await auth.RestoreAsync());
        Assert.False(auth.IsAuthenticated);
        var error = await Assert.ThrowsAsync<MusicServiceException>(() => auth.GetAccessTokenAsync());

        Assert.Equal(401, error.StatusCode);
        Assert.Empty(handler.Requests);
    }
}

[CollectionDefinition("Loopback OAuth", DisableParallelization = true)]
public sealed class LoopbackOAuthCollection;

[Collection("Loopback OAuth")]
public sealed class SpotifyLoopbackOAuthTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LoopbackCallbackChecksStateBeforeExchangingAuthorizationCode(bool validState)
    {
        using var handler = new RecordingHandler(_ => RecordingHandler.Json(
            "{\"access_token\":\"test-access\",\"refresh_token\":\"test-refresh\",\"expires_in\":3600}"));
        using var tokenHttp = new HttpClient(handler);
        using var callbackHttp = new HttpClient(new HttpClientHandler { UseProxy = false });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var store = new MemoryTokenStore();
        var auth = new SpotifyAuthService(tokenHttp, store);
        Uri? authorizationUri = null;
        Task<HttpResponseMessage>? callback = null;

        var signIn = auth.SignInAsync("test-client", uri =>
        {
            authorizationUri = uri;
            var query = TestQuery.Parse(uri.Query);
            callback = callbackHttp.GetAsync($"{SpotifyAuthService.RedirectUri}?code=test-code&state=" +
                (validState ? query["state"] : "wrong-state"), timeout.Token);
        }, timeout.Token);

        if (validState)
        {
            await signIn;
            Assert.NotNull(store.Tokens);
            var request = Assert.Single(handler.Requests);
            var form = TestQuery.Parse(request.Body);
            var query = TestQuery.Parse(authorizationUri!.Query);
            Assert.Equal("S256", query["code_challenge_method"]);
            Assert.Equal(Pkce.CreateChallenge(form["code_verifier"]), query["code_challenge"]);
            Assert.Equal("test-code", form["code"]);
            Assert.Equal(SpotifyAuthService.RedirectUri, form["redirect_uri"]);
            Assert.False(form.ContainsKey("client_secret"));
        }
        else
        {
            var error = await Assert.ThrowsAsync<MusicServiceException>(() => signIn);
            Assert.Contains("state", error.Message);
            Assert.Null(store.Tokens);
            Assert.Empty(handler.Requests);
        }

        Assert.NotNull(callback);
        using var response = await callback;
        Assert.Equal(validState ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
    }
}

internal sealed class MemoryTokenStore(TokenSet? tokens = null) : ITokenStore
{
    public TokenSet? Tokens { get; private set; } = tokens;
    public int SaveCount { get; private set; }
    public Task<TokenSet?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Tokens);
    public Task SaveAsync(TokenSet tokens, CancellationToken cancellationToken = default)
    {
        Tokens = tokens;
        SaveCount++;
        return Task.CompletedTask;
    }
    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        Tokens = null;
        return Task.CompletedTask;
    }
}

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Body, string? Authorization);

internal sealed class RecordingHandler(Func<RecordedRequest, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<RecordedRequest> Requests { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var captured = new RecordedRequest(request.Method, request.RequestUri!,
            request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken),
            request.Headers.Authorization?.ToString());
        Requests.Add(captured);
        return respond(captured);
    }
    public static HttpResponseMessage Json(string value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
}

internal static class TestQuery
{
    public static Dictionary<string, string> Parse(string query) => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(pair => pair.Split('=', 2))
        .ToDictionary(pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')),
            pair => pair.Length == 1 ? "" : Uri.UnescapeDataString(pair[1].Replace('+', ' ')));
}
