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
    private int _cacheGeneration;

    public ImagePreloader(int capacity = 4) => _capacity = Math.Max(2, capacity);

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
        var generation = Volatile.Read(ref _cacheGeneration);
        await _loadSlots.WaitAsync();
        try
        {
            var image = await Task.Run(() => Decode(path));
            Remember(path, generation);
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

    private void Remember(string path, int generation)
    {
        lock (_trimLock)
        {
            if (generation != _cacheGeneration)
                return;
            _cacheOrder.Enqueue(path);
            while (_cacheOrder.Count > _capacity && _cacheOrder.TryDequeue(out var oldest))
                _cache.TryRemove(oldest, out _);
        }
    }

    public void Clear()
    {
        lock (_trimLock)
        {
            _cacheGeneration++;
            _cacheOrder.Clear();
            _cache.Clear();
        }
    }

    public void Dispose()
    {
        Clear();
        _loadSlots.Dispose();
    }
}
