namespace Danvu.Core;

public sealed record Track(string Id, string Title, string Artist, string ArtistId, string Album,
    string AlbumId, string ArtworkUrl, int DurationMs, string Uri, bool IsLiked = false);
public sealed record Album(string Id, string Title, string Artist, string ArtistId, string ArtworkUrl,
    int Year, string Uri);
public sealed record Artist(string Id, string Name, string ArtworkUrl, long Followers = 0);
public sealed record Playlist(string Id, string Name, string Description, string ArtworkUrl, string Uri,
    int TrackCount = 0);
public sealed record HomeContent(Album? FeaturedAlbum, IReadOnlyList<Album> Albums,
    IReadOnlyList<Playlist> Playlists, IReadOnlyList<Artist> Artists);
public sealed record SearchResults(IReadOnlyList<Track> Tracks, IReadOnlyList<Album> Albums,
    IReadOnlyList<Artist> Artists, IReadOnlyList<Playlist> Playlists);
public sealed record ArtistDetail(Artist Artist, IReadOnlyList<Track> TopTracks, IReadOnlyList<Album> Albums);
public sealed record LibraryContent(IReadOnlyList<Track> Tracks, IReadOnlyList<Album> Albums,
    IReadOnlyList<Artist> Artists, IReadOnlyList<Playlist> Playlists);
public sealed record UserProfile(string Id, string DisplayName, string ArtworkUrl, string Product);
public sealed record PlaybackDevice(string Id, string Name, bool IsActive, bool IsRestricted,
    string Type, int Volume);
public enum RepeatMode { Off, All, One }
public sealed record PlaybackSnapshot(Track? Track, int PositionMs, bool IsPlaying, bool Shuffle,
    RepeatMode Repeat, int Volume, string DeviceName);
public sealed record LyricLine(int TimeMs, string Text);
public sealed record LyricsDocument(string Title, string Source, IReadOnlyList<LyricLine> Lines);

public interface IMusicService
{
    bool IsDemo { get; }
    bool CanReorderQueue { get; }
    Task<HomeContent> GetHomeAsync(CancellationToken cancellationToken = default);
    Task<SearchResults> SearchAsync(string query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Track>> GetAlbumTracksAsync(string albumId, CancellationToken cancellationToken = default);
    Task<ArtistDetail> GetArtistAsync(string artistId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Track>> GetPlaylistTracksAsync(string playlistId, CancellationToken cancellationToken = default);
    Task<LibraryContent> GetLibraryAsync(CancellationToken cancellationToken = default);
    Task<UserProfile> GetProfileAsync(CancellationToken cancellationToken = default);
    Task<PlaybackSnapshot> GetPlaybackAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PlaybackDevice>> GetDevicesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Track>> GetQueueAsync(CancellationToken cancellationToken = default);
    Task PlayAsync(IReadOnlyList<Track> tracks, int startIndex = 0, CancellationToken cancellationToken = default);
    Task PauseAsync(CancellationToken cancellationToken = default);
    Task ResumeAsync(CancellationToken cancellationToken = default);
    Task SeekAsync(int positionMs, CancellationToken cancellationToken = default);
    Task SkipAsync(bool next, CancellationToken cancellationToken = default);
    Task SetVolumeAsync(int volume, CancellationToken cancellationToken = default);
    Task SetShuffleAsync(bool enabled, CancellationToken cancellationToken = default);
    Task SetRepeatAsync(RepeatMode mode, CancellationToken cancellationToken = default);
    Task SetLikedAsync(string trackId, bool liked, CancellationToken cancellationToken = default);
    Task EnqueueAsync(Track track, CancellationToken cancellationToken = default);
    Task MoveQueueItemAsync(int fromIndex, int toIndex, CancellationToken cancellationToken = default);
    Task TransferAsync(string deviceId, CancellationToken cancellationToken = default);
    Task<Playlist> CreatePlaylistAsync(string name, string description, CancellationToken cancellationToken = default);
    Task AddToPlaylistAsync(string playlistId, IReadOnlyList<Track> tracks, CancellationToken cancellationToken = default);
}

public sealed class MusicServiceException(string message, int? statusCode = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
}
