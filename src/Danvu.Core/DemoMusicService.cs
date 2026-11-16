namespace Danvu.Core;

/// <summary>A deterministic, silent player simulation. Never represents demo tracks as Spotify audio.</summary>
public sealed class DemoMusicService : IMusicService
{
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly List<Track> _queue = DemoCatalog.Tracks.Take(15).ToList();
    private readonly HashSet<string> _liked = DemoCatalog.Tracks.Where(x => x.IsLiked).Select(x => x.Id).ToHashSet();
    private readonly List<Playlist> _playlists = DemoCatalog.Playlists.ToList();
    private readonly Dictionary<string, List<Track>> _playlistTracks = new();
    private int _index;
    private int _position;
    private bool _playing;
    private bool _shuffle;
    private RepeatMode _repeat;
    private int _volume = 65;
    private DateTimeOffset _updated;

    public DemoMusicService(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _updated = _clock.GetUtcNow();
        for (var i = 0; i < _playlists.Count; i++)
            _playlistTracks[_playlists[i].Id] = DemoCatalog.Tracks.Skip(i * 7).Take(_playlists[i].TrackCount).ToList();
    }

    public bool IsDemo => true;
    public bool CanReorderQueue => true;
    private Track WithLike(Track track) => track with { IsLiked = _liked.Contains(track.Id) };
    private IReadOnlyList<Track> Liked(IEnumerable<Track> tracks) => tracks.Select(WithLike).ToArray();

    public Task<HomeContent> GetHomeAsync(CancellationToken cancellationToken = default) =>
        Completed(new HomeContent(DemoCatalog.Albums[0], DemoCatalog.Albums, _playlists.ToArray(), DemoCatalog.Artists), cancellationToken);

