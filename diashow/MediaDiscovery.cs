using System.Windows.Media.Imaging;
using System.Threading.Channels;

namespace diashow;

public static class MediaDiscovery
{
    public static ChannelReader<MediaItem> Stream(string folder, CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<MediaItem>(new UnboundedChannelOptions
        {
            SingleWriter = true,
            SingleReader = true
        });

        _ = Task.Run(async () =>
        {
            try
            {
                if (!Directory.Exists(folder))
                    return;

                foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var kind = await Task.Run(() => TryImage(path), cancellationToken);
                    await channel.Writer.WriteAsync(new MediaItem(path, kind ? MediaKind.Image : MediaKind.Video),
                        cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, cancellationToken);

        return channel.Reader;
    }

    public static IReadOnlyList<MediaItem> Find(string folder)
    {
        if (!Directory.Exists(folder))
            return [];

        var result = new List<MediaItem>();
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                     .OrderBy(static p => p, StringComparer.CurrentCultureIgnoreCase))
        {
            if (TryImage(path))
                result.Add(new MediaItem(path, MediaKind.Image));
            else
                result.Add(new MediaItem(path, MediaKind.Video));
        }
        return result;
    }

    private static bool TryImage(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
            or ArgumentException or InvalidOperationException or FileFormatException)
        {
            return false;
        }
    }
}
