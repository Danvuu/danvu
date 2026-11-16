using System.Net;
using System.Text.Json;
using Danvu.Core;

namespace Danvu.Core.Tests;

public sealed class SpotifyMusicServiceTests
{
    [Fact]
    public async Task SearchEncodesQueryAndMapsMixedSpotifyResults()
    {
        using var handler = new RecordingHandler(request => IsSavedCheck(request) ? UnsavedTracks(request) : RecordingHandler.Json(
            $$$"""{"tracks":{"items":[null,{{{TrackJson("track-1")}}}]},"albums":{"items":[{{{AlbumJson}}}]},"artists":{"items":[{{{ArtistJson}}}]},"playlists":{"items":[null,{{{PlaylistJson}}}]}}"""));
        using var http = new HttpClient(handler);
        var service = CreateService(http);

        var results = await service.SearchAsync("  blue & gold / live  ");

        var request = Assert.Single(handler.Requests, item => item.Uri.AbsolutePath == "/v1/search");
        Assert.Equal("blue & gold / live", TestQuery.Parse(request.Uri.Query)["q"]);
        Assert.Equal("Bearer test-access", request.Authorization);
        Assert.Equal("Blue & Gold", Assert.Single(results.Tracks).Title);
        Assert.Equal("Original artist", Assert.Single(results.Artists).Name);
        Assert.Equal("Album A", Assert.Single(results.Albums).Title);
        Assert.Equal("Evening", Assert.Single(results.Playlists).Name);
    }

    [Fact]
    public async Task AlbumTracksFollowPaginationAndInheritAlbumArtwork()
    {
        using var handler = new RecordingHandler(request => IsSavedCheck(request) ? UnsavedTracks(request) : RecordingHandler.Json(request.Uri.PathAndQuery switch
        {
            "/v1/albums/album-1" => AlbumJson,
            "/v1/albums/album-1/tracks?limit=50" => $$$"""{"items":[{{{TrackJson("one", false)}}}],"next":"https://api.spotify.com/v1/albums/album-1/tracks?offset=1"}""",
            "/v1/albums/album-1/tracks?offset=1" => $$$"""{"items":[{{{TrackJson("two", false)}}}],"next":null}""",
            _ => throw new InvalidOperationException("Unexpected request")
        }));
        using var http = new HttpClient(handler);

        var tracks = await CreateService(http).GetAlbumTracksAsync("album-1");

        Assert.Equal(new[] { "one", "two" }, tracks.Select(track => track.Id));
        Assert.All(tracks, track =>
        {
            Assert.Equal("Album A", track.Album);
            Assert.Equal("album-1", track.AlbumId);
            Assert.Equal("https://i.scdn.co/image/art", track.ArtworkUrl);
        });
    }

