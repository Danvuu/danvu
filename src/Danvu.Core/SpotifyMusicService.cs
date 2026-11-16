using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Danvu.Core;

/// <summary>Uses the public Spotify Web API. Playback commands target an existing Spotify Connect device.</summary>
public sealed class SpotifyMusicService(HttpClient http, SpotifyAuthService auth, TimeProvider? clock = null) : IMusicService
{
    private static readonly Uri ApiBase = new("https://api.spotify.com/v1/");
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _savedLock = new(1, 1);
    private readonly Dictionary<string, (bool Liked, DateTimeOffset CheckedAt)> _savedStates = new(StringComparer.Ordinal);
    public bool IsDemo => false;
    public bool CanReorderQueue => false;

    public async Task<HomeContent> GetHomeAsync(CancellationToken cancellationToken = default)
    {
        // The official Spotify Home feed is not public. This home is assembled from the user's library.
        // Keep initial rendering bounded even for accounts with thousands of saved items.
        var albumsTask = GetAsync("me/albums?limit=20", cancellationToken);
        var playlistsTask = GetAsync("me/playlists?limit=20", cancellationToken);
        var artistsTask = GetAsync("me/following?type=artist&limit=20", cancellationToken);
        try { await Task.WhenAll(albumsTask, playlistsTask, artistsTask); }
        catch
        {
            if (albumsTask.IsCompletedSuccessfully) albumsTask.Result.Dispose();
            if (playlistsTask.IsCompletedSuccessfully) playlistsTask.Result.Dispose();
            if (artistsTask.IsCompletedSuccessfully) artistsTask.Result.Dispose();
            throw;
        }
        using var albumsJson = await albumsTask;
        using var playlistsJson = await playlistsTask;
        using var artistsJson = await artistsTask;
        var albums = Array(albumsJson.RootElement, "items").Select(x => x.TryGetProperty("album", out var album) ? ReadAlbum(album) : null)
            .OfType<Album>().ToArray();
        var playlists = Array(playlistsJson.RootElement, "items").Select(ReadPlaylist).OfType<Playlist>().ToArray();
        var artists = Items(artistsJson.RootElement, "artists").Select(ReadArtist).OfType<Artist>().ToArray();
        return new HomeContent(albums.FirstOrDefault(), albums, playlists, artists);
    }