    public Task<SearchResults> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var term = query.Trim();
        bool Matches(string value) => value.Contains(term, StringComparison.OrdinalIgnoreCase);
        return Task.FromResult(new SearchResults(Liked(DemoCatalog.Tracks.Where(x => Matches(x.Title) || Matches(x.Artist) || Matches(x.Album))),
            DemoCatalog.Albums.Where(x => Matches(x.Title) || Matches(x.Artist)).ToArray(),
            DemoCatalog.Artists.Where(x => Matches(x.Name)).ToArray(), _playlists.Where(x => Matches(x.Name)).ToArray()));
    }

    public Task<IReadOnlyList<Track>> GetAlbumTracksAsync(string albumId, CancellationToken cancellationToken = default) =>
        Completed(Liked(DemoCatalog.Tracks.Where(x => x.AlbumId == albumId)), cancellationToken);

    public Task<ArtistDetail> GetArtistAsync(string artistId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var artist = DemoCatalog.Artists.FirstOrDefault(x => x.Id == artistId)
            ?? throw new MusicServiceException("This artist is not in the demo catalog.");
        return Task.FromResult(new ArtistDetail(artist, Liked(DemoCatalog.Tracks.Where(x => x.ArtistId == artistId).Take(5)),
            DemoCatalog.Albums.Where(x => x.ArtistId == artistId).ToArray()));
    }

    public Task<IReadOnlyList<Track>> GetPlaylistTracksAsync(string playlistId, CancellationToken cancellationToken = default) =>
        Completed(Liked(_playlistTracks.GetValueOrDefault(playlistId) ?? []), cancellationToken);

    public Task<LibraryContent> GetLibraryAsync(CancellationToken cancellationToken = default) =>
        Completed(new LibraryContent(Liked(DemoCatalog.Tracks.Where(x => _liked.Contains(x.Id))),
            DemoCatalog.Albums, DemoCatalog.Artists, _playlists.ToArray()), cancellationToken);

    public Task<UserProfile> GetProfileAsync(CancellationToken cancellationToken = default) =>
        Completed(new UserProfile("demo", "Music lover", "", "Demo"), cancellationToken);

    public Task<PlaybackSnapshot> GetPlaybackAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Tick();
            return Task.FromResult(new PlaybackSnapshot(_queue.Count == 0 ? null : WithLike(_queue[_index]),
                _position, _playing, _shuffle, _repeat, _volume, "Silent demo · no audio"));
        }
    }

    public Task<IReadOnlyList<PlaybackDevice>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
        Completed<IReadOnlyList<PlaybackDevice>>([new("demo", "Silent demo · no audio", true, false, "Computer", _volume)], cancellationToken);

    public Task<IReadOnlyList<Track>> GetQueueAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult(Liked(_queue.Skip(_index + 1)));
    }

    public Task PlayAsync(IReadOnlyList<Track> tracks, int startIndex = 0, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(tracks);
        if (tracks.Count == 0) return Task.CompletedTask;
        if (tracks.Any(x => x.DurationMs <= 0)) throw new ArgumentException("Tracks must have a positive duration.", nameof(tracks));
        if (startIndex < 0 || startIndex >= tracks.Count) throw new ArgumentOutOfRangeException(nameof(startIndex));
        lock (_gate)
        {
            _queue.Clear(); _queue.AddRange(tracks); _index = startIndex; _position = 0;
            _playing = true; _updated = _clock.GetUtcNow();
            if (_shuffle) ShuffleUpcoming();
        }
        return Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) { Tick(); _playing = false; }
        return Task.CompletedTask;
    }
    public Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) { _updated = _clock.GetUtcNow(); _playing = _queue.Count > 0; }
        return Task.CompletedTask;
    }
    public Task SeekAsync(int positionMs, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_queue.Count > 0) _position = Math.Clamp(positionMs, 0, _queue[_index].DurationMs);
            _updated = _clock.GetUtcNow();
        }
        return Task.CompletedTask;
    }
    public Task SkipAsync(bool next, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Tick();
            if (_queue.Count == 0) return Task.CompletedTask;
            if (!next && _position > 3_000) _position = 0;
            else { _index = Math.Clamp(_index + (next ? 1 : -1), 0, _queue.Count - 1); _position = 0; }
            _updated = _clock.GetUtcNow();
        }
        return Task.CompletedTask;
    }
    public Task SetVolumeAsync(int volume, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); lock (_gate) _volume = Math.Clamp(volume, 0, 100); return Task.CompletedTask; }
    public Task SetShuffleAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (enabled && !_shuffle) ShuffleUpcoming();
            _shuffle = enabled;
        }
        return Task.CompletedTask;
    }
    public Task SetRepeatAsync(RepeatMode mode, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); lock (_gate) _repeat = mode; return Task.CompletedTask; }
    public Task SetLikedAsync(string trackId, bool liked, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); if (liked) _liked.Add(trackId); else _liked.Remove(trackId); return Task.CompletedTask; }
    public Task EnqueueAsync(Track track, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (track.DurationMs <= 0) throw new ArgumentException("Tracks must have a positive duration.", nameof(track));
        lock (_gate) _queue.Add(track);
        return Task.CompletedTask;
    }
    public Task MoveQueueItemAsync(int fromIndex, int toIndex, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var from = _index + 1 + fromIndex; var to = _index + 1 + toIndex;
            if (from <= _index || to <= _index || from >= _queue.Count || to >= _queue.Count)
                throw new ArgumentOutOfRangeException(nameof(fromIndex));
            var item = _queue[from]; _queue.RemoveAt(from); _queue.Insert(to, item);
        }
        return Task.CompletedTask;
    }
    public Task TransferAsync(string deviceId, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    public Task<Playlist> CreatePlaylistAsync(string name, string description, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Give the playlist a name.", nameof(name));
        var id = $"demo-{Guid.NewGuid():N}";
        var playlist = new Playlist(id, name.Trim(), description, DemoCatalog.Albums[0].ArtworkUrl, $"demo:playlist:{id}");
        _playlists.Add(playlist); _playlistTracks[id] = [];
        return Task.FromResult(playlist);
    }
    public Task AddToPlaylistAsync(string playlistId, IReadOnlyList<Track> tracks, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_playlistTracks.TryGetValue(playlistId, out var target)) throw new MusicServiceException("Playlist not found.");
        target.AddRange(tracks);
        var index = _playlists.FindIndex(x => x.Id == playlistId);
        _playlists[index] = _playlists[index] with { TrackCount = target.Count };
        return Task.CompletedTask;
    }

    private static Task<T> Completed<T>(T value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(value);
    }

    private void Tick()
    {
        var now = _clock.GetUtcNow();
        if (_playing && _queue.Count > 0)
        {
            var elapsed = Math.Max(0, (long)(now - _updated).TotalMilliseconds);
            var position = _position + elapsed;
            if (_repeat == RepeatMode.One) position %= _queue[_index].DurationMs;
            if (_repeat == RepeatMode.All && position > _queue.Sum(x => (long)x.DurationMs))
                position %= _queue.Sum(x => (long)x.DurationMs);
            // Consume elapsed time across track boundaries; shuffle changes the upcoming queue itself.
            while (_playing && position >= _queue[_index].DurationMs)
            {
                position -= _queue[_index].DurationMs;
                if (_repeat == RepeatMode.One) continue;
                if (_index + 1 < _queue.Count) _index++;
                else if (_repeat == RepeatMode.All) _index = 0;
                else { _playing = false; position = _queue[_index].DurationMs; }
            }
            _position = (int)Math.Min(position, int.MaxValue);
        }
        _updated = now;
    }

    private void ShuffleUpcoming()
    {
        var original = _queue.Skip(_index + 1).Select(x => x.Id).ToArray();
        for (var i = _queue.Count - 1; i > _index + 1; i--)
        {
            var j = Random.Shared.Next(_index + 1, i + 1);
            (_queue[i], _queue[j]) = (_queue[j], _queue[i]);
        }
        if (original.Length > 1 && original.SequenceEqual(_queue.Skip(_index + 1).Select(x => x.Id)))
            (_queue[_index + 1], _queue[^1]) = (_queue[^1], _queue[_index + 1]);
    }
}
