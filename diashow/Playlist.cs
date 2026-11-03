namespace diashow;

public sealed class Playlist
{
    private readonly List<MediaItem> _all;
    private readonly Random _random = new();
    private List<MediaItem> _unseen = [];
    private readonly List<MediaItem> _history = [];
    private List<MediaItem> _orderedAll = [];
    private int _historyIndex = -1;
    private PlaybackOrder _order;
    private SortDirection _direction;
    private MediaFilter _mediaFilter;
    private HashSet<string>? _scopePaths;
    private PlaylistEndMode _endMode;
    private bool _appendRemainderStarted;
    private bool _scopeComplete = true;

    public MediaItem? Current => _historyIndex >= 0 && _historyIndex < _history.Count ? _history[_historyIndex] : null;
    public int CurrentPosition => _historyIndex >= 0
        ? _history.Take(_historyIndex + 1).Count(IsVisibleInActivePlaylist)
        : 0;
    public int TotalCount => _appendRemainderStarted
        ? _all.Count(item => IsIncluded(item.Kind))
        : Filtered().Count;
    public bool CanGoBack => _historyIndex > 0;
    public bool IsPlaylistActive => _scopePaths is not null;
    public IReadOnlyList<MediaItem> Items => _all;
    public IEnumerable<MediaItem> PreloadCandidates(int count) =>
        new[] { Current }.Concat(_unseen).Where(x => x is not null).Take(Math.Max(0, count) + 1)!;

    public IReadOnlyList<MediaItem> PreviewPreviousItems(int count) =>
        _history
            .Take(Math.Max(0, _historyIndex))
            .Reverse()
            .Where(IsVisibleInActivePlaylist)
            .Take(Math.Max(0, count))
            .ToArray();

    public IReadOnlyList<MediaItem> PreviewUpcomingItems(int count)
    {
        var amount = Math.Max(0, count);
        if (amount == 0)
            return [];

        return _history
            .Skip(Math.Max(0, _historyIndex + 1))
            .Concat(_unseen)
            .Where(IsVisibleInActivePlaylist)
            .Take(amount)
            .ToArray();
    }

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
        RebuildUnseen(reorderFolder: true);
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
        RebuildUnseen(reorderFolder: true);
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
        RebuildUnseen(reorderFolder: true);
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
        RebuildUnseen(reorderFolder: true);
    }

    public void SetActivePlaylist(
        IEnumerable<string>? paths,
        PlaylistEndMode endMode = PlaylistEndMode.LoopPlaylist,
        bool resetHistory = true,
        bool scanComplete = true)
    {
        _scopePaths = paths switch
        {
            null => null,
            HashSet<string> set when ReferenceEquals(set.Comparer, StringComparer.OrdinalIgnoreCase) => set,
            _ => paths.ToHashSet(StringComparer.OrdinalIgnoreCase)
        };
        _endMode = endMode;
        _scopeComplete = scanComplete;
        if (resetHistory)
        {
            _history.Clear();
            _historyIndex = -1;
            _appendRemainderStarted = false;
        }
        RebuildUnseen();
    }

    public void ClearActivePlaylist()
    {
        _scopePaths = null;
        _appendRemainderStarted = false;
        _scopeComplete = true;
        _history.Clear();
        _historyIndex = -1;
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
        while (_historyIndex < _history.Count - 1)
        {
            var historical = _history[++_historyIndex];
            if (IsVisibleInActivePlaylist(historical))
                return historical;
        }

        if (_unseen.Count == 0)
        {
            if (_scopePaths is not null &&
                (!_scopeComplete || !HasVisiblePlaylistMembers()))
                return null;

            if (_scopePaths is not null && _endMode == PlaylistEndMode.AppendNonPlaylistItems &&
                !_appendRemainderStarted)
            {
                _appendRemainderStarted = true;
                RebuildUnseen(reorderFiltered: true);
            }
            else if (_scopePaths is not null && _appendRemainderStarted)
            {
                _scopePaths = null;
                _appendRemainderStarted = false;
                _history.Clear();
                _historyIndex = -1;
                RebuildUnseen(reorderFolder: true);
            }
            else if (_scopePaths is not null)
            {
                _history.Clear();
                _historyIndex = -1;
                RebuildUnseen(reorderFiltered: true);
            }
        }

        if (_unseen.Count == 0)
        {
            var loop = Filtered();
            if (loop.Count == 0) return null;
            _history.Clear();
            _historyIndex = -1;
            RebuildUnseen(reorderFiltered: true);
        }

        var next = _unseen[0];
        _unseen.RemoveAt(0);
        _history.Add(next);
        _historyIndex++;
        return next;
    }

    public MediaItem? Previous()
    {
        while (CanGoBack)
        {
            var previous = _history[--_historyIndex];
            if (IsVisibleInActivePlaylist(previous))
                return previous;
        }
        return Current;
    }

    public MediaItem? JumpTo(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var historyIndex = _history.FindIndex(item =>
            IsVisibleInActivePlaylist(item) &&
            string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
        if (historyIndex >= 0)
        {
            _historyIndex = historyIndex;
            return Current;
        }

        if (!_unseen.Any(item =>
                IsVisibleInActivePlaylist(item) &&
                string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)))
            return null;

        while (_unseen.Count > 0)
        {
            var next = Next();
            if (next is null)
                return null;
            if (string.Equals(next.Path, path, StringComparison.OrdinalIgnoreCase))
                return next;
        }

        return null;
    }

    private List<MediaItem> Filtered() => _all
        .Where(item => IsIncluded(item.Kind) && IsInActiveScope(item))
        .ToList();

    private bool IsVisibleInActivePlaylist(MediaItem item) =>
        IsIncluded(item.Kind) &&
        (_appendRemainderStarted || IsInActiveScope(item));

    private bool HasVisiblePlaylistMembers() =>
        _scopePaths is not null &&
        _all.Any(item => IsIncluded(item.Kind) && _scopePaths.Contains(item.Path));

    private bool IsInActiveScope(MediaItem item) =>
        _scopePaths is null ||
        (_appendRemainderStarted
            ? !_scopePaths.Contains(item.Path)
            : _scopePaths.Contains(item.Path));

    private bool IsIncluded(MediaKind kind) =>
        _mediaFilter switch
        {
            MediaFilter.Images => kind == MediaKind.Image,
            MediaFilter.Videos => kind == MediaKind.Video,
            _ => true
        };

    private void RebuildUnseen(bool reorderFolder = false, bool reorderFiltered = false)
    {
        if (reorderFolder)
            _orderedAll = Order(_all);
        var seen = _history.Take(Math.Max(0, _historyIndex + 1))
            .Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ordered = reorderFiltered
            ? Order(Filtered())
            : _orderedAll.Where(item => IsIncluded(item.Kind) && IsInActiveScope(item)).ToList();
        _unseen = ordered.Where(item => !seen.Contains(item.Path)).ToList();
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
                    ? DateTime.Compare(GetCreationTimeUtc(left), GetCreationTimeUtc(right))
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

    private static DateTime GetCreationTimeUtc(MediaItem item) =>
        item.CreationTimeUtc ?? File.GetCreationTimeUtc(item.Path);
}
