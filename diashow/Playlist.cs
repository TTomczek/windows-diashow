namespace diashow;

public sealed class Playlist
{
    private readonly List<MediaItem> _all;
    private readonly Random _random = new();
    private List<MediaItem> _unseen = [];
    private readonly List<MediaItem> _history = [];
    private int _historyIndex = -1;
    private PlaybackOrder _order;
    private bool _includeVideos;

    public MediaItem? Current => _historyIndex >= 0 && _historyIndex < _history.Count ? _history[_historyIndex] : null;
    public int CurrentPosition => _historyIndex >= 0 ? _historyIndex + 1 : 0;
    public int TotalCount => _includeVideos ? _all.Count : _all.Count(static item => item.Kind == MediaKind.Image);
    public bool CanGoBack => _historyIndex > 0;
    public IEnumerable<MediaItem> PreloadCandidates(int count) =>
        new[] { Current }.Concat(_unseen).Where(x => x is not null).Take(Math.Max(0, count) + 1)!;

    public Playlist(IEnumerable<MediaItem> items, PlaybackOrder order, bool includeVideos)
    {
        _all = items.ToList();
        _order = order;
        _includeVideos = includeVideos;
        if (_order == PlaybackOrder.Filename)
            _all.Sort(static (left, right) => StringComparer.CurrentCultureIgnoreCase.Compare(left.Path, right.Path));
        RebuildUnseen();
    }

    public void AddItems(IEnumerable<MediaItem> items)
    {
        _all.AddRange(items);
        if (_order == PlaybackOrder.Filename)
            _all.Sort(static (left, right) => StringComparer.CurrentCultureIgnoreCase.Compare(left.Path, right.Path));
        RebuildUnseen();
    }

    public void SetOptions(PlaybackOrder order, bool includeVideos)
    {
        _order = order;
        _includeVideos = includeVideos;
        RebuildUnseen();
    }

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

    private List<MediaItem> Filtered() => _all.Where(x => _includeVideos || x.Kind == MediaKind.Image).ToList();

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
        return list;
    }
}
