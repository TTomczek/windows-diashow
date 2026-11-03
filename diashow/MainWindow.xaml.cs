using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using Forms = System.Windows.Forms;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfImage = System.Windows.Controls.Image;
using WpfBrushes = System.Windows.Media.Brushes;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace diashow;

public partial class MainWindow : Window
{
    private const int DiscoveryBatchSize = 500;
    private const double PreviewGap = 8;
    private const double PreviewItemWidth = 96;
    private const double PreviewItemHeight = 64;
    private const double PreviewSelectedWidth = 128;
    private const double PreviewSelectedHeight = 80;
    private const double VideoPreviewBucketSeconds = 1;
    private const int VideoPreviewCacheLimit = 32;
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly ImagePreloader _imagePreloader = new();
    private readonly VideoFrameExtractor _videoFrameExtractor = new();
    private readonly PlaylistManager _playlistManager = new(new FilePlaylistStore());
    private readonly ConcurrentDictionary<string, MediaItem> _discoveredItems =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _favoritePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _activePlaylistPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _controlsTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _videoProgressTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _videoPreviewTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _previewRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly UpdateManager? _updateManager;
    private Playlist? _playlist;
    private CancellationTokenSource? _playbackCancellation;
    private PlaybackAdvanceGate _playbackAdvanceGate = new();
    private CancellationTokenSource? _scanCancellation;
    private MediaFolderMonitor? _folderMonitor;
    private string? _requestedPath;
    private string? _rootFolder;
    private bool _paused;
    private bool _pausedByMinimize;
    private bool _currentVideoAudible;
    private bool _settingsUiReady;
    private string? _emptyMessageKey;
    private Point? _lastMousePosition;
    private DispatcherTimer? _gifTimer;
    private IReadOnlyList<BitmapSource>? _gifFrames;
    private IReadOnlyList<TimeSpan>? _gifDelays;
    private int _gifLoopCount;
    private int _gifCompletedLoops;
    private int _gifFrameIndex;
    private int _transitionVersion;
    private int _previewGeneration;
    private CancellationTokenSource? _previewThumbnailCancellation;
    private CancellationTokenSource? _videoPreviewCancellation;
    private CancellationTokenSource? _videoPreviewPrefetchCancellation;
    private string? _videoPreviewPath;
    private Point _videoPreviewPointer;
    private double _videoPreviewSeconds;
    private int _videoPreviewBucket = -1;
    private int _videoPreviewGeneration;
    private readonly Dictionary<int, BitmapSource> _videoPreviewFrames = [];
    private readonly List<(Animatable Target, DependencyProperty Property, AnimationClock Clock)>
        _kenBurnsAnimations = [];
    private readonly Dictionary<string, BitmapSource> _previewThumbnails =
        new(StringComparer.OrdinalIgnoreCase);
    private PlaylistIpcServer? _playlistIpcServer;
    private string? _activePlaylistId;
    private PlaylistEndMode _activePlaylistEndMode;
    private bool _updatingPlaylistUi;
    private bool _playlistWaitingForNext;