    public async Task<SearchResults> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return new SearchResults([], [], [], []);
        using var json = await GetAsync($"search?q={Uri.EscapeDataString(query.Trim())}&type=track,album,artist,playlist&limit=10", cancellationToken);
        var root = json.RootElement;
        return new SearchResults(await AnnotateSavedAsync(Items(root, "tracks").Select(ReadTrack).OfType<Track>().ToArray(), cancellationToken),
            Items(root, "albums").Select(ReadAlbum).OfType<Album>().ToArray(),
            Items(root, "artists").Select(ReadArtist).OfType<Artist>().ToArray(),
            Items(root, "playlists").Select(ReadPlaylist).OfType<Playlist>().ToArray());
    }

    public async Task<IReadOnlyList<Track>> GetAlbumTracksAsync(string albumId, CancellationToken cancellationToken = default)
    {
        using var albumJson = await GetAsync($"albums/{Id(albumId)}", cancellationToken);
        var album = ReadAlbum(albumJson.RootElement) ?? throw new MusicServiceException("Album details are unavailable.");
        var items = await ReadPagesAsync($"albums/{Id(albumId)}/tracks?limit=50", cancellationToken);
        return await AnnotateSavedAsync(items.Select(x => ReadTrack(x, album)).OfType<Track>().ToArray(), cancellationToken);
    }

    public async Task<ArtistDetail> GetArtistAsync(string artistId, CancellationToken cancellationToken = default)
    {
        using var artistJson = await GetAsync($"artists/{Id(artistId)}", cancellationToken);
        var artist = ReadArtist(artistJson.RootElement) ?? throw new MusicServiceException("Artist details are unavailable.");
        var albums = await ReadPagesAsync($"artists/{Id(artistId)}/albums?include_groups=album,single&limit=50", cancellationToken);
        IReadOnlyList<Track> topTracks = [];
        try
        {
            using var topJson = await GetAsync($"artists/{Id(artistId)}/top-tracks", cancellationToken);
            topTracks = Array(topJson.RootElement, "tracks").Select(ReadTrack).OfType<Track>().ToArray();
        }
        catch (MusicServiceException ex) when (ex.StatusCode is 403 or 404)
        { /* Spotify restricts top tracks for some developer apps. Artist and discography remain usable. */ }
        return new ArtistDetail(artist, await AnnotateSavedAsync(topTracks, cancellationToken), albums.Select(ReadAlbum).OfType<Album>().DistinctBy(x => x.Id).ToArray());
    }

    public async Task<IReadOnlyList<Track>> GetPlaylistTracksAsync(string playlistId, CancellationToken cancellationToken = default)
    {
        var items = await ReadPagesAsync($"playlists/{Id(playlistId)}/tracks?limit=50", cancellationToken);
        return await AnnotateSavedAsync(items.Select(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("track", out var track) ? ReadTrack(track) : null).OfType<Track>().ToArray(), cancellationToken);
    }

    public async Task<LibraryContent> GetLibraryAsync(CancellationToken cancellationToken = default)
    {
        var tracksTask = ReadPagesAsync("me/tracks?limit=50", cancellationToken);
        var albumsTask = ReadPagesAsync("me/albums?limit=50", cancellationToken);
        var artistsTask = ReadPagesAsync("me/following?type=artist&limit=50", cancellationToken, "artists");
        var playlistsTask = ReadPagesAsync("me/playlists?limit=50", cancellationToken);
        await Task.WhenAll(tracksTask, albumsTask, artistsTask, playlistsTask);
        var savedTracks = (await tracksTask).Select(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("track", out var track) ? ReadTrack(track) : null)
            .OfType<Track>().Select(x => x with { IsLiked = true }).ToArray();
        await _savedLock.WaitAsync(cancellationToken);
        try { foreach (var track in savedTracks) _savedStates[track.Id] = (true, _clock.GetUtcNow()); }
        finally { _savedLock.Release(); }
        return new LibraryContent(savedTracks,
            (await albumsTask).Select(x => x.TryGetProperty("album", out var album) ? ReadAlbum(album) : null).OfType<Album>().ToArray(),
            (await artistsTask).Select(ReadArtist).OfType<Artist>().ToArray(),
            (await playlistsTask).Select(ReadPlaylist).OfType<Playlist>().ToArray());
    }

    public async Task<UserProfile> GetProfileAsync(CancellationToken cancellationToken = default)
    {
        using var json = await GetAsync("me", cancellationToken);
        var root = json.RootElement;
        return new UserProfile(Text(root, "id"), Text(root, "display_name", "Spotify listener"), Image(root), Text(root, "product"));
    }

    public async Task<PlaybackSnapshot> GetPlaybackAsync(CancellationToken cancellationToken = default)
    {
        using var request = await RequestAsync(HttpMethod.Get, "me/player", null, cancellationToken);
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent)
            return new PlaybackSnapshot(null, 0, false, false, RepeatMode.Off, 50, "No active Spotify device");
        await EnsureSuccessAsync(response, cancellationToken);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = json.RootElement;
        var device = Object(root, "device");
        var currentTrack = root.TryGetProperty("item", out var track) ? ReadTrack(track) : null;
        if (currentTrack is not null) currentTrack = (await AnnotateSavedAsync([currentTrack], cancellationToken))[0];
        return new PlaybackSnapshot(currentTrack,
            Number(root, "progress_ms"), Boolean(root, "is_playing"), Boolean(root, "shuffle_state"),
            Text(root, "repeat_state") switch { "track" => RepeatMode.One, "context" => RepeatMode.All, _ => RepeatMode.Off },
            Number(device, "volume_percent", 50), Text(device, "name", "Spotify device"));
    }

    public async Task<IReadOnlyList<PlaybackDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        using var json = await GetAsync("me/player/devices", cancellationToken);
        return Array(json.RootElement, "devices").Where(x => !string.IsNullOrEmpty(Text(x, "id")))
            .Select(x => new PlaybackDevice(Text(x, "id"), Text(x, "name"), Boolean(x, "is_active"),
                Boolean(x, "is_restricted"), Text(x, "type"), Number(x, "volume_percent", 50))).ToArray();
    }

    public async Task<IReadOnlyList<Track>> GetQueueAsync(CancellationToken cancellationToken = default)
    {
        using var json = await GetAsync("me/player/queue", cancellationToken);
        return await AnnotateSavedAsync(Array(json.RootElement, "queue").Select(ReadTrack).OfType<Track>().ToArray(), cancellationToken);
    }

    public Task PlayAsync(IReadOnlyList<Track> tracks, int startIndex = 0, CancellationToken cancellationToken = default)
    {
        if (tracks.Count == 0) return Task.CompletedTask;
        if (startIndex < 0 || startIndex >= tracks.Count) throw new ArgumentOutOfRangeException(nameof(startIndex));
        // Explicit-URI playback accepts up to 100 tracks; preserve selected index when playing a larger collection.
        var windowStart = Math.Max(0, startIndex - 25);
        var window = tracks.Skip(windowStart).Take(100).ToArray();
        return SendAsync(HttpMethod.Put, "me/player/play", new
        {
            uris = window.Select(x => x.Uri).ToArray(), offset = new { position = startIndex - windowStart }, position_ms = 0
        }, cancellationToken);
    }
    public Task PauseAsync(CancellationToken cancellationToken = default) => SendAsync(HttpMethod.Put, "me/player/pause", null, cancellationToken);
    public Task ResumeAsync(CancellationToken cancellationToken = default) => SendAsync(HttpMethod.Put, "me/player/play", null, cancellationToken);
    public Task SeekAsync(int positionMs, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"me/player/seek?position_ms={Math.Max(0, positionMs)}", null, cancellationToken);
    public Task SkipAsync(bool next, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, next ? "me/player/next" : "me/player/previous", null, cancellationToken);
    public Task SetVolumeAsync(int volume, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"me/player/volume?volume_percent={Math.Clamp(volume, 0, 100)}", null, cancellationToken);
    public Task SetShuffleAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"me/player/shuffle?state={enabled.ToString().ToLowerInvariant()}", null, cancellationToken);
    public Task SetRepeatAsync(RepeatMode mode, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, "me/player/repeat?state=" + (mode switch { RepeatMode.All => "context", RepeatMode.One => "track", _ => "off" }), null, cancellationToken);
    public async Task SetLikedAsync(string trackId, bool liked, CancellationToken cancellationToken = default)
    {
        // Serialize read-through caching and mutations so an older contains response cannot undo a successful toggle.
        await _savedLock.WaitAsync(cancellationToken);
        try
        {
            await SendAsync(liked ? HttpMethod.Put : HttpMethod.Delete, $"me/tracks?ids={Id(trackId)}", null, cancellationToken);
            _savedStates[trackId] = (liked, _clock.GetUtcNow());
        }
        finally { _savedLock.Release(); }
    }
    public Task EnqueueAsync(Track track, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "me/player/queue?uri=" + Uri.EscapeDataString(track.Uri), null, cancellationToken);
    public Task MoveQueueItemAsync(int fromIndex, int toIndex, CancellationToken cancellationToken = default) =>
        throw new MusicServiceException("Spotify's public API cannot reorder the playback queue. Queue reordering is available in demo mode only.");
    public Task TransferAsync(string deviceId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, "me/player", new { device_ids = new[] { deviceId }, play = true }, cancellationToken);

    public async Task<Playlist> CreatePlaylistAsync(string name, string description, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Give the playlist a name.", nameof(name));
        var profile = await GetProfileAsync(cancellationToken);
        using var request = await RequestAsync(HttpMethod.Post, $"users/{Id(profile.Id)}/playlists",
            new { name = name.Trim(), description, @public = false }, cancellationToken);
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return ReadPlaylist(json.RootElement) ?? throw new MusicServiceException("Spotify did not return the created playlist.");
    }

    public async Task AddToPlaylistAsync(string playlistId, IReadOnlyList<Track> tracks, CancellationToken cancellationToken = default)
    {
        foreach (var batch in tracks.Chunk(100))
            await SendAsync(HttpMethod.Post, $"playlists/{Id(playlistId)}/tracks", new { uris = batch.Select(x => x.Uri).ToArray() }, cancellationToken);
    }

    private async Task<IReadOnlyList<Track>> AnnotateSavedAsync(IReadOnlyList<Track> tracks, CancellationToken cancellationToken)
    {
        if (tracks.Count == 0) return tracks;
        await _savedLock.WaitAsync(cancellationToken);
        try
        {
            var staleBefore = _clock.GetUtcNow().AddSeconds(-60);
            var missing = tracks.Select(x => x.Id).Distinct(StringComparer.Ordinal)
                .Where(id => !_savedStates.TryGetValue(id, out var state) || state.CheckedAt <= staleBefore).ToArray();
            foreach (var batch in missing.Chunk(50))
            {
                using var json = await GetAsync("me/tracks/contains?ids=" + string.Join(',', batch.Select(Id)), cancellationToken);
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != batch.Length ||
                    root.EnumerateArray().Any(x => x.ValueKind is not (JsonValueKind.True or JsonValueKind.False)))
                    throw new MusicServiceException("Spotify returned incomplete saved-song information. Please try again.");
                for (var i = 0; i < batch.Length; i++)
                    _savedStates[batch[i]] = (root[i].GetBoolean(), _clock.GetUtcNow());
            }
            return tracks.Select(track => track with { IsLiked = _savedStates[track.Id].Liked }).ToArray();
        }
        finally { _savedLock.Release(); }
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var request = await RequestAsync(HttpMethod.Get, path, null, cancellationToken);
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
    }

    private async Task<List<JsonElement>> ReadPagesAsync(string path, CancellationToken cancellationToken, string? nested = null)
    {
        var result = new List<JsonElement>();
        string? next = path;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (!string.IsNullOrEmpty(next))
        {
            if (!visited.Add(next)) throw new MusicServiceException("Spotify returned a repeated results page.");
            using var json = await GetAsync(next, cancellationToken);
            var root = nested is null ? json.RootElement : Object(json.RootElement, nested);
            result.AddRange(Array(root, "items").Select(x => x.Clone()));
            next = Text(root, "next");
        }
        return result;
    }

    private async Task SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = await RequestAsync(method, path, body, cancellationToken);
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<HttpRequestMessage> RequestAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var uri = new Uri(ApiBase, path);
        if (uri.Scheme != "https" || uri.Host != ApiBase.Host || uri.Port != 443 || !uri.AbsolutePath.StartsWith("/v1/", StringComparison.Ordinal))
            throw new MusicServiceException("Spotify returned an unexpected API destination.");
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await auth.GetAccessTokenAsync(cancellationToken));
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return request;
    }

    private static Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (response.IsSuccessStatusCode) return Task.CompletedTask;
        var code = (int)response.StatusCode;
        var message = code switch
        {
            401 => "Your Spotify session needs to be renewed. Sign out and connect again in Settings.",
            403 => "Spotify denied this action. Check Premium, app development-mode access, account permissions, and device restrictions.",
            404 => "No active Spotify device or the requested content is unavailable. Open Spotify, start a track, then select its device in Music.",
            429 => "Spotify's request limit was reached. Wait " + (response.Headers.RetryAfter?.Delta?.TotalSeconds.ToString("0") ?? "a few") + " seconds and try again.",
            _ => $"Spotify request failed (HTTP {code}). Please try again."
        };
        throw new MusicServiceException(message, code);
    }

    private static string Id(string value) => Uri.EscapeDataString(value);
    private static string Text(JsonElement element, string property, string fallback = "") =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback : fallback;
    private static int Number(JsonElement element, string property, int fallback = 0) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : fallback;
    private static bool Boolean(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
    private static JsonElement Object(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object ? value : default;
    private static IEnumerable<JsonElement> Array(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
    private static IEnumerable<JsonElement> Items(JsonElement element, string property) => Array(Object(element, property), "items");
    private static string Image(JsonElement element) => Text(Array(element, "images").FirstOrDefault(), "url");
    private static string ArtistNames(JsonElement element) => string.Join(", ", Array(element, "artists").Select(x => Text(x, "name")));

    private static Track? ReadTrack(JsonElement element) => ReadTrack(element, null);
    private static Track? ReadTrack(JsonElement element, Album? parent)
    {
        if (element.ValueKind != JsonValueKind.Object || string.IsNullOrEmpty(Text(element, "id")) || Text(element, "type", "track") != "track") return null;
        var album = Object(element, "album");
        return new Track(Text(element, "id"), Text(element, "name", "Untitled"), ArtistNames(element),
            Text(Array(element, "artists").FirstOrDefault(), "id"), Text(album, "name", parent?.Title ?? ""),
            Text(album, "id", parent?.Id ?? ""), Image(album) is { Length: > 0 } image ? image : parent?.ArtworkUrl ?? "",
            Number(element, "duration_ms"), Text(element, "uri"));
    }
    private static Album? ReadAlbum(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || string.IsNullOrEmpty(Text(element, "id"))) return null;
        var release = Text(element, "release_date");
        var year = release.Length >= 4 && int.TryParse(release[..4], out var number) ? number : 0;
        return new Album(Text(element, "id"), Text(element, "name", "Untitled"), ArtistNames(element),
            Text(Array(element, "artists").FirstOrDefault(), "id"), Image(element), year, Text(element, "uri"));
    }
    private static Artist? ReadArtist(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || string.IsNullOrEmpty(Text(element, "id"))) return null;
        return new Artist(Text(element, "id"), Text(element, "name"), Image(element), Number(Object(element, "followers"), "total"));
    }
    private static Playlist? ReadPlaylist(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || string.IsNullOrEmpty(Text(element, "id"))) return null;
        return new Playlist(Text(element, "id"), Text(element, "name"), Text(element, "description"), Image(element),
            Text(element, "uri"), Number(Object(element, "tracks"), "total"));
    }
}
