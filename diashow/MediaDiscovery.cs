using System.Threading.Channels;
using System.Windows.Media.Imaging;

namespace diashow;

public static class MediaDiscovery
{
    public static ChannelReader<MediaItem> Stream(
        string folder,
        CancellationToken cancellationToken = default,
        bool randomize = false)
    {
        var channel = Channel.CreateUnbounded<MediaItem>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = true
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
                        var randomizedPaths = randomize
                            ? EnumerateFiles(partition, partition == folder).ToList()
                            : null;
                        if (randomizedPaths is not null)
                            Shuffle(randomizedPaths);
                        var paths = (IEnumerable<string>?)randomizedPaths
                            ?? EnumerateFiles(partition, partition == folder);

                        foreach (var path in paths)
                        {
                            token.ThrowIfCancellationRequested();
                            var kind = TryImage(path);
                            await channel.Writer.WriteAsync(
                                new MediaItem(path, kind ? MediaKind.Image : MediaKind.Video), token);
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
        }, cancellationToken);

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
                .Select(path => new MediaItem(path, TryImage(path) ? MediaKind.Image : MediaKind.Video)))
            .OrderBy(static item => item.Path, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return result;
    }

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

    public static bool IsCorruptJpeg(string path)
    {
        if (!IsJpeg(path))
            return false;

        try
        {
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

    private static bool IsJpeg(string path) =>
        string.Equals(Path.GetExtension(path), ".jpg", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetExtension(path), ".jpeg", StringComparison.OrdinalIgnoreCase);
}
