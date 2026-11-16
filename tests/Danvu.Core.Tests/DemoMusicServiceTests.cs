using Danvu.Core;

namespace Danvu.Core.Tests;

public sealed class DemoMusicServiceTests
{
    [Fact]
    public async Task CatalogHasNavigableArtistsAlbumsAndPlayableTracks()
    {
        var service = new DemoMusicService();
        var home = await service.GetHomeAsync();

        Assert.NotNull(home.FeaturedAlbum);
        Assert.True(home.Albums.Count >= 8);
        foreach (var album in home.Albums)
        {
            var tracks = await service.GetAlbumTracksAsync(album.Id);
            var artist = await service.GetArtistAsync(album.ArtistId);
            Assert.NotEmpty(tracks);
            Assert.All(tracks, track => Assert.Equal(album.Id, track.AlbumId));
            Assert.Contains(artist.Albums, item => item.Id == album.Id);
            Assert.All(artist.TopTracks, track => Assert.Equal(artist.Artist.Id, track.ArtistId));
        }
    }

    [Fact]
    public async Task SearchIsCaseInsensitiveAndFindsArtistAndAlbumContent()
    {
        var service = new DemoMusicService();
        var artist = await service.SearchAsync("lUnA vAlE");
        var album = await service.SearchAsync("blue hour");
        var missing = await service.SearchAsync("no-such-record-72984");

        Assert.NotEmpty(artist.Artists);
        Assert.NotEmpty(artist.Tracks);
        Assert.NotEmpty(artist.Albums);
        Assert.Contains(album.Albums, item => item.Title == "Blue Hour");
        Assert.Contains(album.Tracks, item => item.Title == "Blue Hour");
        Assert.Empty(missing.Tracks);
        Assert.Empty(missing.Albums);
        Assert.Empty(missing.Artists);
    }

    [Fact]
    public async Task PlaybackClockRunsOnlyWhilePlaying()
    {
        var clock = new ManualClock();
        var service = new DemoMusicService(clock);
        await service.PlayAsync(DemoCatalog.Tracks, 2);
        clock.Advance(12_500);
        var playing = await service.GetPlaybackAsync();
        await service.PauseAsync();
        clock.Advance(8_000);
        var paused = await service.GetPlaybackAsync();
        await service.ResumeAsync();
        clock.Advance(500);
        var resumed = await service.GetPlaybackAsync();

        Assert.True(playing.IsPlaying);
        Assert.Equal(DemoCatalog.Tracks[2].Id, playing.Track?.Id);
        Assert.Equal(12_500, playing.PositionMs);
        Assert.False(paused.IsPlaying);
        Assert.Equal(12_500, paused.PositionMs);
        Assert.Equal(13_000, resumed.PositionMs);
        Assert.Contains("no audio", playing.DeviceName);
    }

    [Fact]
    public async Task ClockConsumesTimeAcrossMultipleTrackBoundaries()
    {
        var clock = new ManualClock();
        var service = new DemoMusicService(clock);
        var tracks = DemoCatalog.Tracks.Take(3).ToArray();
        await service.PlayAsync(tracks);
        clock.Advance(tracks[0].DurationMs + tracks[1].DurationMs + 750);

        var playback = await service.GetPlaybackAsync();

        Assert.Equal(tracks[2].Id, playback.Track?.Id);
        Assert.Equal(750, playback.PositionMs);
        Assert.True(playback.IsPlaying);
    }

    [Fact]
    public async Task EndOfQueueStopsAtFinalTrackDuration()
    {
        var clock = new ManualClock();
        var service = new DemoMusicService(clock);
        var track = DemoCatalog.Tracks[0];
        await service.PlayAsync([track]);
        clock.Advance(track.DurationMs + 1000);

        var playback = await service.GetPlaybackAsync();

        Assert.False(playback.IsPlaying);
        Assert.Equal(track.DurationMs, playback.PositionMs);
    }

    [Fact]
    public async Task RepeatOnePreservesTrackAndWrapsPosition()
    {
        var clock = new ManualClock();
        var service = new DemoMusicService(clock);
        await service.PlayAsync(DemoCatalog.Tracks.Take(2).ToArray());
        await service.SetRepeatAsync(RepeatMode.One);
        clock.Advance(DemoCatalog.Tracks[0].DurationMs * 2 + 400);

        var playback = await service.GetPlaybackAsync();

        Assert.Equal(DemoCatalog.Tracks[0].Id, playback.Track?.Id);
        Assert.Equal(400, playback.PositionMs);
        Assert.True(playback.IsPlaying);
    }

    [Fact]
    public async Task RepeatAllWrapsQueue()
    {
        var clock = new ManualClock();
        var service = new DemoMusicService(clock);
        var tracks = DemoCatalog.Tracks.Take(2).ToArray();
        await service.PlayAsync(tracks);
        await service.SetRepeatAsync(RepeatMode.All);
        clock.Advance(tracks.Sum(track => track.DurationMs) + 100);

        var playback = await service.GetPlaybackAsync();

        Assert.Equal(tracks[0].Id, playback.Track?.Id);
        Assert.Equal(100, playback.PositionMs);
        Assert.True(playback.IsPlaying);
    }

    [Fact]
    public async Task SeekAndVolumeClampToAllowedRanges()
    {
        var service = new DemoMusicService(new ManualClock());
        var track = DemoCatalog.Tracks[0];
        await service.PlayAsync([track]);
        await service.PauseAsync();
        await service.SeekAsync(-100);
        await service.SetVolumeAsync(-1);
        var low = await service.GetPlaybackAsync();
        await service.SeekAsync(int.MaxValue);
        await service.SetVolumeAsync(101);
        var high = await service.GetPlaybackAsync();

        Assert.Equal(0, low.PositionMs);
        Assert.Equal(0, low.Volume);
        Assert.Equal(track.DurationMs, high.PositionMs);
        Assert.Equal(100, high.Volume);
    }

