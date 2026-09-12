namespace diashow;

public sealed class MediaFolderMonitor : IDisposable
{
    private readonly FileSystemWatcher _watcher;

    public event Action<MediaItem>? MediaAdded;
    public event Action<string>? MediaRemoved;

    public MediaFolderMonitor(string folder)
    {
        _watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
            EnableRaisingEvents = false
        };
        _watcher.Created += Watcher_Created;
        _watcher.Deleted += Watcher_Deleted;
        _watcher.Renamed += Watcher_Renamed;
        _watcher.EnableRaisingEvents = true;
    }

    private void Watcher_Created(object sender, FileSystemEventArgs e)
    {
        if (File.Exists(e.FullPath))
            MediaAdded?.Invoke(MediaDiscovery.CreateItem(e.FullPath));
    }

    private void Watcher_Deleted(object sender, FileSystemEventArgs e) =>
        MediaRemoved?.Invoke(e.FullPath);

    private void Watcher_Renamed(object sender, RenamedEventArgs e)
    {
        MediaRemoved?.Invoke(e.OldFullPath);
        if (File.Exists(e.FullPath))
            MediaAdded?.Invoke(MediaDiscovery.CreateItem(e.FullPath));
    }

    public void Dispose()
    {
        _watcher.Created -= Watcher_Created;
        _watcher.Deleted -= Watcher_Deleted;
        _watcher.Renamed -= Watcher_Renamed;
        _watcher.Dispose();
    }
}
