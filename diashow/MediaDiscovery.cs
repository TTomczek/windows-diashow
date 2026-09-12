using System.Threading.Channels;

namespace diashow;

public static class MediaDiscovery
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bmp", ".gif", ".jpeg", ".jpg", ".png", ".tif", ".tiff", ".webp"
    };

    public static ChannelReader<MediaItem> Stream(
        string folder,
        CancellationToken cancellationToken = default,
        bool randomize = false)
    {
        var channel = Channel.CreateBounded<MediaItem>(new BoundedChannelOptions(512)
        {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        _ = Task.Run(async () =>
        {
            try
            {
                if (!Directory.Exists(folder))
                    return;

                var partitions = EnumeratePartitions(folder).ToList();
                if (randomize)
                    Shuffle(partitions);

                await Parallel.ForEachAsync(
                    partitions,
                    new ParallelOptions
                    {
                        CancellationToken = cancellationToken,
                        MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8)
                    },
                    async (partition, token) =>
                    {
                        foreach (var path in EnumerateFiles(partition, partition == folder))
                        {
                            token.ThrowIfCancellationRequested();
                            await channel.Writer.WriteAsync(
                                new MediaItem(path, GetMediaKind(path)), token);
                        }
                    });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (IsDiscoveryException(ex))
            {
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        });

        return channel.Reader;
    }

    public static IReadOnlyList<MediaItem> Find(string folder)
    {
        if (!Directory.Exists(folder))
            return [];

        var result = EnumeratePartitions(folder)
            .AsParallel()
            .WithDegreeOfParallelism(Math.Clamp(Environment.ProcessorCount, 2, 8))
            .SelectMany(partition => EnumerateFiles(partition, partition == folder)
                .Select(path => new MediaItem(path, GetMediaKind(path))))
            .OrderBy(static item => item.Path, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return result;
    }

    public static MediaItem CreateItem(string path) => new(path, GetMediaKind(path));

    private static IEnumerable<string> EnumeratePartitions(string folder)
    {
        yield return folder;

        IEnumerator<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(
                folder, "*", SearchOption.TopDirectoryOnly).GetEnumerator();
        }
        catch (Exception ex) when (IsDiscoveryException(ex))
        {
            yield break;
        }

        using (directories)
        while (true)
        {
            bool hasNext;
            try
            {
                hasNext = directories.MoveNext();
            }
            catch (Exception ex) when (IsDiscoveryException(ex))
            {
                yield break;
            }

            if (!hasNext)
                yield break;
            yield return directories.Current;
        }
    }

    private static IEnumerable<string> EnumerateFiles(string partition, bool topDirectoryOnly)
    {
        IEnumerator<string> files;
        try
        {
            files = Directory.EnumerateFiles(
                partition,
                "*",
                topDirectoryOnly ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories).GetEnumerator();
        }
        catch (Exception ex) when (IsDiscoveryException(ex))
        {
            yield break;
        }

        using (files)
        while (true)
        {
            bool hasNext;
            try
            {
                hasNext = files.MoveNext();
            }
            catch (Exception ex) when (IsDiscoveryException(ex))
            {
                yield break;
            }

            if (!hasNext)
                yield break;
            yield return files.Current;
        }
    }

    private static void Shuffle<T>(IList<T> items)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    private static bool IsDiscoveryException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException
            or PathTooLongException;

    private static MediaKind GetMediaKind(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path))
            ? MediaKind.Image
            : MediaKind.Video;

    public static bool IsCorruptJpeg(string path)
    {
        if (!IsJpeg(path))
            return false;

        try
        {
            if (!HasJpegEndMarker(path))
                return true;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.SequentialScan);
            using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
            using var bitmap = new Bitmap(image);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    private static bool HasJpegEndMarker(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        var buffer = new byte[64 * 1024];
        var previous = (byte)0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var index = 0; index < read; index++)
            {
                if (previous == 0xFF && buffer[index] == 0xD9)
                    return true;
                previous = buffer[index];
            }
        }
        return false;
    }

    private static bool IsJpeg(string path) =>
        string.Equals(Path.GetExtension(path), ".jpg", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetExtension(path), ".jpeg", StringComparison.OrdinalIgnoreCase);
}
