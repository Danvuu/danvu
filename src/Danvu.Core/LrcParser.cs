using System.Globalization;
using System.Text.RegularExpressions;

namespace Danvu.Core;

/// <summary>Reads user-supplied LRC files. No lyric scraping or unlicensed lyric service is used.</summary>
public static partial class LrcParser
{
    [GeneratedRegex(@"\[(\d{1,3}):(\d{2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex TimestampPattern();

    [GeneratedRegex(@"^\[offset:([+-]?\d+)\]$", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetPattern();

    public static LyricsDocument Parse(string content, string title = "Imported lyrics")
    {
        ArgumentNullException.ThrowIfNull(content);
        var lines = new List<LyricLine>();
        var offset = 0;
        foreach (var raw in content.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            var offsetMatch = OffsetPattern().Match(line);
            if (offsetMatch.Success && int.TryParse(offsetMatch.Groups[1].Value, out var parsedOffset))
            {
                offset = parsedOffset;
                continue;
            }
            var matches = TimestampPattern().Matches(line);
            if (matches.Count == 0) continue;
            var text = TimestampPattern().Replace(line, "").Trim();
            if (text.Length == 0) continue;
            foreach (Match match in matches)
            {
                var minutes = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                var seconds = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                if (seconds >= 60) continue;
                var fraction = match.Groups[3].Value;
                var milliseconds = fraction.Length == 0 ? 0 : int.Parse(fraction.PadRight(3, '0'), CultureInfo.InvariantCulture);
                lines.Add(new LyricLine(minutes * 60_000 + seconds * 1_000 + milliseconds, text));
            }
        }
        return new LyricsDocument(title, "Local LRC file", lines.Select(x => x with
        {
            TimeMs = (int)Math.Clamp((long)x.TimeMs + offset, 0, int.MaxValue)
        }).OrderBy(x => x.TimeMs).ToArray());
    }

    public static int FindActiveLine(IReadOnlyList<LyricLine> lines, int positionMs)
    {
        var low = 0;
        var high = lines.Count - 1;
        var result = -1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (lines[middle].TimeMs <= positionMs) { result = middle; low = middle + 1; }
            else high = middle - 1;
        }
        return result;
    }
}
