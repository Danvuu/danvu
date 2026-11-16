namespace Danvu.Core;

/// <summary>Fictional catalog and original lyrics, free of Spotify credentials and copyrighted recordings.</summary>
public static class DemoCatalog
{
    private static string Art(string name) => $"ms-appx:///Assets/Artwork/{name}.png";
    public static IReadOnlyList<Artist> Artists { get; } =
    [
        new("luna-vale", "Luna Vale", Art("luna-vale"), 182304),
        new("north-coast", "North Coast", Art("north-coast"), 249187),
        new("the-velvets", "The Velvets", Art("the-velvets"), 108299),
        new("solana", "Solana", Art("solana"), 418953),
        new("ellis-grey", "Ellis Grey", Art("ellis-grey"), 94288)
    ];
    public static IReadOnlyList<Album> Albums { get; } =
    [
        new("afterglow", "Afterglow", "Luna Vale", "luna-vale", Art("afterglow"), 2026, "demo:album:afterglow"),
        new("blue-hour", "Blue Hour", "North Coast", "north-coast", Art("blue-hour"), 2026, "demo:album:blue-hour"),
        new("soft-focus", "Soft Focus", "Ellis Grey", "ellis-grey", Art("soft-focus"), 2025, "demo:album:soft-focus"),
        new("velvet-sky", "Velvet Sky", "The Velvets", "the-velvets", Art("velvet-sky"), 2026, "demo:album:velvet-sky"),
        new("currents", "Currents", "North Coast", "north-coast", Art("currents"), 2025, "demo:album:currents"),
        new("midnight-radio", "Midnight Radio", "The Velvets", "the-velvets", Art("midnight-radio"), 2024, "demo:album:midnight-radio"),
        new("daydream", "Daydream", "Ellis Grey", "ellis-grey", Art("daydream"), 2026, "demo:album:daydream"),
        new("golden", "Golden", "Solana", "solana", Art("golden"), 2026, "demo:album:golden"),
        new("bloom", "Bloom", "Luna Vale", "luna-vale", Art("bloom"), 2025, "demo:album:bloom"),
        new("satellites", "Satellites", "Solana", "solana", Art("satellites"), 2024, "demo:album:satellites")
    ];

    private static readonly string[][] SongNames =
    [
        ["Afterglow", "Stay a Little Longer", "Polaroid Summer", "Everything in Color", "The Way Home"],
        ["Blue Hour", "Pacific Drive", "Under the Surface", "Coastline", "Coming Up for Air"],
        ["Soft Focus", "A Quiet Place", "Window Seat", "Out of the Frame", "Little Things"],
        ["Velvet Sky", "Neon Hearts", "Slow Motion", "Somewhere Tonight", "Wide Awake"],
        ["Currents", "Ocean Floor", "Drifting", "Tidepool", "New Horizons"],
        ["Midnight Radio", "Antenna", "City Lights", "Frequency", "Last Transmission"],
        ["Daydream", "Paper Planes", "Sunday Morning", "In Between", "Soft Landing"],
        ["Golden", "Warmth", "Sun Kissed", "Almost There", "A New Day"],
        ["Bloom", "Wildflower", "Early Spring", "Growing Pains", "Let It In"],
        ["Satellites", "Orbit", "Stardust", "Gravity", "Far Away"]
    ];

    public static IReadOnlyList<Track> Tracks { get; } = Albums.SelectMany((album, index) =>
        SongNames[index].Select((title, trackIndex) => new Track($"{album.Id}-{trackIndex + 1}", title,
            album.Artist, album.ArtistId, album.Title, album.Id, album.ArtworkUrl,
            188_000 + (index * 13_000 + trackIndex * 19_000) % 78_000,
            $"demo:track:{album.Id}-{trackIndex + 1}", trackIndex == 0))).ToArray();

    public static IReadOnlyList<Playlist> Playlists { get; } =
    [
        new("favorites", "On repeat", "The ones you keep coming back to.", Art("afterglow"), "demo:playlist:favorites", 12),
        new("late-night", "Late night feelings", "For the hours that belong to you.", Art("midnight-radio"), "demo:playlist:late-night", 15),
        new("slow-sunday", "Slow Sunday", "A little room to breathe.", Art("soft-focus"), "demo:playlist:slow-sunday", 10),
        new("new-discoveries", "New discoveries", "Your next favorite is in here.", Art("golden"), "demo:playlist:new-discoveries", 16)
    ];
}

public static class DemoLyrics
{
    private static readonly string[] OriginalLines =
    [
        "We left the windows open",
        "Let the evening settle in",
        "All the roads we never traveled",
        "Feel like places we have been",
        "And the light falls on your shoulder",
        "Like a promise we could keep",
        "Let the city turn to silver",
        "Let the world outside fall asleep",
        "Stay here in the afterglow",
        "There is nowhere else to go",
        "Every color running through us",
        "Every little thing we know",
        "We can take the long way home",
        "Leave the hours on the road",
        "If the morning comes to find us",
        "We will give it something gold",
        "Hear the quiet in the distance",
        "See the skyline turning blue",
        "All the words we never needed",
        "Find their way from me to you",
        "Stay here in the afterglow",
        "Let the feeling take its time",
        "There is music in the silence",
        "There is daylight in your eyes"
    ];

    public static LyricsDocument GetForTrack(Track track)
    {
        var interval = Math.Max(1, (track.DurationMs - 12_000) / OriginalLines.Length);
        return new LyricsDocument(track.Title, "Original demo lyrics · simulated playback",
            OriginalLines.Select((line, index) => new LyricLine(3_000 + index * interval, line)).ToArray());
    }
}
