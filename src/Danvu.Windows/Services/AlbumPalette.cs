using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.UI;

namespace Danvu.Windows.Services;

/// <summary>Bounded thumbnails avoid full-size decode work while extracting artwork colors.</summary>
internal sealed class AlbumPalette(HttpClient http)
{
    private const int MaxEncodedBytes = 10 * 1024 * 1024;
    private const ulong MaxPixels = 40_000_000;
    private readonly ConcurrentDictionary<string, IReadOnlyList<Color>> _cache = new(StringComparer.Ordinal);
    private static readonly Color[] Fallback = [Color.FromArgb(255, 135, 43, 74), Color.FromArgb(255, 51, 45, 98), Color.FromArgb(255, 35, 27, 47)];
    public async Task<IReadOnlyList<Color>> GetAsync(string url, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_cache.TryGetValue(url, out var cached)) return cached;
        var uri = LocalState.ResolveArtworkUri(url);
        if (uri is null) return Fallback;
        try
        {
            byte[] bytes;
            if (uri.IsFile)
            {
                await using var file = new FileStream(uri.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (file.Length > MaxEncodedBytes) return Fallback;
                bytes = await ReadBoundedAsync(file, cancellationToken);
            }
            else
            {
                using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps ||
                    response.Content.Headers.ContentLength > MaxEncodedBytes) return Fallback;
                await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
                bytes = await ReadBoundedAsync(body, cancellationToken);
            }
            using var stream = new MemoryStream(bytes).AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
            if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0 || (ulong)decoder.PixelWidth * decoder.PixelHeight > MaxPixels) return Fallback;
            var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Ignore,
                new BitmapTransform { ScaledWidth = 48, ScaledHeight = 48 }, ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.ColorManageToSRgb).AsTask(cancellationToken);
            var data = pixels.DetachPixelData();
            var bins = new Dictionary<int, (int Count, long R, long G, long B)>();
            for (var i = 0; i + 3 < data.Length; i += 4)
            {
                int r = data[i], g = data[i + 1], b = data[i + 2];
                if (Math.Max(r, Math.Max(g, b)) < 40 || Math.Min(r, Math.Min(g, b)) > 230) continue;
                var key = (r / 48 << 8) | (g / 48 << 4) | b / 48;
                bins.TryGetValue(key, out var bin);
                bins[key] = (bin.Count + 1, bin.R + r, bin.G + g, bin.B + b);
            }
            var result = bins.Values.OrderByDescending(x => x.Count).Take(3)
                .Select(x => Color.FromArgb(255, (byte)(x.R / x.Count), (byte)(x.G / x.Count), (byte)(x.B / x.Count))).ToArray();
            IReadOnlyList<Color> palette = result.Length >= 2 ? result : Fallback;
            if (_cache.Count >= 256) _cache.Clear();
            _cache.TryAdd(url, palette);
            return palette;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or ArgumentException or System.Runtime.InteropServices.COMException)
        { return Fallback; }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream source, CancellationToken cancellationToken)
    {
        using var destination = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) return destination.ToArray();
            if (destination.Length + read > MaxEncodedBytes) throw new InvalidDataException("Artwork exceeds the supported image size.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }
}