    [Fact]
    public async Task LibraryCombinesSavedTracksAlbumsFollowedArtistsAndPlaylists()
    {
        using var handler = new RecordingHandler(request => RecordingHandler.Json(request.Uri.AbsolutePath switch
        {
            "/v1/me/tracks" => $$$"""{"items":[{"track":{{{TrackJson("saved-track")}}}},{"track":null}],"next":null}""",
            "/v1/me/albums" => $$$"""{"items":[{"album":{{{AlbumJson}}}}],"next":null}""",
            "/v1/me/following" => $$$"""{"artists":{"items":[{{{ArtistJson}}}],"next":null}}""",
            "/v1/me/playlists" => $$$"""{"items":[{{{PlaylistJson}}}],"next":null}""",
            _ => throw new InvalidOperationException("Unexpected request")
        }));
        using var http = new HttpClient(handler);

        var library = await CreateService(http).GetLibraryAsync();

        Assert.True(Assert.Single(library.Tracks).IsLiked);
        Assert.Single(library.Albums);
        Assert.Single(library.Artists);
        Assert.Single(library.Playlists);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task HomeLoadsBoundedLibraryPreviewWithoutFollowingPagination()
    {
        const string next = "https://api.spotify.com/v1/unexpected-next-page";
        using var handler = new RecordingHandler(request => RecordingHandler.Json(request.Uri.PathAndQuery switch
        {
            "/v1/me/albums?limit=20" => $$$"""{"items":[{"album":{{{AlbumJson}}}}],"next":"{{{next}}}"}""",
            "/v1/me/playlists?limit=20" => $$$"""{"items":[{{{PlaylistJson}}}],"next":"{{{next}}}"}""",
            "/v1/me/following?type=artist&limit=20" => $$$"""{"artists":{"items":[{{{ArtistJson}}}],"next":"{{{next}}}"}}""",
            _ => throw new InvalidOperationException("Home fetched beyond its bounded preview")
        }));
        using var http = new HttpClient(handler);

        var home = await CreateService(http).GetHomeAsync();

        Assert.Equal("album-1", home.FeaturedAlbum?.Id);
        Assert.Single(home.Albums);
        Assert.Single(home.Artists);
        Assert.Single(home.Playlists);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task RestrictedTopTracksDoNotPreventArtistProfileAndDiscography()
    {
        using var handler = new RecordingHandler(request => request.Uri.AbsolutePath switch
        {
            "/v1/artists/artist-1" => RecordingHandler.Json(ArtistJson),
            "/v1/artists/artist-1/albums" => RecordingHandler.Json($$$"""{"items":[{{{AlbumJson}}},{{{AlbumJson}}}],"next":null}"""),
            "/v1/artists/artist-1/top-tracks" => RecordingHandler.Json("{}", HttpStatusCode.Forbidden),
            _ => throw new InvalidOperationException("Unexpected request")
        });
        using var http = new HttpClient(handler);

        var artist = await CreateService(http).GetArtistAsync("artist-1");

        Assert.Equal("Original artist", artist.Artist.Name);
        Assert.Empty(artist.TopTracks);
        Assert.Single(artist.Albums);
    }

    [Fact]
    public async Task NoActiveDeviceReturnsExplicitEmptyPlaybackState()
    {
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var http = new HttpClient(handler);

        var playback = await CreateService(http).GetPlaybackAsync();

        Assert.Null(playback.Track);
        Assert.False(playback.IsPlaying);
        Assert.Contains("No active", playback.DeviceName);
    }

    [Fact]
    public async Task NullableDeviceVolumeUsesFallbackInsteadOfCrashingPlayback()
    {
        using var handler = new RecordingHandler(request => IsSavedCheck(request) ? UnsavedTracks(request) : RecordingHandler.Json(
            $$$"""{"item":{{{TrackJson("playing")}}},"progress_ms":4000,"is_playing":true,"shuffle_state":false,"repeat_state":"track","device":{"name":"Living room","volume_percent":null}}"""));
        using var http = new HttpClient(handler);

        var playback = await CreateService(http).GetPlaybackAsync();

        Assert.True(playback.IsPlaying);
        Assert.Equal(4000, playback.PositionMs);
        Assert.Equal(RepeatMode.One, playback.Repeat);
        Assert.InRange(playback.Volume, 0, 100);
        Assert.Equal("Living room", playback.DeviceName);
    }

    [Fact]
    public async Task LargePlaylistPlaybackIncludesSelectedTrackWithinSpotifyUriLimit()
    {
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var http = new HttpClient(handler);
        var tracks = Enumerable.Range(0, 230).Select(index => DemoCatalog.Tracks[0] with
        {
            Id = $"track-{index}", Uri = $"spotify:track:track-{index}"
        }).ToArray();

        await CreateService(http).PlayAsync(tracks, 170);

        var request = Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(request.Body);
        var uris = body.RootElement.GetProperty("uris").EnumerateArray().Select(value => value.GetString()).ToArray();
        var selected = body.RootElement.GetProperty("offset").GetProperty("position").GetInt32();
        Assert.InRange(uris.Length, 1, 100);
        Assert.Equal(tracks[170].Uri, uris[selected]);
        Assert.Equal(HttpMethod.Put, request.Method);
    }

    [Fact]
    public async Task AddingPlaylistTracksSplitsAtSpotifyBatchLimit()
    {
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        using var http = new HttpClient(handler);
        var tracks = Enumerable.Range(0, 205).Select(index => DemoCatalog.Tracks[0] with { Uri = $"spotify:track:{index}" }).ToArray();

        await CreateService(http).AddToPlaylistAsync("playlist-1", tracks);

        var sentUris = new List<string?>();
        foreach (var request in handler.Requests)
        {
            using var body = JsonDocument.Parse(request.Body);
            var batch = body.RootElement.GetProperty("uris").EnumerateArray().Select(value => value.GetString()).ToArray();
            Assert.InRange(batch.Length, 1, 100);
            sentUris.AddRange(batch);
        }
        Assert.Equal(tracks.Select(track => track.Uri), sentUris);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task PlaybackReadsSavedStatusAndCanLikeAndUnlikeCurrentSong()
    {
        var saved = true;
        using var handler = new RecordingHandler(request =>
        {
            if (IsSavedCheck(request)) return RecordingHandler.Json(saved ? "[true]" : "[false]");
            if (request.Uri.AbsolutePath == "/v1/me/player")
                return RecordingHandler.Json($$$"""{"item":{{{TrackJson("playing")}}},"is_playing":true,"progress_ms":1000}""");
            if (request.Uri.AbsolutePath == "/v1/me/tracks")
            {
                saved = request.Method == HttpMethod.Put;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            throw new InvalidOperationException("Unexpected request");
        });
        using var http = new HttpClient(handler);
        var service = CreateService(http);

        var initial = await service.GetPlaybackAsync();
        await service.SetLikedAsync("playing", false);
        var removed = await service.GetPlaybackAsync();
        await service.SetLikedAsync("playing", true);
        var added = await service.GetPlaybackAsync();

        Assert.True(initial.Track?.IsLiked);
        Assert.False(removed.Track?.IsLiked);
        Assert.True(added.Track?.IsLiked);
        Assert.Contains(handler.Requests, request => request.Uri.AbsolutePath == "/v1/me/tracks" && request.Method == HttpMethod.Delete);
        Assert.Contains(handler.Requests, request => request.Uri.AbsolutePath == "/v1/me/tracks" && request.Method == HttpMethod.Put);
        Assert.Single(handler.Requests, IsSavedCheck);
    }

    [Fact]
    public async Task PlaylistSavedStateUsesSpotifyBatchLimitsAndPreservesTrackOrder()
    {
        var entries = string.Join(',', Enumerable.Range(0, 70).Select(index => "{\"track\":" + TrackJson($"track-{index}") + "}"));
        using var handler = new RecordingHandler(request =>
        {
            if (IsSavedCheck(request))
            {
                var ids = TestQuery.Parse(request.Uri.Query)["ids"].Split(',');
                Assert.InRange(ids.Length, 1, 50);
                return RecordingHandler.Json(JsonSerializer.Serialize(ids.Select(id => int.Parse(id[6..]) % 2 == 0)));
            }
            return RecordingHandler.Json("{\"items\":[" + entries + "],\"next\":null}");
        });
        using var http = new HttpClient(handler);

        var tracks = await CreateService(http).GetPlaylistTracksAsync("playlist-1");

        Assert.Equal(70, tracks.Count);
        for (var index = 0; index < tracks.Count; index++)
        {
            Assert.Equal($"track-{index}", tracks[index].Id);
            Assert.Equal(index % 2 == 0, tracks[index].IsLiked);
        }
        Assert.Equal(2, handler.Requests.Count(IsSavedCheck));
    }

    [Fact]
    public async Task FailedLikeMutationDoesNotChangeDisplayedSavedState()
    {
        using var handler = new RecordingHandler(request =>
        {
            if (IsSavedCheck(request)) return RecordingHandler.Json("[false]");
            if (request.Uri.AbsolutePath == "/v1/me/player")
                return RecordingHandler.Json($$$"""{"item":{{{TrackJson("playing")}}},"is_playing":true}""");
            return RecordingHandler.Json("{}", HttpStatusCode.Forbidden);
        });
        using var http = new HttpClient(handler);
        var service = CreateService(http);
        Assert.False((await service.GetPlaybackAsync()).Track?.IsLiked);

        await Assert.ThrowsAsync<MusicServiceException>(() => service.SetLikedAsync("playing", true));

        Assert.False((await service.GetPlaybackAsync()).Track?.IsLiked);
    }

    [Theory]
    [InlineData(401, "session")]
    [InlineData(403, "Premium")]
    [InlineData(404, "device")]
    [InlineData(429, "12")]
    public async Task ApiFailuresProvideActionableErrorsWithoutLeakingResponses(int status, string expectedMessage)
    {
        using var handler = new RecordingHandler(_ =>
        {
            var response = RecordingHandler.Json("{\"error\":\"sensitive-response-body\"}", (HttpStatusCode)status);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(12));
            return response;
        });
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<MusicServiceException>(() => CreateService(http).PauseAsync());

        Assert.Equal(status, error.StatusCode);
        Assert.Contains(expectedMessage, error.Message);
        Assert.DoesNotContain("sensitive-response-body", error.Message);
    }

    [Theory]
    [InlineData("https://unexpected.example/v1/me/playlists")]
    [InlineData("http://api.spotify.com/v1/me/playlists")]
    [InlineData("https://api.spotify.com/private/path")]
    public async Task PaginationCannotForwardBearerTokenToUntrustedDestination(string nextPage)
    {
        using var handler = new RecordingHandler(_ => RecordingHandler.Json(
            JsonSerializer.Serialize(new { items = System.Array.Empty<object>(), next = nextPage })));
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<MusicServiceException>(() => CreateService(http).GetPlaylistTracksAsync("playlist-1"));

        Assert.Contains("destination", error.Message);
        Assert.Single(handler.Requests);
        Assert.All(handler.Requests, request => Assert.Equal("api.spotify.com", request.Uri.Host));
    }

    [Fact]
    public async Task RepeatedPaginationIsRejectedInsteadOfLoopingForever()
    {
        using var handler = new RecordingHandler(_ => RecordingHandler.Json(
            "{\"items\":[],\"next\":\"https://api.spotify.com/v1/playlists/playlist-1/tracks?limit=50\"}"));
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<MusicServiceException>(() => CreateService(http).GetPlaylistTracksAsync("playlist-1"));

        Assert.Contains("repeated", error.Message);
        Assert.InRange(handler.Requests.Count, 1, 2);
    }

    private static bool IsSavedCheck(RecordedRequest request) => request.Uri.AbsolutePath == "/v1/me/tracks/contains";

    private static HttpResponseMessage UnsavedTracks(RecordedRequest request) => RecordingHandler.Json(
        JsonSerializer.Serialize(TestQuery.Parse(request.Uri.Query)["ids"].Split(',').Select(_ => false)));

    private static SpotifyMusicService CreateService(HttpClient http)
    {
        var store = new MemoryTokenStore(new TokenSet("test-access", "test-refresh", DateTimeOffset.UtcNow.AddHours(1), "test-client"));
        return new SpotifyMusicService(http, new SpotifyAuthService(http, store));
    }

    private const string ArtistJson = """{"id":"artist-1","name":"Original artist","images":[],"followers":{"total":123}}""";
    private const string AlbumJson = """{"id":"album-1","name":"Album A","release_date":"2026-01-01","uri":"spotify:album:album-1","images":[{"url":"https://i.scdn.co/image/art"}],"artists":[{"id":"artist-1","name":"Original artist"}]}""";
    private const string PlaylistJson = """{"id":"playlist-1","name":"Evening","description":"A personal playlist","uri":"spotify:playlist:playlist-1","images":[],"tracks":{"total":2}}""";

    private static string TrackJson(string id, bool includeAlbum = true) =>
        $$$"""{"id":"{{{id}}}","name":"Blue & Gold","type":"track","duration_ms":180000,"uri":"spotify:track:{{{id}}}","artists":[{"id":"artist-1","name":"Original artist"}]{{{(includeAlbum ? ",\"album\":" + AlbumJson : "")}}}}""";
}