    public MainWindow(string[] args, UpdateManager? updateManager = null)
    {
        InitializeComponent();
        _updateManager = updateManager;
        _playlistIpcServer = new PlaylistIpcServer(_playlistManager, () => _discoveredItems.Values);
        _requestedPath = args.FirstOrDefault();
        _controlsTimer.Tick += (_, _) =>
        {
            if (VideoPreviewPopup.Visibility == Visibility.Visible &&
                (VideoProgress.IsMouseOver || VideoProgress.IsKeyboardFocusWithin))
            {
                _controlsTimer.Start();
                return;
            }

            Controls.Opacity = 0;
            Controls.IsEnabled = false;
            QueuePreviewHost.Opacity = 0;
            QueuePreviewHost.IsEnabled = false;
            Cursor = Cursors.None;
        };
        _videoProgressTimer.Tick += (_, _) => UpdateVideoProgress();
        _videoPreviewTimer.Tick += async (_, _) =>
        {
            _videoPreviewTimer.Stop();
            await GenerateVideoPreviewAsync();
        };
        _previewRefreshTimer.Tick += (_, _) =>
        {
            _previewRefreshTimer.Stop();
            UpdateQueuePreview();
        };
        VideoView.Volume = 0;
        Localization.SetLanguage(_settings.Language);
        ApplyLocalization();
        ApplySettingsToUi();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _lastMousePosition = Mouse.GetPosition(this);
        _controlsTimer.Start();
        if (_settings.StartFullscreen) SetFullscreen(true);
        var input = ResolveInput(_requestedPath);
        if (input is null)
        {
            ShowMessage("ChooseFolderMessage");
            return;
        }
        _ = LoadFolderAsync(input.Value.Folder, input.Value.StartFile);
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            if (!_paused)
            {
                _pausedByMinimize = true;
                TogglePause();
            }
        }
        else if (_pausedByMinimize)
        {
            _pausedByMinimize = false;
            TogglePause();
        }
    }

    private static (string Folder, string? StartFile)? ResolveInput(string? argument)
    {
        if (!string.IsNullOrWhiteSpace(argument))
        {
            if (Directory.Exists(argument)) return (argument, null);
            if (File.Exists(argument)) return (Path.GetDirectoryName(argument)!, argument);
        }
        var settings = AppSettings.Load();
        return settings.LastFolder is string lastFolder && Directory.Exists(lastFolder)
            ? (lastFolder, null) : null;
    }

    private async Task LoadFolderAsync(string folder, string? startFile)
    {
        _scanCancellation?.Cancel();
        _playbackCancellation?.Cancel();
        var scanCancellation = new CancellationTokenSource();
        _scanCancellation = scanCancellation;
        _settings.LastFolder = folder;
        _rootFolder = folder;
        _settings.Save();
        _activePlaylistId = null;
        _playlistWaitingForNext = false;
        _activePlaylistPaths.Clear();
        _favoritePaths.Clear();
        _discoveredItems.Clear();
        _folderMonitor?.Dispose();
        _folderMonitor = new MediaFolderMonitor(folder);
        _folderMonitor.MediaAdded += FolderMonitor_MediaAdded;
        _folderMonitor.MediaRemoved += FolderMonitor_MediaRemoved;
        _imagePreloader.Clear();
        _previewThumbnails.Clear();
        _previewGeneration++;
        var playlist = new Playlist([], _settings.Order, _settings.MediaFilter, _settings.Direction);
        _playlist = playlist;
        await Task.Run(() => _playlistManager.LoadFolder(folder)).ConfigureAwait(true);
        if (scanCancellation.IsCancellationRequested)
            return;
        await RefreshPlaylistPanelAsync();
        UpdateQueuePreview();
        ImageView.Source = null;
        StopVideoProgress();
        VideoView.Stop();
        VideoView.Source = null;
        var reader = MediaDiscovery.Stream(folder, scanCancellation.Token, startFile is null);
        var pending = new List<MediaItem>();
        var started = false;

        await foreach (var item in reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (scanCancellation.IsCancellationRequested)
                return;
            pending.Add(item);
            _discoveredItems[item.Path] = item;
            var isRequestedItem = startFile is not null &&
                string.Equals(item.Path, startFile, StringComparison.OrdinalIgnoreCase);
            if (!started && (startFile is null || isRequestedItem))
            {
                var initialItems = pending.ToArray();
                pending.Clear();
                var matches = await GetPlaylistMatchesAsync(initialItems);
                var first = await Dispatcher.InvokeAsync(() =>
                {
                    if (_playlist != playlist || scanCancellation.IsCancellationRequested)
                        return null;

                    playlist.AddItems(initialItems);
                    ApplyPlaylistMatches(playlist, matches);
                    UpdateItemCounter();
                    return startFile is null ? playlist.StartAt(item.Path) : playlist.StartAt(startFile);
                });
                if (first is not null)
                {
                    started = true;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        EmptyState.Visibility = Visibility.Collapsed;
                        ShowItem(first);
                    });
                }
            }
            else if (started && pending.Count >= DiscoveryBatchSize)
            {
                var batch = pending.ToArray();
                pending.Clear();
                var matches = await GetPlaylistMatchesAsync(batch);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_playlist != playlist || scanCancellation.IsCancellationRequested)
                        return;

                    playlist.AddItems(batch);
                    ApplyPlaylistMatches(playlist, matches);
                    UpdateItemCounter();
                    RequestQueuePreviewUpdate();
                });
            }
        }

        if (scanCancellation.IsCancellationRequested)
            return;
        if (pending.Count > 0)
        {
            var batch = pending.ToArray();
            var matches = await GetPlaylistMatchesAsync(batch);
            await Dispatcher.InvokeAsync(() =>
            {
                if (_playlist != playlist || scanCancellation.IsCancellationRequested)
                    return;

                playlist.AddItems(batch);
                ApplyPlaylistMatches(playlist, matches);
                UpdateItemCounter();
                RequestQueuePreviewUpdate();
            });
        }
        if (!started)
        {
            var (first, activePlaylist) = await Dispatcher.InvokeAsync(() =>
            {
                if (_playlist != playlist || scanCancellation.IsCancellationRequested)
                    return ((MediaItem?)null, _activePlaylistId is not null);

                var active = _activePlaylistId is not null;
                var item = active
                    ? playlist.Current is null ? playlist.Next() : null
                    : startFile is null ? playlist.StartRandom() : playlist.StartAt(startFile);
                if (active)
                    _playlistWaitingForNext = item is null;
                return (item, active);
            });
            if (first is not null)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    EmptyState.Visibility = Visibility.Collapsed;
                    ShowItem(first);
                });
            }
            else if (!activePlaylist)
                await Dispatcher.InvokeAsync(() => ShowMessage("NoMediaMessage"));
        }
        if (!scanCancellation.IsCancellationRequested)
            await Task.Run(() => _playlistManager.CompleteScan(folder));
        if (!scanCancellation.IsCancellationRequested)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (_playlist != playlist || _activePlaylistId is null)
                    return;
                playlist.SetActivePlaylist(
                    _activePlaylistPaths,
                    _activePlaylistEndMode,
                    resetHistory: false,
                    scanComplete: true);
                if (_playlistWaitingForNext && playlist.Next() is { } next)
                {
                    _playlistWaitingForNext = false;
                    EmptyState.Visibility = Visibility.Collapsed;
                    ShowItem(next);
                }
            });
        }
        await RefreshPlaylistPanelAsync();
    }

    private void FolderMonitor_MediaAdded(MediaItem item) => _ = AddDiscoveredMediaAsync(item);

    private async Task AddDiscoveredMediaAsync(MediaItem item)
    {
        if (!File.Exists(item.Path))
            return;
        var matches = await GetPlaylistMatchesAsync([item]);
        await Dispatcher.InvokeAsync(() =>
        {
            if (_playlist is null || _rootFolder is null)
                return;
            if (!PlaylistPathIdentity.TryGetRelativePath(_rootFolder, item.Path, out _))
                return;
            _discoveredItems[item.Path] = item;
            _playlist.AddItems([item]);
            ApplyPlaylistMatches(_playlist, matches);
            UpdateItemCounter();
            UpdateQueuePreview();
            PreloadUpcoming();
            UpdateFavoriteToggle(_playlist.Current);
            _ = RefreshPlaylistPanelAsync();
        });
    }

    private void FolderMonitor_MediaRemoved(string path) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (_playlist is null)
                return;
            if (_rootFolder is null ||
                !PlaylistPathIdentity.TryGetRelativePath(_rootFolder, path, out _))
                return;
            _discoveredItems.TryRemove(path, out _);
            _favoritePaths.Remove(path);
            _activePlaylistPaths.Remove(path);
            if (_activePlaylistId is not null)
                _playlist.SetActivePlaylist(
                    _activePlaylistPaths,
                    _activePlaylistEndMode,
                    resetHistory: false,
                    scanComplete: _playlistManager.IsScanComplete);
            var currentRemoved = _playlist.RemoveItems([path]);
            _imagePreloader.Clear();
            UpdateItemCounter();
            UpdateQueuePreview();
            UpdateFavoriteToggle(_playlist.Current);
            _ = RefreshPlaylistPanelAsync();
            if (currentRemoved)
                GoNext();
        });

    private async Task<(HashSet<string> Favorites, string? ActiveId, HashSet<string> ActivePaths,
        PlaylistEndMode EndMode, string? Root)> GetPlaylistMatchesAsync(IEnumerable<MediaItem> items)
    {
        var batch = items.ToArray();
        var active = await Dispatcher.InvokeAsync(() =>
            (_activePlaylistId, _activePlaylistEndMode, _rootFolder));
        if (active._rootFolder is null)
            return ([], null, [], active._activePlaylistEndMode, null);

        var favorites = await Task.Run(() =>
            _playlistManager.GetPlayablePaths(active._rootFolder, PlaylistManager.FavoriteId, batch));
        var activePaths = active._activePlaylistId is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : await Task.Run(() =>
                _playlistManager.GetPlayablePaths(active._rootFolder, active._activePlaylistId, batch));
        return (favorites, active._activePlaylistId, activePaths, active._activePlaylistEndMode,
            active._rootFolder);
    }

    private void ApplyPlaylistMatches(
        Playlist playlist,
        (HashSet<string> Favorites, string? ActiveId, HashSet<string> ActivePaths,
            PlaylistEndMode EndMode, string? Root) matches)
    {
        if (matches.Root is null || !string.Equals(matches.Root, _rootFolder, StringComparison.OrdinalIgnoreCase))
            return;
        _favoritePaths.UnionWith(matches.Favorites);
        if (matches.ActiveId is not null && matches.ActiveId == _activePlaylistId)
        {
            _activePlaylistPaths.UnionWith(matches.ActivePaths);
            _activePlaylistEndMode = matches.EndMode;
            playlist.SetActivePlaylist(
                _activePlaylistPaths,
                _activePlaylistEndMode,
                resetHistory: false,
                scanComplete: _playlistManager.IsScanComplete);
            if (_playlistWaitingForNext && playlist.Next() is { } first)
            {
                _playlistWaitingForNext = false;
                EmptyState.Visibility = Visibility.Collapsed;
                ShowItem(first);
            }
        }
        UpdateFavoriteToggle(playlist.Current);
    }

    private async Task RefreshPlaylistPanelAsync()
    {
        var root = _playlistManager.CurrentFolder;
        if (root is null)
            return;
        var views = await Task.Run(() =>
        {
            var items = _discoveredItems.Values.ToArray();
            return _playlistManager.List(root, items);
        });
        if (!string.Equals(root, _playlistManager.CurrentFolder, StringComparison.OrdinalIgnoreCase))
            return;

        _updatingPlaylistUi = true;
        try
        {
            var selectedId = _activePlaylistId ?? (PlaylistList.SelectedItem as PlaylistView)?.Id;
            PlaylistList.ItemsSource = views;
            PlaylistList.SelectedValue = views.Any(view => view.Id == selectedId)
                ? selectedId
                : PlaylistManager.FavoriteId;
            PlaylistList.SetValue(AutomationProperties.NameProperty, Localization.Get("Playlists"));
            PlaylistNameBox.SetValue(AutomationProperties.NameProperty, Localization.Get("PlaylistName"));
            PlaylistEndModeBox.SetValue(
                AutomationProperties.NameProperty, Localization.Get("PlaylistEndMode"));
            ApiCapabilityBox.SetValue(AutomationProperties.NameProperty, Localization.Get("ApiAccessKey"));
            UpdatePlaylistSelectionUi();
            var active = views.FirstOrDefault(view => view.Id == _activePlaylistId);
            _activePlaylistName = active?.Name;
            UpdateActivePlaylistIndicator();
        }
        finally
        {
            _updatingPlaylistUi = false;
        }
    }

    private string? _activePlaylistName;

    private PlaylistView? SelectedPlaylist => PlaylistList.SelectedItem as PlaylistView;

    private void UpdatePlaylistSelectionUi()
    {
        var selected = SelectedPlaylist;
        PlaylistNameBox.Text = selected is { IsFavorite: false } ? selected.Name : string.Empty;
        RenamePlaylistButton.IsEnabled = selected is { IsFavorite: false };
        DeletePlaylistButton.IsEnabled = selected is { IsFavorite: false };
        CleanupStaleEntriesButton.IsEnabled = selected is not null && _playlistManager.IsScanComplete;
        ActivatePlaylistButton.IsEnabled = selected is not null;
        PlaylistEndModeBox.IsEnabled = selected is { IsFavorite: false };
        SharePlaylistBox.IsEnabled = selected is { IsFavorite: false };
        PlaylistEndModeBox.SelectedIndex = selected?.EndMode == PlaylistEndMode.AppendNonPlaylistItems ? 1 : 0;
        SharePlaylistBox.IsChecked = selected?.SharedWithClients == true;
    }

    private void UpdateActivePlaylistIndicator()
    {
        ActivePlaylistIndicator.Text = _activePlaylistId is null || _activePlaylistName is null
            ? string.Empty
            : Localization.Get("ActivePlaylist", _activePlaylistName);
        ActivePlaylistIndicator.Visibility = _activePlaylistId is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        AutomationProperties.SetName(
            ActivePlaylistIndicator,
            _activePlaylistId is null || _activePlaylistName is null
                ? string.Empty
                : Localization.Get("ActivePlaylist", _activePlaylistName));
    }

    private void UpdateFavoriteToggle(MediaItem? item)
    {
        FavoriteToggle.IsEnabled = item is not null;
        var isFavorite = item is not null && _favoritePaths.Contains(item.Path);
        FavoriteToggle.IsChecked = isFavorite;
        var name = Localization.Get(isFavorite ? "FavoriteOnAutomation" : "FavoriteOffAutomation");
        FavoriteToggle.ToolTip = name;
        AutomationProperties.SetName(FavoriteToggle, name);
    }

    private async Task ActivatePlaylistAsync(PlaylistView? selected)
    {
        if (selected is null || _playlist is null || _rootFolder is null)
            return;

        var root = _rootFolder;
        var paths = await Task.Run(() =>
            _playlistManager.GetPlayablePaths(root, selected.Id, _discoveredItems.Values));
        if (_playlist is null || !string.Equals(root, _rootFolder, StringComparison.OrdinalIgnoreCase))
            return;

        _activePlaylistId = selected.Id;
        _activePlaylistName = selected.Name;
        _activePlaylistEndMode = selected.EndMode;
        _playlistWaitingForNext = false;
        _activePlaylistPaths.Clear();
        _activePlaylistPaths.UnionWith(paths);
        _playlist.SetActivePlaylist(
            _activePlaylistPaths,
            _activePlaylistEndMode,
            scanComplete: _playlistManager.IsScanComplete);
        UpdateActivePlaylistIndicator();
        UpdateItemCounter();
        UpdateQueuePreview();
        if (_playlist.Next() is { } first)
        {
            _playlistWaitingForNext = false;
            EmptyState.Visibility = Visibility.Collapsed;
            ShowItem(first);
        }
        else
        {
            _playlistWaitingForNext = true;
            ShowMessage("PlaylistEmpty");
        }
        PreloadUpcoming();
    }

    private async void NewPlaylist_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = PlaylistNameBox.Text;
            var mode = SelectedPlaylistEndMode();
            var id = await Task.Run(() => _playlistManager.Create(name, mode));
            await RefreshPlaylistPanelAsync();
            PlaylistList.SelectedValue = id;
        }
        catch (PlaylistRequestException)
        {
            ApiCapabilityStatus.Text = Localization.Get("PlaylistOperationFailed");
        }
    }

    private async void PreviewFavoriteToggle_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is ToggleButton { Tag: MediaItem item, IsChecked: { } isFavorite } &&
            _rootFolder is { } root)
            await SetFavoriteAsync(root, item, isFavorite);
    }

    private async void RenamePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPlaylist is not { IsFavorite: false } selected)
            return;
        try
        {
            await Task.Run(() => _playlistManager.Rename(selected.Id, PlaylistNameBox.Text));
            await RefreshPlaylistPanelAsync();
        }
        catch (PlaylistRequestException)
        {
            ApiCapabilityStatus.Text = Localization.Get("PlaylistOperationFailed");
        }
    }

    private async void DeletePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPlaylist is not { IsFavorite: false } selected)
            return;
        try
        {
            await Task.Run(() => _playlistManager.Delete(selected.Id));
            if (_activePlaylistId == selected.Id)
            {
                _activePlaylistId = null;
                _playlistWaitingForNext = false;
                _activePlaylistName = null;
                _activePlaylistPaths.Clear();
                _playlist?.ClearActivePlaylist();
                UpdateActivePlaylistIndicator();
                if (_playlist?.Next() is { } first)
                    ShowItem(first);
            }
            await RefreshPlaylistPanelAsync();
        }
        catch (PlaylistRequestException)
        {
            ApiCapabilityStatus.Text = Localization.Get("PlaylistOperationFailed");
        }
    }

    private async void CleanupStaleEntries_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPlaylist is not { } selected)
            return;
        try
        {
            var items = _discoveredItems.Values.ToArray();
            await Task.Run(() => _playlistManager.CleanupStale(selected.Id, items));
            if (_activePlaylistId == selected.Id && _rootFolder is { } root)
            {
                var paths = await Task.Run(() =>
                    _playlistManager.GetPlayablePaths(root, selected.Id, items));
                _activePlaylistPaths.Clear();
                _activePlaylistPaths.UnionWith(paths);
                _playlist?.SetActivePlaylist(
                    _activePlaylistPaths,
                    _activePlaylistEndMode,
                    resetHistory: false,
                    scanComplete: _playlistManager.IsScanComplete);
                if (_playlist?.Current is { } current && !_activePlaylistPaths.Contains(current.Path))
                    GoNext();
            }
            await RefreshPlaylistPanelAsync();
        }
        catch (PlaylistRequestException)
        {
            ApiCapabilityStatus.Text = Localization.Get("PlaylistOperationFailed");
        }
    }

    private async void ActivatePlaylist_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ActivatePlaylistAsync(SelectedPlaylist);
        }
        catch (PlaylistRequestException)
        {
            ApiCapabilityStatus.Text = Localization.Get("PlaylistOperationFailed");
        }
    }

    private async void PlaylistEndModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingPlaylistUi || SelectedPlaylist is not { IsFavorite: false } selected)
            return;
        var mode = SelectedPlaylistEndMode();
        try
        {
            await Task.Run(() => _playlistManager.SetEndMode(selected.Id, mode));
            if (_activePlaylistId == selected.Id)
            {
                _activePlaylistEndMode = mode;
                _playlist?.SetActivePlaylist(
                    _activePlaylistPaths,
                    mode,
                    resetHistory: false,
                    scanComplete: _playlistManager.IsScanComplete);
            }
            await RefreshPlaylistPanelAsync();
        }
        catch (PlaylistRequestException)
        {
            ApiCapabilityStatus.Text = Localization.Get("PlaylistOperationFailed");
        }
    }

    private async void SharePlaylistBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingPlaylistUi || SelectedPlaylist is not { IsFavorite: false } selected)
            return;
        try
        {
            var shared = SharePlaylistBox.IsChecked == true;
            await Task.Run(() => _playlistManager.SetShared(selected.Id, shared));
            await RefreshPlaylistPanelAsync();
        }
        catch (PlaylistRequestException)
        {
            ApiCapabilityStatus.Text = Localization.Get("PlaylistOperationFailed");
        }
    }

    private async void FavoriteToggle_Click(object sender, RoutedEventArgs e) =>
        await SetCurrentFavoriteAsync(FavoriteToggle.IsChecked == true);

    private void ToggleFavorite()
    {
        if (_playlist?.Current is not { } current)
            return;
        var target = !_favoritePaths.Contains(current.Path);
        FavoriteToggle.IsChecked = target;
        _ = SetCurrentFavoriteAsync(target);
    }

    private async Task SetCurrentFavoriteAsync(bool isFavorite)
    {
        if (_playlist?.Current is not { } current || _rootFolder is not { } root)
            return;
        await SetFavoriteAsync(root, current, isFavorite);
    }

    private async Task SetFavoriteAsync(string root, MediaItem item, bool isFavorite)
    {
        try
        {
            await Task.Run(() => _playlistManager.SetFavorite(root, item.Path, isFavorite));
            if (!string.Equals(root, _rootFolder, StringComparison.OrdinalIgnoreCase) ||
                _playlist is not { } currentPlaylist)
                return;
            if (isFavorite)
                _favoritePaths.Add(item.Path);
            else
                _favoritePaths.Remove(item.Path);
            if (_activePlaylistId == PlaylistManager.FavoriteId)
            {
                if (isFavorite)
                    _activePlaylistPaths.Add(item.Path);
                else
                    _activePlaylistPaths.Remove(item.Path);
                currentPlaylist.SetActivePlaylist(
                    _activePlaylistPaths,
                    _activePlaylistEndMode,
                    resetHistory: false,
                    scanComplete: _playlistManager.IsScanComplete);
                if (!isFavorite && currentPlaylist.Current?.Path == item.Path)
                    GoNext();
                else if (_playlistWaitingForNext && currentPlaylist.Next() is { } next)
                {
                    _playlistWaitingForNext = false;
                    ShowItem(next);
                }
            }
            UpdateFavoriteToggle(currentPlaylist.Current);
            UpdateQueuePreview();
            await RefreshPlaylistPanelAsync();
        }
        catch (PlaylistRequestException)
        {
            UpdateFavoriteToggle(_playlist?.Current);
            ApiCapabilityStatus.Text = Localization.Get("PlaylistOperationFailed");
        }
    }

    private PlaylistEndMode SelectedPlaylistEndMode() =>
        PlaylistEndModeBox.SelectedIndex == 1
            ? PlaylistEndMode.AppendNonPlaylistItems
            : PlaylistEndMode.LoopPlaylist;

    private void PlaylistPanelButton_Click(object sender, RoutedEventArgs e)
    {
        PlaylistPanel.Visibility = PlaylistPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
        SettingsPanel.Visibility = Visibility.Collapsed;
        ShowControls();
    }

    private void ClosePlaylistPanel_Click(object sender, RoutedEventArgs e) =>
        PlaylistPanel.Visibility = Visibility.Collapsed;

    private void PlaylistList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingPlaylistUi)
            return;
        _updatingPlaylistUi = true;
        try
        {
            UpdatePlaylistSelectionUi();
        }
        finally
        {
            _updatingPlaylistUi = false;
        }
    }

    private void GenerateApiCapability_Click(object sender, RoutedEventArgs e)
    {
        if (_playlistIpcServer is null)
            return;
        try
        {
            var issued = _playlistIpcServer.IssueCapability();
            ApiCapabilityBox.Text = issued.Capability;
            ApiCapabilityBox.Visibility = Visibility.Visible;
            ApiCapabilityBox.SetValue(AutomationProperties.NameProperty, Localization.Get("ApiAccessKey"));
            try
            {
                System.Windows.Clipboard.SetText(issued.Capability);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
            }
            ApiCapabilityStatus.Text = Localization.Get("ApiCapabilityIssued");
        }
        catch (PlaylistRequestException)
        {
            ApiCapabilityStatus.Text = Localization.Get("PlaylistOperationFailed");
        }
    }

    private void RevokeApiCapabilities_Click(object sender, RoutedEventArgs e)
    {
        _playlistIpcServer?.RevokeCapabilities();
        ApiCapabilityBox.Clear();
        ApiCapabilityBox.Visibility = Visibility.Collapsed;
        ApiCapabilityStatus.Text = Localization.Get("ApiCapabilitiesRevoked");
    }

    private static bool IsEditableControlFocused() =>
        Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox ||
        Keyboard.FocusedElement is System.Windows.Controls.ComboBox { IsEditable: true };

    private async void ShowItem(MediaItem item, bool preservePlaybackState = false)
    {
        var transitionVersion = ++_transitionVersion;
        _playbackCancellation?.Cancel();
        StopGif();
        _playbackCancellation = new CancellationTokenSource();
        var preservePause = preservePlaybackState && _paused;
        _playbackAdvanceGate = new PlaybackAdvanceGate(preservePause);
        _paused = preservePause;
        SetButtonIcon(PauseButton, _paused ? "\uE768" : "\uE769",
            Localization.Get(_paused ? "Resume" : "Pause"));
        _currentVideoAudible = false;
        SetButtonIcon(AudioButton, "\uE74F", Localization.Get("Unmute"));
        var relativePath = _rootFolder is null ? item.Path : Path.GetRelativePath(_rootFolder, item.Path);
        PathBanner.Text = relativePath.Replace('\\', '/').Replace("/", " / ");
        var mediaName = Localization.Get("MediaName",
            Localization.Get(item.Kind == MediaKind.Image ? "Image" : "Video"), Path.GetFileName(item.Path));
        AutomationProperties.SetName(ImageView, mediaName);
        AutomationProperties.SetName(VideoView, mediaName);
        UpdateItemCounter();
        UpdateQueuePreview();
        if (item.Kind == MediaKind.Image)
        {
            VideoView.Stop();
            VideoView.Visibility = Visibility.Collapsed;
            StopVideoProgress();
            try
            {
                if (string.Equals(Path.GetExtension(item.Path), ".gif", StringComparison.OrdinalIgnoreCase))
                {
                    var animation = await Task.Run(() => DecodeGif(item.Path));
                    if (_playbackCancellation.IsCancellationRequested)
                        return;
                    if (animation.Frames.Count == 0)
                        throw new NotSupportedException();
                    _gifFrames = animation.Frames;
                    _gifDelays = animation.Delays;
                    _gifLoopCount = animation.LoopCount;
                    _gifCompletedLoops = 0;
                    _gifFrameIndex = 0;
                    ShowImage(animation.Frames[0], transitionVersion);
                    StartGifTimer();
                    return;
                }

                var image = await _imagePreloader.GetAsync(item.Path, _playbackCancellation.Token);
                if (_playbackCancellation.IsCancellationRequested)
                    return;
                ShowImage(image, transitionVersion);
                _ = WaitThenNext(_playbackCancellation.Token);
                PreloadUpcoming();
            }

            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                or ArgumentException or InvalidOperationException or FileFormatException or InvalidDataException)
            {
                ShowAccessToast(item.Path, ex);
                GoNext();
            }
            catch (OperationCanceledException)
            {
            }
        }
        else
        {
            ImageView.Visibility = Visibility.Collapsed;
            ResetImageTransition();
            VideoView.Visibility = Visibility.Visible;
            StopVideoProgress();
            VideoView.Source = new Uri(item.Path);
            VideoView.Volume = 0;
            VideoView.Play();
            if (preservePause)
                VideoView.Pause();
            _videoProgressTimer.Start();
            AnimateTransition(VideoView);
            PreloadUpcoming();
        }
        if (!preservePause)
        {
            SetButtonIcon(PauseButton, "\uE769", Localization.Get("Pause"));
            _paused = false;
        }
        RequestQueuePreviewUpdate();
    }

    private sealed record GifAnimation(
        IReadOnlyList<BitmapSource> Frames,
        IReadOnlyList<TimeSpan> Delays,
        int LoopCount);

    private static GifAnimation DecodeGif(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var sourceFrames = decoder.Frames;
        if (sourceFrames.Count == 0)
            return new GifAnimation([], [], 0);

        var width = ReadGifMetadata(sourceFrames[0], "/logscrdesc/Width");
        var height = ReadGifMetadata(sourceFrames[0], "/logscrdesc/Height");
        if (width == 0) width = sourceFrames.Max(frame => frame.PixelWidth);
        if (height == 0) height = sourceFrames.Max(frame => frame.PixelHeight);

        var canvas = new byte[width * height * 4];
        var result = new List<BitmapSource>(sourceFrames.Count);
        var delays = new List<TimeSpan>(sourceFrames.Count);
        var loopCount = ReadGifMetadata(sourceFrames[0], "/appext/data/"); 

        foreach (var frame in sourceFrames)
        {
            var left = ReadGifMetadata(frame, "/imgdesc/Left");
            var top = ReadGifMetadata(frame, "/imgdesc/Top");
            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[frame.PixelWidth * frame.PixelHeight * 4];
            converted.CopyPixels(pixels, frame.PixelWidth * 4, 0);
            var before = (byte[])canvas.Clone();

            for (var y = 0; y < frame.PixelHeight; y++)
            {
                for (var x = 0; x < frame.PixelWidth; x++)
                {
                    var destinationX = left + x;
                    var destinationY = top + y;
                    if (destinationX < 0 || destinationX >= width || destinationY < 0 || destinationY >= height)
                        continue;
                    var sourceIndex = (y * frame.PixelWidth + x) * 4;
                    if (pixels[sourceIndex + 3] == 0)
                        continue;
                    var destinationIndex = (destinationY * width + destinationX) * 4;
                    Buffer.BlockCopy(pixels, sourceIndex, canvas, destinationIndex, 4);
                }
            }

            var composed = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            composed.WritePixels(new Int32Rect(0, 0, width, height), canvas, width * 4, 0);
            composed.Freeze();
            result.Add(composed);
            var delay = ReadGifMetadata(frame, "/grctlext/Delay");
            delays.Add(TimeSpan.FromMilliseconds(Math.Max(10, delay * 10)));

            switch (ReadGifMetadata(frame, "/grctlext/Disposal"))
            {
                case 2:
                    ClearRect(canvas, width, height, left, top, frame.PixelWidth, frame.PixelHeight);
                    break;
                case 3:
                    canvas = before;
                    break;
            }
        }

        return new GifAnimation(result, delays, loopCount);
    }

    private static int ReadGifMetadata(BitmapFrame frame, string query)
    {
        if (frame.Metadata is BitmapMetadata metadata &&
            metadata.ContainsQuery(query))
        {
            var value = metadata.GetQuery(query);
            return value switch
            {
                byte number => number,
                ushort number => number,
                short number => number,
                uint number => (int)number,
                _ => 0
            };
        }
        return 0;
    }

    private static void ClearRect(byte[] canvas, int width, int height, int left, int top, int rectWidth, int rectHeight)
    {
        for (var y = Math.Max(0, top); y < Math.Min(height, top + rectHeight); y++)
        {
            var start = (y * width + Math.Max(0, left)) * 4;
            var length = (Math.Min(width, left + rectWidth) - Math.Max(0, left)) * 4;
            if (length > 0)
                Array.Clear(canvas, start, length);
        }
    }

    private void StartGifTimer()
    {
        _gifTimer = new DispatcherTimer { Interval = _gifDelays?[0] ?? TimeSpan.FromMilliseconds(100) };
        _gifTimer.Tick += GifTimer_Tick;
        _gifTimer.Start();
    }

    private void GifTimer_Tick(object? sender, EventArgs e)
    {
        if (_paused || _gifFrames is null || _gifFrames.Count == 0)
            return;
        _gifFrameIndex++;
        if (_gifFrameIndex >= _gifFrames.Count)
        {
            _gifFrameIndex = 0;
            _gifCompletedLoops++;
            var requiredLoops = _gifLoopCount == 0 ? 1 : _gifLoopCount + 1;
            if (_gifCompletedLoops >= requiredLoops)
            {
                GoNext();
                return;
            }
        }
        ImageView.Source = _gifFrames[_gifFrameIndex];
        if (_gifTimer is not null)
            _gifTimer.Interval = _gifDelays?[_gifFrameIndex] ?? TimeSpan.FromMilliseconds(100);
    }

    private void StopGif()
    {
        if (_gifTimer is not null)
        {
            _gifTimer.Stop();
            _gifTimer.Tick -= GifTimer_Tick;
            _gifTimer = null;
        }
        _gifFrames = null;
        _gifDelays = null;
        _gifLoopCount = 0;
        _gifCompletedLoops = 0;
        _gifFrameIndex = 0;
    }

    private void PreloadUpcoming()
    {
        if (_settings.PreloadEnabled && _playlist is not null)
            _imagePreloader.Preload(_playlist.PreloadCandidates(_settings.PreloadCount));
    }

    private void QueuePreviewPanel_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateQueuePreview();

    private void RequestQueuePreviewUpdate()
    {
        _previewRefreshTimer.Stop();
        _previewRefreshTimer.Start();
    }

    private void Controls_SizeChanged(object sender, SizeChangedEventArgs e) =>
        QueuePreviewHost.Margin = new Thickness(0, 0, 0, e.NewSize.Height);

    private void UpdateQueuePreview()
    {
        _previewThumbnailCancellation?.Cancel();
        _previewThumbnailCancellation?.Dispose();
        _previewThumbnailCancellation = new CancellationTokenSource();
        var cancellationToken = _previewThumbnailCancellation.Token;
        QueuePreviewItems.Children.Clear();
        var generation = ++_previewGeneration;
        UpdateQueueToggleButton();
        UpdateActivePlaylistIndicator();
        UpdateFavoriteToggle(_playlist?.Current);
        if (!_settings.QueuePreviewVisible)
        {
            QueuePreviewPanel.Visibility = Visibility.Collapsed;
            QueueToggleButton.IsEnabled = _playlist?.TotalCount >= 2;
            QueuePreviousButton.IsEnabled = false;
            QueueNextButton.IsEnabled = false;
            return;
        }

        QueuePreviewPanel.Visibility = Visibility.Visible;
        if (_playlist?.Current is not { } current || _playlist.TotalCount < 2)
        {
            QueuePreviewPanel.Visibility = Visibility.Collapsed;
            QueueToggleButton.IsEnabled = false;
            QueuePreviousButton.IsEnabled = false;
            QueueNextButton.IsEnabled = false;
            return;
        }

        QueueToggleButton.IsEnabled = true;
        var width = QueuePreviewPanel.ActualWidth > 0
            ? QueuePreviewPanel.ActualWidth
            : (Root.ActualWidth > 0 ? Root.ActualWidth : ActualWidth);
        var available = Math.Max(0, width - 112);
        var pairsThatFit = (int)Math.Floor(
            (available - PreviewSelectedWidth) / (2 * (PreviewItemWidth + PreviewGap)));
        var previous = _playlist.PreviewPreviousItems(Math.Max(0, pairsThatFit));
        var upcoming = _playlist.PreviewUpcomingItems(Math.Max(0, pairsThatFit));

        QueueToggleButton.IsEnabled = previous.Count > 0 || upcoming.Count > 0;
        var position = _playlist.CurrentPosition;
        if (upcoming.Count > previous.Count)
            AddPreviewSpacer((upcoming.Count - previous.Count) * (PreviewItemWidth + PreviewGap));

        for (var index = previous.Count - 1; index >= 0; index--)
        {
            var item = previous[index];
            AddPreviewButton(item, selected: false, position - index - 1, generation, cancellationToken);
        }

        AddPreviewButton(current, selected: true, position, generation, cancellationToken);

        for (var index = 0; index < upcoming.Count; index++)
            AddPreviewButton(upcoming[index], selected: false, position + index + 1, generation,
                cancellationToken);

        if (previous.Count > upcoming.Count)
            AddPreviewSpacer((previous.Count - upcoming.Count) * (PreviewItemWidth + PreviewGap));

        if (QueuePreviewItems.Children[^1] is Button lastButton)
            lastButton.Margin = new Thickness(0);
        QueuePreviousButton.IsEnabled = previous.Count > 0 && _playlist.CanGoBack;
        QueueNextButton.IsEnabled = upcoming.Count > 0;
    }

    private void AddPreviewSpacer(double width) =>
        QueuePreviewItems.Children.Add(new Border { Width = width, IsHitTestVisible = false });

    private void AddPreviewButton(
        MediaItem item,
        bool selected,
        int position,
        int generation,
        CancellationToken cancellationToken)
    {
        var image = new WpfImage
        {
            Stretch = Stretch.Uniform,
            Source = null,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        var fallback = new TextBlock
        {
            Text = item.Kind == MediaKind.Video ? "\uE768" : "\uE91B",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = selected ? 25 : 20,
            Foreground = WpfBrushes.White,
            Opacity = 0.8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        var content = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x2A, 0x32))
        };
        content.Children.Add(image);
        content.Children.Add(fallback);
        if (item.Kind == MediaKind.Video)
            content.Children.Add(new TextBlock
            {
                Text = "\uE768",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = selected ? 25 : 20,
                Foreground = WpfBrushes.White,
                Opacity = 0.8,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            });
        var favoriteToggle = new ToggleButton
        {
            Content = "\uE734",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            Foreground = WpfBrushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x18, 0x18, 0x20)),
            BorderThickness = new Thickness(0),
            Width = 24,
            Height = 24,
            IsChecked = _favoritePaths.Contains(item.Path),
            Tag = item,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Focusable = true
        };
        AutomationProperties.SetName(
            favoriteToggle,
            Localization.Get(_favoritePaths.Contains(item.Path)
                ? "FavoriteOnAutomation"
                : "FavoriteOffAutomation"));
        favoriteToggle.Click += PreviewFavoriteToggle_Click;
        content.Children.Add(favoriteToggle);

        var baseBorderBrush = selected
            ? WpfBrushes.White
            : new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF));
        var previewBorder = new Border
        {
            Background = WpfBrushes.Black,
            BorderBrush = baseBorderBrush,
            BorderThickness = new Thickness(selected ? 2 : 1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = content
        };
        var button = new Button
        {
            Style = (Style)Root.FindResource("PreviewButton"),
            Width = selected ? PreviewSelectedWidth : PreviewItemWidth,
            Height = selected ? PreviewSelectedHeight : PreviewItemHeight,
            Margin = new Thickness(0, 0, PreviewGap, 0),
            Padding = new Thickness(0),
            Content = previewBorder,
            Background = WpfBrushes.Transparent,
            BorderBrush = WpfBrushes.Transparent,
            BorderThickness = new Thickness(0),
            Focusable = true,
            Tag = item
        };
        var filename = Path.GetFileName(item.Path);
        AutomationProperties.SetName(button, Localization.Get("QueuePosition", Math.Max(1, position), filename));
        AutomationProperties.SetHelpText(button, filename);
        button.Click += QueuePreviewItem_Click;
        button.MouseEnter += (_, _) => previewBorder.BorderBrush = WpfBrushes.White;
        button.MouseLeave += (_, _) => previewBorder.BorderBrush = baseBorderBrush;
        QueuePreviewItems.Children.Add(button);

        if (_previewThumbnails.TryGetValue(item.Path, out var thumbnail))
        {
            image.Source = thumbnail;
            fallback.Visibility = Visibility.Collapsed;
            SizePreviewBorder(previewBorder, thumbnail, selected);
        }
        else
            _ = LoadPreviewThumbnailAsync(
                item, image, fallback, previewBorder, selected, generation, cancellationToken);
    }

    private async Task LoadPreviewThumbnailAsync(
        MediaItem item,
        WpfImage target,
        TextBlock fallback,
        Border previewBorder,
        bool selected,
        int generation,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            BitmapSource? thumbnail = item.Kind == MediaKind.Video
                ? await Task.Run(() => ShellThumbnail.Load(item.Path, 256, 160), cancellationToken)
                : await _imagePreloader.GetAsync(item.Path, cancellationToken);
            if (thumbnail is null)
                return;

            _previewThumbnails[item.Path] = thumbnail;
            await Dispatcher.InvokeAsync(() =>
            {
                if (generation != _previewGeneration || cancellationToken.IsCancellationRequested)
                    return;
                target.Source = thumbnail;
                fallback.Visibility = Visibility.Collapsed;
                SizePreviewBorder(previewBorder, thumbnail, selected);
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // A missing or unsupported preview keeps its clickable fallback.
        }
    }

    private static void SizePreviewBorder(Border border, BitmapSource thumbnail, bool selected)
    {
        var maxWidth = selected ? PreviewSelectedWidth : PreviewItemWidth;
        var maxHeight = selected ? PreviewSelectedHeight : PreviewItemHeight;
        var aspectRatio = (double)thumbnail.PixelWidth / thumbnail.PixelHeight;
        var width = maxWidth;
        var height = width / aspectRatio;
        if (height > maxHeight)
        {
            height = maxHeight;
            width = height * aspectRatio;
        }

        border.Width = Math.Max(1, width);
        border.Height = Math.Max(1, height);
    }

    private void QueuePreviewItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MediaItem item } ||
            _playlist?.JumpTo(item.Path) is not { } target)
            return;

        ShowItem(target, preservePlaybackState: true);
    }

    private void QueuePrevious_Click(object sender, RoutedEventArgs e)
    {
        if (_playlist?.CanGoBack == true)
            GoPrevious();
    }

    private void QueueNext_Click(object sender, RoutedEventArgs e)
    {
        if (_playlist?.PreviewUpcomingItems(1).Count > 0)
            GoNext();
    }

    private async Task WaitThenNext(CancellationToken token)
    {
        try
        {
            var remaining = TimeSpan.FromSeconds(Math.Max(1, _settings.ImageDurationSeconds));
            while (remaining > TimeSpan.Zero)
            {
                await _playbackAdvanceGate.WaitUntilResumedAsync(token);
                var started = Stopwatch.GetTimestamp();
                var interval = remaining > TimeSpan.FromMilliseconds(50)
                    ? TimeSpan.FromMilliseconds(50)
                    : remaining;
                await Task.Delay(interval, token);
                remaining -= Stopwatch.GetElapsedTime(started);
            }

            await _playbackAdvanceGate.WaitUntilResumedAsync(token);
            if (!token.IsCancellationRequested) Dispatcher.Invoke(GoNext);
        }
        catch (TaskCanceledException) { }
    }

    private void GoNext()
    {
        if (_playlist?.Next() is { } next)
        {
            _playlistWaitingForNext = false;
            ShowItem(next);
            return;
        }

        if (_playlist?.IsPlaylistActive == true)
            _playlistWaitingForNext = true;
        StopGif();
        StopVideoProgress();
        VideoView.Stop();
        VideoView.Source = null;
        ResetImageTransition();
        ImageView.Source = null;
        ImageView.Visibility = Visibility.Collapsed;
        VideoView.Visibility = Visibility.Collapsed;
        ShowMessage(_playlist?.IsPlaylistActive == true ? "PlaylistEmpty" : "NoMediaMessage");
    }

    private void GoPrevious()
    {
        if (_playlist?.Previous() is { } previous) ShowItem(previous);
    }

    private void TogglePause()
    {
        _paused = !_paused;
        if (_paused)
            _playbackAdvanceGate.Pause();
        else
            _playbackAdvanceGate.Resume();
        if (VideoView.Visibility == Visibility.Visible)
        {
            if (_paused) VideoView.Pause(); else VideoView.Play();
        }
        foreach (var animation in _kenBurnsAnimations)
        {
            if (_paused) animation.Clock.Controller?.Pause();
            else animation.Clock.Controller?.Resume();
        }
        SetButtonIcon(PauseButton, _paused ? "\uE768" : "\uE769",
            Localization.Get(_paused ? "Resume" : "Pause"));
    }

    private void ToggleMediaFilter()
    {
        _settings.MediaFilter = _settings.MediaFilter switch
        {
            MediaFilter.Images => MediaFilter.Videos,
            MediaFilter.Videos => MediaFilter.Both,
            _ => MediaFilter.Images
        };
        _settings.Save();
        UpdateVideoToggle();
        _playlist?.SetOptions(_settings.Order, _settings.MediaFilter, _settings.Direction);
        UpdateItemCounter();
        if (_playlist?.Current is { } current &&
            !IsIncluded(current.Kind, _settings.MediaFilter))
            GoNext();
        else if (_playlistWaitingForNext && _playlist?.Next() is { } next)
        {
            _playlistWaitingForNext = false;
            EmptyState.Visibility = Visibility.Collapsed;
            ShowItem(next);
        }
    }

    private void SetFullscreen(bool enabled)
    {
        WindowStyle = enabled ? WindowStyle.None : WindowStyle.SingleBorderWindow;
        WindowState = enabled ? WindowState.Maximized : WindowState.Normal;
        _settings.StartFullscreen = enabled;
        _settings.Save();
        UpdateFullscreenButton(enabled);
    }

    private void UpdateFullscreenButton(bool isFullscreen)
    {
        SetButtonIcon(FullscreenButton,
            isFullscreen ? "\uE73F" : "\uE740",
            Localization.Get(isFullscreen ? "ExitFullscreen" : "Fullscreen"));
    }

    private void AnimateTransition(UIElement element)
    {
        if (_settings.Transition != TransitionMode.Fade) return;
        element.Opacity = 0;
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1,
            TimeSpan.FromSeconds(Math.Max(0.05, _settings.FadeDurationSeconds))));
    }

    private void ShowImage(ImageSource image, int transitionVersion)
    {
        if (!IsLayeredTransition(_settings.Transition) ||
            ImageView.Visibility != Visibility.Visible ||
            ImageView.Source is null)
        {
            ResetImageTransition();
            ImageView.Source = image;
            ImageView.Visibility = Visibility.Visible;
            if (_settings.Transition == TransitionMode.KenBurns)
                AnimateKenBurns(ImageView, transitionVersion);
            else
                AnimateTransition(ImageView);
            return;
        }

        PrepareLayeredTransition();
        var width = Root.ActualWidth > 0 ? Root.ActualWidth : ActualWidth;
        var outgoingTransform = new TranslateTransform();
        var incomingTransform = new TranslateTransform(width, 0);
        ImageView.RenderTransform = outgoingTransform;
        IncomingImageView.RenderTransform = incomingTransform;
        IncomingImageView.Source = image;
        IncomingImageView.Visibility = Visibility.Visible;

        var duration = TimeSpan.FromSeconds(Math.Max(0.05, _settings.FadeDurationSeconds));
        EventHandler completed = (_, _) =>
        {
            if (transitionVersion != _transitionVersion)
                return;

            ImageView.Source = IncomingImageView.Source;
            ImageView.Visibility = Visibility.Visible;
            ResetImageTransition();
        };
        switch (_settings.Transition)
        {
            case TransitionMode.Slide:
            {
                var incomingAnimation = new DoubleAnimation(width, 0, duration);
                incomingAnimation.Completed += completed;
                incomingTransform.BeginAnimation(TranslateTransform.XProperty, incomingAnimation);
                outgoingTransform.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(0, -width, duration));
                break;
            }
            case TransitionMode.Cover:
            {
                var incomingAnimation = new DoubleAnimation(width, 0, duration);
                incomingAnimation.Completed += completed;
                incomingTransform.BeginAnimation(TranslateTransform.XProperty, incomingAnimation);
                break;
            }
            case TransitionMode.Crossfade:
            {
                ImageView.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, duration));
                var incomingAnimation = new DoubleAnimation(0, 1, duration);
                incomingAnimation.Completed += completed;
                IncomingImageView.BeginAnimation(OpacityProperty, incomingAnimation);
                break;
            }
            case TransitionMode.Zoom:
            {
                ImageView.RenderTransform = new ScaleTransform(1, 1);
                IncomingImageView.RenderTransform = new ScaleTransform(0.9, 0.9);
                ImageView.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty,
                    new DoubleAnimation(1, 1.1, duration));
                ImageView.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty,
                    new DoubleAnimation(1, 1.1, duration));
                var incomingScaleX = new DoubleAnimation(0.9, 1, duration);
                incomingScaleX.Completed += completed;
                IncomingImageView.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, incomingScaleX);
                IncomingImageView.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty,
                    new DoubleAnimation(0.9, 1, duration));
                ImageView.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, duration));
                IncomingImageView.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));
                break;
            }
            case TransitionMode.BlurDissolve:
            {
                var outgoingBlur = new BlurEffect();
                var incomingBlur = new BlurEffect { Radius = 12 };
                ImageView.Effect = outgoingBlur;
                IncomingImageView.Effect = incomingBlur;
                outgoingBlur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(0, 12, duration));
                incomingBlur.BeginAnimation(BlurEffect.RadiusProperty,
                    new DoubleAnimation(12, 0, duration));
                ImageView.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, duration));
                var incomingAnimation = new DoubleAnimation(0, 1, duration);
                incomingAnimation.Completed += completed;
                IncomingImageView.BeginAnimation(OpacityProperty, incomingAnimation);
                break;
            }
        }
    }

    private void ResetImageTransition()
    {
        StopKenBurns();
        ImageView.BeginAnimation(OpacityProperty, null);
        IncomingImageView.BeginAnimation(OpacityProperty, null);
        ImageView.Opacity = 1;
        IncomingImageView.Opacity = 1;
        ImageView.Effect = null;
        IncomingImageView.Effect = null;
        ImageView.RenderTransform = Transform.Identity;
        IncomingImageView.RenderTransform = Transform.Identity;
        IncomingImageView.Source = null;
        IncomingImageView.Visibility = Visibility.Collapsed;
    }

    private void PrepareLayeredTransition()
    {
        StopKenBurns();
        ImageView.BeginAnimation(OpacityProperty, null);
        IncomingImageView.BeginAnimation(OpacityProperty, null);
        ImageView.Opacity = 1;
        IncomingImageView.Opacity = 1;
        ImageView.Effect = null;
        IncomingImageView.Effect = null;
        ImageView.RenderTransform = Transform.Identity;
        IncomingImageView.RenderTransform = Transform.Identity;
    }

    private static bool IsLayeredTransition(TransitionMode transition) =>
        transition is TransitionMode.Slide or TransitionMode.Crossfade or TransitionMode.Zoom
            or TransitionMode.Cover or TransitionMode.BlurDissolve;

    private void AnimateKenBurns(FrameworkElement element, int transitionVersion)
    {
        StopKenBurns();
        var scale = new ScaleTransform(1, 1);
        var translate = new TranslateTransform();
        var transforms = new TransformGroup();
        transforms.Children.Add(scale);
        transforms.Children.Add(translate);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = transforms;

        var duration = TimeSpan.FromSeconds(Math.Max(1, _settings.ImageDurationSeconds));
        var direction = transitionVersion % 2 == 0 ? 1 : -1;
        AddKenBurnsAnimation(scale, ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 1.12, duration));
        AddKenBurnsAnimation(scale, ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, 1.12, duration));
        AddKenBurnsAnimation(translate, TranslateTransform.XProperty,
            new DoubleAnimation(-18 * direction, 18 * direction, duration));
        AddKenBurnsAnimation(translate, TranslateTransform.YProperty,
            new DoubleAnimation(10 * direction, -10 * direction, duration));
    }

    private void AddKenBurnsAnimation(
        Animatable target,
        DependencyProperty property,
        DoubleAnimation animation)
    {
        var clock = animation.CreateClock();
        target.ApplyAnimationClock(property, clock);
        _kenBurnsAnimations.Add((target, property, clock));
        if (_paused)
            clock.Controller?.Pause();
    }

    private void StopKenBurns()
    {
        foreach (var animation in _kenBurnsAnimations)
        {
            animation.Target.ApplyAnimationClock(animation.Property, null);
            animation.Clock.Controller?.Remove();
        }
        _kenBurnsAnimations.Clear();
    }

    private void ShowMessage(string localizationKey)
    {
        _emptyMessageKey = localizationKey;
        EmptyMessage.Text = Localization.Get(localizationKey);
        EmptyState.Visibility = Visibility.Visible;
    }

    private void ApplyLocalization()
    {
        Title = Localization.Get("Title");
        ChooseFolderButton.Content = Localization.Get("ChooseFolder");
        ChooseFolderSettingsButton.Content = Localization.Get("ChooseFolder");
        CloseSettingsButton.Content = Localization.Get("Close");
        VersionLabel.Text = $"{Localization.Get("Version")}: {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}";
        SettingsButton.ToolTip = Localization.Get("Settings");
        AutomationProperties.SetName(SettingsButton, Localization.Get("Settings"));
        SettingsTitle.Text = Localization.Get("Settings");
        ImageDurationLabel.Text = Localization.Get("ImageDuration");
        DurationBox.SetValue(AutomationProperties.NameProperty, Localization.Get("ImageDurationAutomation"));
        PlaybackOrderLabel.Text = Localization.Get("PlaybackOrder");
        OrderBox.SetValue(AutomationProperties.NameProperty, Localization.Get("PlaybackOrder"));
        FilenameOrderItem.Content = Localization.Get("FilenameOrder");
        CreationDateOrderItem.Content = Localization.Get("CreationDateOrder");
        RandomShuffleItem.Content = Localization.Get("RandomShuffle");
        SortDirectionLabel.Text = Localization.Get("SortDirection");
        DirectionBox.SetValue(AutomationProperties.NameProperty, Localization.Get("SortDirection"));
        AscendingItem.Content = Localization.Get("Ascending");
        DescendingItem.Content = Localization.Get("Descending");
        TransitionLabel.Text = Localization.Get("Transition");
        TransitionBox.SetValue(AutomationProperties.NameProperty, Localization.Get("Transition"));
        InstantSwitchItem.Content = Localization.Get("InstantSwitch");
        SimpleFadeItem.Content = Localization.Get("SimpleFade");
        SlideItem.Content = Localization.Get("Slide");
        KenBurnsItem.Content = Localization.Get("KenBurns");
        CrossfadeItem.Content = Localization.Get("Crossfade");
        ZoomItem.Content = Localization.Get("Zoom");
        CoverItem.Content = Localization.Get("Cover");
        BlurDissolveItem.Content = Localization.Get("BlurDissolve");
        FadeDurationLabel.Text = Localization.Get("FadeDuration");
        FadeBox.SetValue(AutomationProperties.NameProperty, Localization.Get("FadeDurationAutomation"));
        PreloadBox.Content = Localization.Get("Preload");
        LanguageLabel.Text = Localization.Get("Language");
        LanguageBox.SetValue(AutomationProperties.NameProperty, Localization.Get("Language"));
        KeyboardShortcutsLabel.Text = Localization.Get("KeyboardShortcuts");
        PauseOrResumeShortcut.Text = Localization.Get("PauseOrResume");
        PreviousNextShortcut.Text = Localization.Get("PreviousNext");
        SeekShortcut.Text = Localization.Get("Seek");
        FullscreenShortcut.Text = Localization.Get("ToggleFullscreen");
        MediaShortcut.Text = Localization.Get("CycleMedia");
        MuteShortcut.Text = Localization.Get("MuteVideo");
        CloseShortcut.Text = Localization.Get("CloseSettings");
        RevealShortcut.Text = Localization.Get("RevealCurrent");
        QueuePreviewShortcut.Text = Localization.Get("ToggleQueuePreview");
        FavoriteShortcut.Text = Localization.Get("FavoriteShortcut");
        PlaylistPanelTitle.Text = Localization.Get("Playlists");
        PlaylistNameLabel.Text = Localization.Get("PlaylistName");
        NewPlaylistButton.Content = Localization.Get("NewPlaylist");
        RenamePlaylistButton.Content = Localization.Get("RenamePlaylist");
        DeletePlaylistButton.Content = Localization.Get("DeletePlaylist");
        CleanupStaleEntriesButton.Content = Localization.Get("CleanupStaleEntries");
        ActivatePlaylistButton.Content = Localization.Get("ActivatePlaylist");
        PlaylistEndModeLabel.Text = Localization.Get("PlaylistEndMode");
        LoopPlaylistItem.Content = Localization.Get("LoopPlaylist");
        AppendNonPlaylistItemsItem.Content = Localization.Get("AppendNonPlaylistItems");
        SharePlaylistBox.Content = Localization.Get("SharePlaylist");
        ApiAccessLabel.Text = Localization.Get("ApiAccess");
        GenerateApiCapabilityButton.Content = Localization.Get("GenerateApiCapability");
        RevokeApiCapabilitiesButton.Content = Localization.Get("RevokeApiCapabilities");
        ClosePlaylistPanelButton.Content = Localization.Get("Close");
        PlaylistPanelButton.ToolTip = Localization.Get("Playlists");
        AutomationProperties.SetName(PlaylistPanelButton, Localization.Get("Playlists"));
        PlaylistList.SetValue(AutomationProperties.NameProperty, Localization.Get("Playlists"));
        PlaylistNameBox.SetValue(AutomationProperties.NameProperty, Localization.Get("PlaylistName"));
        PlaylistEndModeBox.SetValue(
            AutomationProperties.NameProperty, Localization.Get("PlaylistEndMode"));
        ApiCapabilityBox.SetValue(AutomationProperties.NameProperty, Localization.Get("ApiAccessKey"));
        AccessToastDismissHint.Text = Localization.Get("PressToDismiss");
        AccessToast.SetValue(AutomationProperties.NameProperty, Localization.Get("DismissFileError"));
        VideoProgress.ToolTip = Localization.Get("VideoPosition");
        AutomationProperties.SetName(VideoPreviewPopup, Localization.Get("VideoPreview"));
        QueuePreviousButton.ToolTip = Localization.Get("PreviousPreview");
        AutomationProperties.SetName(QueuePreviousButton, Localization.Get("PreviousPreview"));
        QueueNextButton.ToolTip = Localization.Get("NextPreview");
        AutomationProperties.SetName(QueueNextButton, Localization.Get("NextPreview"));
        SetButtonIcon(PreviousButton, "\uE100", Localization.Get("PreviousItem"));
        SetButtonIcon(NextButton, "\uE101", Localization.Get("NextItem"));
        SetButtonIcon(RevealButton, "\uE8B7", Localization.Get("Reveal"));
        UpdateFullscreenButton(WindowStyle == WindowStyle.None);
        SetButtonIcon(AudioButton, _currentVideoAudible ? "\uE767" : "\uE74F",
            Localization.Get(_currentVideoAudible ? "Mute" : "Unmute"));
        SetButtonIcon(PauseButton, _paused ? "\uE768" : "\uE769",
            Localization.Get(_paused ? "Resume" : "Pause"));
        UpdateQueueToggleButton();
        UpdateActivePlaylistIndicator();
        UpdateFavoriteToggle(_playlist?.Current);
        if (_emptyMessageKey is not null)
            EmptyMessage.Text = Localization.Get(_emptyMessageKey);
        LanguageBox.SelectedIndex = Localization.Current switch
        {
            AppLanguage.German => 1,
            AppLanguage.Spanish => 2,
            _ => 0
        };
        UpdateVideoToggle();
    }

    private void UpdateItemCounter()
    {
        if (_playlist is null)
        {
            ItemCounter.Text = string.Empty;
            return;
        }

        ItemCounter.Text = $"{_playlist.CurrentPosition} / {_playlist.TotalCount}";
    }

    private void ApplySettingsToUi()
    {
        DurationBox.Text = _settings.ImageDurationSeconds.ToString("0.##");
        FadeBox.Text = _settings.FadeDurationSeconds.ToString("0.##");
        OrderBox.SelectedIndex = _settings.Order switch
        {
            PlaybackOrder.CreationDate => 1,
            PlaybackOrder.Random => 2,
            _ => 0
        };
        DirectionBox.SelectedIndex = _settings.Direction == SortDirection.Descending ? 1 : 0;
        TransitionBox.SelectedIndex = _settings.Transition switch
        {
            TransitionMode.Fade => 1,
            TransitionMode.Slide => 2,
            TransitionMode.KenBurns => 3,
            TransitionMode.Crossfade => 4,
            TransitionMode.Zoom => 5,
            TransitionMode.Cover => 6,
            TransitionMode.BlurDissolve => 7,
            _ => 0
        };
        PreloadBox.IsChecked = _settings.PreloadEnabled;
        _settingsUiReady = true;
    }

    private void UpdateVideoToggle()
    {
        var (icon, tooltip) = _settings.MediaFilter switch
        {
            MediaFilter.Images => ("\uE91B", Localization.Get("ImagesOnly")),
            MediaFilter.Videos => ("\uE714", Localization.Get("VideosOnly")),
            _ => ("\uE91B\uE714", Localization.Get("ImagesAndVideos"))
        };
        VideoToggle.Content = icon;
        VideoToggle.ToolTip = tooltip;
        AutomationProperties.SetName(VideoToggle, tooltip);
        VideoToggle.FontSize = _settings.MediaFilter == MediaFilter.Both ? 11 : 17;
        VideoToggle.Opacity = 1;
        VideoToggle.Background = _settings.MediaFilter == MediaFilter.Both
            ? new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF))
            : new SolidColorBrush(Color.FromArgb(0x66, 0x18, 0x18, 0x20));
    }

    private static bool IsIncluded(MediaKind kind, MediaFilter filter) =>
        filter switch
        {
            MediaFilter.Images => kind == MediaKind.Image,
            MediaFilter.Videos => kind == MediaKind.Video,
            _ => true
        };

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        var mousePosition = e.GetPosition(this);
        if (_lastMousePosition == mousePosition)
            return;

        _lastMousePosition = mousePosition;
        ShowControls();
    }

    private void ShowControls()
    {
        Cursor = Cursors.Arrow;
        Controls.IsEnabled = true;
        Controls.Opacity = 1;
        QueuePreviewHost.IsEnabled = true;
        QueuePreviewHost.Opacity = 1;
        _controlsTimer.Stop();
        _controlsTimer.Start();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is Button)
        {
            if (e.Key == Key.Right)
            {
                GoNext();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Left)
            {
                GoPrevious();
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Tab)
        {
            ShowControls();
            e.Handled = true;
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Shift &&
            !IsEditableControlFocused())
        {
            ToggleFavorite();
            e.Handled = true;
            return;
        }
        if (e.OriginalSource is Button && e.Key is not (Key.Left or Key.Right or Key.Q))
            return;
        switch (e.Key)
        {
            case Key.Space: TogglePause(); break;
            case Key.Right: GoNext(); break;
            case Key.Left: GoPrevious(); break;
            case Key.F when Keyboard.Modifiers == ModifierKeys.None:
                SetFullscreen(WindowStyle != WindowStyle.None);
                break;
            case Key.V: ToggleMediaFilter(); break;
            case Key.M: ToggleAudio(); break;
            case Key.E: RevealCurrentInExplorer(); break;
            case Key.Q: ToggleQueuePreview(); break;
            case Key.Escape:
                if (PlaylistPanel.Visibility == Visibility.Visible)
                    PlaylistPanel.Visibility = Visibility.Collapsed;
                else if (SettingsPanel.Visibility == Visibility.Visible)
                    SettingsPanel.Visibility = Visibility.Collapsed;
                else if (WindowStyle == WindowStyle.None) SetFullscreen(false);
                break;
        }
    }

    private void VideoView_MediaEnded(object sender, RoutedEventArgs e) => GoNext();

    private void VideoView_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (!VideoView.NaturalDuration.HasTimeSpan)
            return;

        VideoProgress.Maximum = VideoView.NaturalDuration.TimeSpan.TotalSeconds;
        VideoProgress.Value = 0;
        VideoProgress.Visibility = Visibility.Visible;
        _videoProgressTimer.Start();
    }

    private void VideoView_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        StopVideoProgress();
        if (VideoView.Source is { } source)
            ShowAccessToast(source.LocalPath, e.ErrorException);
        GoNext();
    }

    private void ShowAccessToast(string path, Exception? exception)
    {
        var errorType = exception?.GetType().Name ?? Localization.Get("UnknownError");
        var errorMessage = exception is null || string.IsNullOrWhiteSpace(exception.Message)
            ? string.Empty
            : $"{exception.Message}\n";
        ShowToast(Localization.Get("FileError", errorType), $"{errorMessage}{Path.GetFileName(path)}\n{path}");
    }

    private void ShowToast(string type, string message)
    {
        AccessToastType.Text = type;
        AccessToastMessage.Text = message;
        AccessToast.Visibility = Visibility.Visible;
    }

    private void AccessToast_Click(object sender, RoutedEventArgs e)
    {
        AccessToast.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private void VideoProgress_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VideoProgress.Visibility != Visibility.Visible ||
            VideoView.Visibility != Visibility.Visible ||
            VideoView.Source is null ||
            !VideoView.NaturalDuration.HasTimeSpan)
            return;

        VideoView.Position = TimeSpan.FromSeconds(e.NewValue);
    }

    private void VideoProgress_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Thumb ||
            VideoProgress.Visibility != Visibility.Visible ||
            VideoView.Visibility != Visibility.Visible ||
            !VideoView.NaturalDuration.HasTimeSpan ||
            VideoProgress.ActualWidth <= 0)
            return;

        var position = e.GetPosition(VideoProgress);
        var ratio = Math.Clamp(position.X / VideoProgress.ActualWidth, 0, 1);
        VideoProgress.Value = VideoProgress.Minimum +
            ratio * (VideoProgress.Maximum - VideoProgress.Minimum);
        e.Handled = true;
    }

    private void VideoProgress_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right) ||
            !VideoView.NaturalDuration.HasTimeSpan)
            return;

        var offset = e.Key == Key.Right ? 10 : -10;
        VideoProgress.Value = Math.Clamp(VideoProgress.Value + offset, 0, VideoProgress.Maximum);
        RequestVideoPreview(VideoProgress.ActualWidth * GetVideoProgressRatio());
        e.Handled = true;
    }

    private void VideoProgress_MouseMove(object sender, MouseEventArgs e)
    {
        if (!VideoProgress.IsMouseOver)
            return;

        var position = e.GetPosition(VideoProgress);
        RequestVideoPreview(position.X);
        ShowControls();
    }

    private void VideoProgress_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!VideoProgress.IsKeyboardFocusWithin)
            HideVideoPreview();
    }

    private void VideoProgress_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        RequestVideoPreview(VideoProgress.ActualWidth * GetVideoProgressRatio());
    }

    private void VideoProgress_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!VideoProgress.IsMouseOver)
            HideVideoPreview();
    }

    private double GetVideoProgressRatio() =>
        VideoProgress.Maximum <= VideoProgress.Minimum
            ? 0
            : Math.Clamp((VideoProgress.Value - VideoProgress.Minimum) /
                (VideoProgress.Maximum - VideoProgress.Minimum), 0, 1);

    private void RequestVideoPreview(double pointerX)
    {
        if (VideoProgress.Visibility != Visibility.Visible ||
            VideoView.Visibility != Visibility.Visible ||
            VideoView.Source is not { } source ||
            !VideoView.NaturalDuration.HasTimeSpan ||
            VideoProgress.ActualWidth <= 0)
        {
            HideVideoPreview();
            return;
        }

        var nextPointer = Math.Clamp(pointerX, 0, VideoProgress.ActualWidth);
        var nextSeconds = Math.Clamp(
            VideoProgress.Minimum +
            nextPointer / VideoProgress.ActualWidth *
            (VideoProgress.Maximum - VideoProgress.Minimum),
            0, VideoProgress.Maximum);

        var path = source.LocalPath;
        var bucket = VideoPreviewMath.GetBucket(
            nextSeconds, VideoPreviewBucketSeconds, VideoView.NaturalDuration.TimeSpan);
        if (VideoPreviewPopup.Visibility == Visibility.Visible &&
            string.Equals(_videoPreviewPath, path, StringComparison.OrdinalIgnoreCase) &&
            Math.Abs(nextPointer - _videoPreviewPointer.X) < 0.5 &&
            Math.Abs(nextSeconds - _videoPreviewSeconds) < 0.01)
            return;

        _videoPreviewPointer = new Point(nextPointer, 0);
        _videoPreviewSeconds = nextSeconds;
        if (!string.Equals(_videoPreviewPath, path, StringComparison.OrdinalIgnoreCase))
        {
            _videoPreviewPrefetchCancellation?.Cancel();
            _videoPreviewPrefetchCancellation?.Dispose();
            _videoPreviewPrefetchCancellation = null;
            _videoPreviewPath = path;
            _videoPreviewBucket = -1;
            _videoPreviewFrames.Clear();
            VideoPreviewImage.Source = null;
            VideoPreviewImage.Visibility = Visibility.Collapsed;
        }

        VideoPreviewTimestamp.Text = VideoPreviewMath.FormatTimestamp(
            _videoPreviewSeconds, VideoView.NaturalDuration.TimeSpan);
        VideoPreviewStatus.Text = Localization.Get("VideoPreviewLoading");
        VideoPreviewPopup.Visibility = Visibility.Visible;
        PositionVideoPreview();
        if (_videoPreviewBucket == bucket)
        {
            if (_videoPreviewFrames.TryGetValue(bucket, out var cachedFrame))
            {
                VideoPreviewImage.Source = cachedFrame;
                VideoPreviewImage.Visibility = Visibility.Visible;
                VideoPreviewStatus.Text = string.Empty;
            }
            PositionVideoPreview();
            return;
        }

        _videoPreviewPrefetchCancellation?.Cancel();
        _videoPreviewBucket = bucket;
        _videoPreviewTimer.Stop();
        _videoPreviewTimer.Start();
    }

    private void PositionVideoPreview()
    {
        if (VideoPreviewPopup.Visibility != Visibility.Visible)
            return;

        VideoPreviewPopup.UpdateLayout();
        var sliderOrigin = VideoProgress.TranslatePoint(new Point(0, 0), VideoPreviewOverlay);
        var width = VideoPreviewPopup.ActualWidth;
        var x = sliderOrigin.X + _videoPreviewPointer.X - width / 2;
        var maxX = Math.Max(0, VideoPreviewOverlay.ActualWidth - width);
        Canvas.SetLeft(VideoPreviewPopup, Math.Clamp(x, 0, maxX));

        var y = sliderOrigin.Y - VideoPreviewPopup.ActualHeight - 8;
        if (y < 0)
            y = sliderOrigin.Y + VideoProgress.ActualHeight + 8;
        Canvas.SetTop(VideoPreviewPopup, y);
    }

    private async Task GenerateVideoPreviewAsync()
    {
        if (_videoPreviewPath is null)
            return;

        var path = _videoPreviewPath;
        var bucket = _videoPreviewBucket;
        var seconds = bucket * VideoPreviewBucketSeconds;
        var generation = ++_videoPreviewGeneration;
        _videoPreviewCancellation?.Cancel();
        _videoPreviewCancellation?.Dispose();
        _videoPreviewCancellation = new CancellationTokenSource();
        var token = _videoPreviewCancellation.Token;

        try
        {
            var frame = await Task.Run(
                () => _videoFrameExtractor.Extract(path, seconds, token), token);
            if (frame is null || generation != _videoPreviewGeneration)
            {
                if (generation == _videoPreviewGeneration)
                    VideoPreviewStatus.Text = Localization.Get("VideoPreviewUnavailable");
                return;
            }

            VideoPreviewImage.Source = frame;
            VideoPreviewImage.Visibility = Visibility.Visible;
            VideoPreviewStatus.Text = string.Empty;
            PositionVideoPreview();
            CacheVideoPreviewFrame(bucket, frame);
            StartVideoPreviewPrefetch(path, bucket, VideoView.NaturalDuration.TimeSpan);
        }

        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            if (generation == _videoPreviewGeneration)
                VideoPreviewStatus.Text = Localization.Get("VideoPreviewUnavailable");
        }
    }

    private void CacheVideoPreviewFrame(int bucket, BitmapSource frame)
    {
        _videoPreviewFrames[bucket] = frame;
        while (_videoPreviewFrames.Count > VideoPreviewCacheLimit)
            _videoPreviewFrames.Remove(_videoPreviewFrames.Keys.First());
    }

    private void StartVideoPreviewPrefetch(string path, int centerBucket, TimeSpan duration)
    {
        _videoPreviewPrefetchCancellation?.Cancel();
        _videoPreviewPrefetchCancellation?.Dispose();
        _videoPreviewPrefetchCancellation = new CancellationTokenSource();
        _ = PrefetchVideoPreviewAsync(
            path, centerBucket, duration, _videoPreviewPrefetchCancellation.Token);
    }

    private async Task PrefetchVideoPreviewAsync(
        string path, int centerBucket, TimeSpan duration, CancellationToken token)
    {
        var maxBucket = (int)Math.Ceiling(duration.TotalSeconds / VideoPreviewBucketSeconds);
        foreach (var bucket in Enumerable.Range(-2, 5)
                     .Select(offset => centerBucket + offset)
                     .Where(candidate => candidate >= 0 && candidate <= maxBucket)
                     .Distinct())
        {
            if (token.IsCancellationRequested || _videoPreviewFrames.ContainsKey(bucket))
                continue;

            var frame = await Task.Run(
                () => _videoFrameExtractor.Extract(
                    path, bucket * VideoPreviewBucketSeconds, token), token);
            if (frame is null || token.IsCancellationRequested ||
                !string.Equals(_videoPreviewPath, path, StringComparison.OrdinalIgnoreCase))
                continue;

            CacheVideoPreviewFrame(bucket, frame);
        }
    }

    private void HideVideoPreview()
    {
        _videoPreviewTimer.Stop();
        _videoPreviewCancellation?.Cancel();
        _videoPreviewPrefetchCancellation?.Cancel();
        VideoPreviewPopup.Visibility = Visibility.Collapsed;
    }

    private void UpdateVideoProgress()
    {
        if (VideoView.Visibility != Visibility.Visible ||
            !VideoView.NaturalDuration.HasTimeSpan)
            return;

        if (VideoProgress.Visibility != Visibility.Visible)
        {
            VideoProgress.Maximum = VideoView.NaturalDuration.TimeSpan.TotalSeconds;
            VideoProgress.Value = Math.Clamp(VideoView.Position.TotalSeconds, 0, VideoProgress.Maximum);
            VideoProgress.Visibility = Visibility.Visible;
        }

        if (VideoProgress.IsMouseCaptureWithin || VideoProgress.IsKeyboardFocusWithin)
            return;

        VideoProgress.Value = Math.Clamp(VideoView.Position.TotalSeconds, 0, VideoProgress.Maximum);
    }

    private void StopVideoProgress()
    {
        _videoProgressTimer.Stop();
        HideVideoPreview();
        VideoProgress.Visibility = Visibility.Collapsed;
        VideoProgress.Value = 0;
        VideoProgress.Maximum = 1;
    }

    private void Pause_Click(object sender, RoutedEventArgs e) => TogglePause();
    private void Next_Click(object sender, RoutedEventArgs e) => GoNext();
    private void Previous_Click(object sender, RoutedEventArgs e) => GoPrevious();
    private void VideoToggle_Click(object sender, RoutedEventArgs e) => ToggleMediaFilter();
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => SetFullscreen(WindowStyle != WindowStyle.None);

    private void RevealInExplorer_Click(object sender, RoutedEventArgs e) => RevealCurrentInExplorer();

    private void RevealCurrentInExplorer()
    {
        if (_playlist?.Current is not { } current)
            return;

        ExplorerIntegration.Reveal(current.Path);
    }

    private void Audio_Click(object sender, RoutedEventArgs e) => ToggleAudio();

    private void ToggleAudio()
    {
        if (VideoView.Visibility != Visibility.Visible) return;
        _currentVideoAudible = !_currentVideoAudible;
        VideoView.Volume = _currentVideoAudible ? 1 : 0;
        SetButtonIcon(AudioButton, _currentVideoAudible ? "\uE767" : "\uE74F",
            Localization.Get(_currentVideoAudible ? "Mute" : "Unmute"));
    }

    private void Settings_Click(object sender, RoutedEventArgs e) =>
        ToggleSettingsPanel();

    private void ToggleSettingsPanel()
    {
        SettingsPanel.Visibility = SettingsPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
        PlaylistPanel.Visibility = Visibility.Collapsed;
    }

    private void QueueToggle_Click(object sender, RoutedEventArgs e) => ToggleQueuePreview();

    private void ToggleQueuePreview()
    {
        _settings.QueuePreviewVisible = !_settings.QueuePreviewVisible;
        _settings.Save();
        ApplyLocalization();
        UpdateQueuePreview();
        if (_settings.QueuePreviewVisible)
            ShowControls();
    }

    private void UpdateQueueToggleButton()
    {
        var isVisible = _settings.QueuePreviewVisible;
        SetButtonIcon(
            QueueToggleButton,
            isVisible ? "\uE70E" : "\uE70D",
            Localization.Get(isVisible ? "HideQueuePreview" : "ShowQueuePreview"));
    }

    private static void SetButtonIcon(Button button, string glyph, string tooltip)
    {
        button.Content = glyph;
        button.FontFamily = new FontFamily("Segoe MDL2 Assets");
        button.ToolTip = tooltip;
        AutomationProperties.SetName(button, tooltip);
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromUi();
        SettingsPanel.Visibility = Visibility.Collapsed;
    }

    private void SettingsControl_Changed(object sender, RoutedEventArgs e)
    {
        if (!_settingsUiReady)
            return;
        SaveSettingsFromUi();
    }

    private void SettingsControl_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_settingsUiReady)
            SaveSettingsFromUi();
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsUiReady ||
            LanguageBox.SelectedItem is not ComboBoxItem { Tag: string languageCode } ||
            Localization.Parse(languageCode) is not { } language)
            return;

        _settings.Language = Localization.Code(language);
        Localization.SetLanguage(_settings.Language);
        _settings.Save();
        ApplyLocalization();
        _ = RefreshPlaylistPanelAsync();
    }

    private void SaveSettingsFromUi()
    {
        if (double.TryParse(DurationBox.Text, out var duration))
            _settings.ImageDurationSeconds = Math.Clamp(duration, 1, 3600);
        if (double.TryParse(FadeBox.Text, out var fade))
            _settings.FadeDurationSeconds = Math.Clamp(fade, 0.05, 10);
        _settings.Order = OrderBox.SelectedIndex switch
        {
            1 => PlaybackOrder.CreationDate,
            2 => PlaybackOrder.Random,
            _ => PlaybackOrder.Filename
        };
        _settings.Direction = DirectionBox.SelectedIndex == 1
            ? SortDirection.Descending
            : SortDirection.Ascending;
        _settings.Transition = TransitionBox.SelectedIndex switch
        {
            1 => TransitionMode.Fade,
            2 => TransitionMode.Slide,
            3 => TransitionMode.KenBurns,
            4 => TransitionMode.Crossfade,
            5 => TransitionMode.Zoom,
            6 => TransitionMode.Cover,
            7 => TransitionMode.BlurDissolve,
            _ => TransitionMode.Instant
        };
        _settings.PreloadEnabled = PreloadBox.IsChecked == true;
        _settings.Save();
        _playlist?.SetOptions(_settings.Order, _settings.MediaFilter, _settings.Direction);
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = Localization.Get("ChooseFolderDescription"),
            SelectedPath = _settings.LastFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            SettingsPanel.Visibility = Visibility.Collapsed;
            PlaylistPanel.Visibility = Visibility.Collapsed;
            _ = LoadFolderAsync(dialog.SelectedPath, null);
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _updateManager?.PrepareForExit();
        _playbackCancellation?.Cancel();
        _scanCancellation?.Cancel();
        _previewRefreshTimer.Stop();
        _videoPreviewTimer.Stop();
        _previewThumbnailCancellation?.Cancel();
        _videoPreviewCancellation?.Cancel();
        _videoPreviewPrefetchCancellation?.Cancel();
        _folderMonitor?.Dispose();
        _folderMonitor = null;
        _playlistIpcServer?.Dispose();
        _playlistIpcServer = null;
        StopGif();
        StopVideoProgress();
        VideoView.Stop();
        _imagePreloader.Dispose();
        _videoFrameExtractor.Dispose();
        _previewThumbnailCancellation?.Dispose();
        _previewThumbnailCancellation = null;
        _videoPreviewCancellation?.Dispose();
        _videoPreviewCancellation = null;
        _videoPreviewPrefetchCancellation?.Dispose();
        _videoPreviewPrefetchCancellation = null;
        _settings.Save();
    }
}