    [Fact]
    public async Task PreviousRestartsTrackAfterThreeSecondsThenMovesBack()
    {
        var service = new DemoMusicService(new ManualClock());
        await service.PlayAsync(DemoCatalog.Tracks.Take(3).ToArray(), 1);
        await service.SeekAsync(4000);
        await service.SkipAsync(false);
        var restarted = await service.GetPlaybackAsync();
        await service.SkipAsync(false);
        var previous = await service.GetPlaybackAsync();

        Assert.Equal(DemoCatalog.Tracks[1].Id, restarted.Track?.Id);
        Assert.Equal(0, restarted.PositionMs);
        Assert.Equal(DemoCatalog.Tracks[0].Id, previous.Track?.Id);
    }

    [Fact]
    public async Task QueueReorderAndEnqueuePreserveCurrentTrack()
    {
        var service = new DemoMusicService(new ManualClock());
        var tracks = DemoCatalog.Tracks.Take(4).ToArray();
        await service.PlayAsync(tracks);
        await service.MoveQueueItemAsync(0, 2);
        await service.EnqueueAsync(DemoCatalog.Tracks[4]);

        var queue = await service.GetQueueAsync();
        var current = await service.GetPlaybackAsync();

        Assert.Equal(new[] { tracks[2].Id, tracks[3].Id, tracks[1].Id, DemoCatalog.Tracks[4].Id }, queue.Select(track => track.Id));
        Assert.Equal(tracks[0].Id, current.Track?.Id);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.MoveQueueItemAsync(-1, 0));
    }

    [Fact]
    public async Task ShuffleChangesUpcomingOrderWithoutDroppingTracksOrChangingCurrent()
    {
        var service = new DemoMusicService(new ManualClock());
        var tracks = DemoCatalog.Tracks.Take(10).ToArray();
        await service.PlayAsync(tracks, 2);
        var before = await service.GetQueueAsync();

        await service.SetShuffleAsync(true);

        var after = await service.GetQueueAsync();
        var current = await service.GetPlaybackAsync();
        Assert.True(current.Shuffle);
        Assert.Equal(tracks[2].Id, current.Track?.Id);
        Assert.False(before.Select(track => track.Id).SequenceEqual(after.Select(track => track.Id)));
        Assert.Equal(before.Select(track => track.Id).Order(), after.Select(track => track.Id).Order());
        await service.SkipAsync(true);
        Assert.Equal(after[0].Id, (await service.GetPlaybackAsync()).Track?.Id);
    }

    [Fact]
    public async Task LikingTrackUpdatesLibraryAndSearchResults()
    {
        var service = new DemoMusicService();
        var track = DemoCatalog.Tracks.First(item => !item.IsLiked);
        await service.SetLikedAsync(track.Id, true);
        var saved = await service.GetLibraryAsync();
        var results = await service.SearchAsync(track.Title);
        await service.SetLikedAsync(track.Id, false);
        var removed = await service.GetLibraryAsync();

        Assert.Contains(saved.Tracks, item => item.Id == track.Id && item.IsLiked);
        Assert.Contains(results.Tracks, item => item.Id == track.Id && item.IsLiked);
        Assert.DoesNotContain(removed.Tracks, item => item.Id == track.Id);
    }

    [Fact]
    public async Task NewPlaylistCanBePopulatedAndDiscovered()
    {
        var service = new DemoMusicService();
        var playlist = await service.CreatePlaylistAsync("  Evening walk  ", "A personal selection");
        await service.AddToPlaylistAsync(playlist.Id, DemoCatalog.Tracks.Take(2).ToArray());
        var library = await service.GetLibraryAsync();
        var tracks = await service.GetPlaylistTracksAsync(playlist.Id);

        Assert.Equal("Evening walk", playlist.Name);
        Assert.Equal("A personal selection", playlist.Description);
        Assert.Equal(2, Assert.Single(library.Playlists, item => item.Id == playlist.Id).TrackCount);
        Assert.Equal(DemoCatalog.Tracks.Take(2).Select(item => item.Id), tracks.Select(item => item.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreatePlaylistAsync(" ", ""));
    }

    [Fact]
    public void DemoLyricsAreTimedWithinTrackAndDiscloseTheirSource()
    {
        foreach (var track in DemoCatalog.Tracks)
        {
            var lyrics = DemoLyrics.GetForTrack(track);
            Assert.NotEmpty(lyrics.Lines);
            Assert.Contains("Original demo lyrics", lyrics.Source);
            Assert.All(lyrics.Lines, line => Assert.InRange(line.TimeMs, 0, track.DurationMs));
            Assert.Equal(lyrics.Lines.OrderBy(line => line.TimeMs), lyrics.Lines);
        }
    }

    [Fact]
    public async Task CancelledPlaybackCommandDoesNotChangeCurrentTrack()
    {
        var service = new DemoMusicService(new ManualClock());
        var before = await service.GetPlaybackAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.PlayAsync(DemoCatalog.Tracks, 4, cancellation.Token));

        var after = await service.GetPlaybackAsync();
        Assert.Equal(before, after);
    }
}

internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _time = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _time;
    public void Advance(int milliseconds) => _time = _time.AddMilliseconds(milliseconds);
}
