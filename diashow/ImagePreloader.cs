using System.Collections.Concurrent;
using System.Windows.Media.Imaging;

namespace diashow;

public sealed class ImagePreloader : IDisposable
{
    private readonly ConcurrentDictionary<string, Task<BitmapSource>> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _loadSlots = new(2, 2);
    private readonly int _capacity;
    private readonly object _trimLock = new();
    private readonly Queue<string> _cacheOrder = [];

    public ImagePreloader(int capacity = 12) => _capacity = Math.Max(4, capacity);

    public async Task<BitmapSource> GetAsync(string path, CancellationToken cancellationToken)
    {
        var task = _cache.GetOrAdd(path, LoadAsync);
        try
        {
            return await task.WaitAsync(cancellationToken);
        }
        catch
        {
            _cache.TryRemove(path, out _);
            throw;
        }
    }

    public void Preload(IEnumerable<MediaItem> items)
    {
        foreach (var item in items.Where(x => x.Kind == MediaKind.Image))
        {
            _ = GetAsync(item.Path, CancellationToken.None).ContinueWith(
                static task => _ = task.Exception,
                TaskScheduler.Default);
        }
    }

    private async Task<BitmapSource> LoadAsync(string path)
    {
        await _loadSlots.WaitAsync();
        try
        {
            var image = await Task.Run(() => Decode(path));
            Remember(path);
            return image;
        }
        finally
        {
            _loadSlots.Release();
        }
    }

    private static BitmapSource Decode(string path)
    {
        return ImageDecoder.Decode(path);
    }

    private void Remember(string path)
    {
        lock (_trimLock)
        {
            _cacheOrder.Enqueue(path);
            while (_cacheOrder.Count > _capacity && _cacheOrder.TryDequeue(out var oldest))
                _cache.TryRemove(oldest, out _);
        }
    }

    public void Dispose()
    {
        _loadSlots.Dispose();
        _cache.Clear();
    }
}
