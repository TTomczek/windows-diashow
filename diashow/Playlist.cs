namespace diashow;

public sealed class Playlist
{
    private readonly List<MediaItem> _all;
    private readonly Random _random = new();
    private List<MediaItem> _unseen = [];
    private readonly List<MediaItem> _history = [];
    private int _historyIndex = -1;
    private PlaybackOrder _order;
    private SortDirection _direction;
    private MediaFilter _mediaFilter;

    public MediaItem? Current => _historyIndex >= 0 && _historyIndex < _history.Count ? _history[_historyIndex] : null;
    public int CurrentPosition => _historyIndex >= 0
        ? _history.Take(_historyIndex + 1).Count(item => IsIncluded(item.Kind))
        : 0;
    public int TotalCount => Filtered().Count;
    public bool CanGoBack => _historyIndex > 0;
    public IEnumerable<MediaItem> PreloadCandidates(int count) =>
        new[] { Current }.Concat(_unseen).Where(x => x is not null).Take(Math.Max(0, count) + 1)!;

    public Playlist(
        IEnumerable<MediaItem> items,
        PlaybackOrder order,
        MediaFilter mediaFilter,
        SortDirection direction = SortDirection.Ascending)
    {
        _all = items.ToList();
        _order = order;
        _direction = direction;
        _mediaFilter = mediaFilter;
        RebuildUnseen();
    }

    public Playlist(
        IEnumerable<MediaItem> items,
        PlaybackOrder order,
        bool includeVideos,
        SortDirection direction = SortDirection.Ascending)
        : this(items, order, includeVideos ? MediaFilter.Both : MediaFilter.Images, direction)
    {
    }

    public void AddItems(IEnumerable<MediaItem> items)
    {
        var existing = _all.Select(static item => item.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _all.AddRange(items.Where(item => existing.Add(item.Path)));
        RebuildUnseen();
    }

    public bool RemoveItems(IEnumerable<string> paths)
    {
        var removed = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (removed.Count == 0)
            return false;

        var currentPath = Current?.Path;
        var removedThroughCurrent = _historyIndex >= 0
            ? _history.Take(_historyIndex + 1).Count(item => removed.Contains(item.Path))
            : 0;
        _all.RemoveAll(item => removed.Contains(item.Path));
        _history.RemoveAll(item => removed.Contains(item.Path));
        _historyIndex = Math.Min(_historyIndex - removedThroughCurrent, _history.Count - 1);
        RebuildUnseen();
        return currentPath is not null && removed.Contains(currentPath);
    }

    public void SetOptions(
        PlaybackOrder order,
        MediaFilter mediaFilter,
        SortDirection direction = SortDirection.Ascending)
    {
        _order = order;
        _direction = direction;
        _mediaFilter = mediaFilter;
        RebuildUnseen();
    }

    public void SetOptions(
        PlaybackOrder order,
        bool includeVideos,
        SortDirection direction = SortDirection.Ascending) =>
        SetOptions(order, includeVideos ? MediaFilter.Both : MediaFilter.Images, direction);

    public MediaItem? StartAt(string? path)
    {
        var candidate = Filtered().FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
        if (candidate is null) return Next();
        _history.Clear();
        _history.Add(candidate);
        _historyIndex = 0;
        RebuildUnseen();
        _unseen.RemoveAll(x => string.Equals(x.Path, candidate.Path, StringComparison.OrdinalIgnoreCase));
        return candidate;
    }

    public MediaItem? StartRandom()
    {
        var items = Filtered();
        if (items.Count == 0) return null;

        var candidate = items[_random.Next(items.Count)];
        _history.Clear();
        _history.Add(candidate);
        _historyIndex = 0;
        RebuildUnseen();
        _unseen.RemoveAll(x => string.Equals(x.Path, candidate.Path, StringComparison.OrdinalIgnoreCase));
        return candidate;
    }

    public MediaItem? Next()
    {
        if (_historyIndex < _history.Count - 1)
            return _history[++_historyIndex];

        if (_unseen.Count == 0)
        {
            var loop = Filtered();
            if (loop.Count == 0) return null;
            _history.Clear();
            _historyIndex = -1;
            _unseen = Order(loop);
        }

        var next = _unseen[0];
        _unseen.RemoveAt(0);
        _history.Add(next);
        _historyIndex++;
        return next;
    }

    public MediaItem? Previous()
    {
        if (!CanGoBack) return Current;
        return _history[--_historyIndex];
    }

    private List<MediaItem> Filtered() => _all
        .Where(item => IsIncluded(item.Kind))
        .ToList();

    private bool IsIncluded(MediaKind kind) =>
        _mediaFilter switch
        {
            MediaFilter.Images => kind == MediaKind.Image,
            MediaFilter.Videos => kind == MediaKind.Video,
            _ => true
        };

    private void RebuildUnseen()
    {
        var seen = _history.Take(Math.Max(0, _historyIndex + 1))
            .Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _unseen = Order(Filtered().Where(x => !seen.Contains(x.Path)));
    }

    private List<MediaItem> Order(IEnumerable<MediaItem> source)
    {
        var list = source.ToList();
        if (_order == PlaybackOrder.Random)
            for (var i = list.Count - 1; i > 0; i--)
            {
                var j = _random.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        else
        {
            list.Sort((left, right) =>
            {
                var comparison = _order == PlaybackOrder.CreationDate
                    ? DateTime.Compare(File.GetCreationTimeUtc(left.Path), File.GetCreationTimeUtc(right.Path))
                    : StringComparer.CurrentCultureIgnoreCase.Compare(left.Path, right.Path);
                if (_direction == SortDirection.Descending)
                    comparison = -comparison;
                return comparison != 0
                    ? comparison
                    : StringComparer.CurrentCultureIgnoreCase.Compare(left.Path, right.Path);
            });
        }
        return list;
    }
}
