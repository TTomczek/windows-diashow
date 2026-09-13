using System.ComponentModel;
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
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace diashow;

public partial class MainWindow : Window
{
    private const int DiscoveryBatchSize = 500;
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly ImagePreloader _imagePreloader = new();
    private readonly DispatcherTimer _controlsTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _videoProgressTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly UpdateManager? _updateManager;
    private Playlist? _playlist;
    private CancellationTokenSource? _playbackCancellation;
    private CancellationTokenSource? _scanCancellation;
    private MediaFolderMonitor? _folderMonitor;
    private string? _requestedPath;
    private string? _rootFolder;
    private bool _paused;
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

    public MainWindow(string[] args, UpdateManager? updateManager = null)
    {
        InitializeComponent();
        _updateManager = updateManager;
        _requestedPath = args.FirstOrDefault();
        _controlsTimer.Tick += (_, _) =>
        {
            Controls.Opacity = 0;
            Controls.IsEnabled = false;
            Cursor = Cursors.None;
        };
        _videoProgressTimer.Tick += (_, _) => UpdateVideoProgress();
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
        _folderMonitor?.Dispose();
        _folderMonitor = new MediaFolderMonitor(folder);
        _folderMonitor.MediaAdded += FolderMonitor_MediaAdded;
        _folderMonitor.MediaRemoved += FolderMonitor_MediaRemoved;
        _imagePreloader.Clear();
        var playlist = new Playlist([], _settings.Order, _settings.MediaFilter, _settings.Direction);
        _playlist = playlist;
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
            var isRequestedItem = startFile is not null &&
                string.Equals(item.Path, startFile, StringComparison.OrdinalIgnoreCase);
            if (!started && (startFile is null || isRequestedItem))
            {
                var initialItems = pending.ToArray();
                pending.Clear();
                var first = await Dispatcher.InvokeAsync(() =>
                {
                    if (_playlist != playlist || scanCancellation.IsCancellationRequested)
                        return null;

                    playlist.AddItems(initialItems);
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
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_playlist != playlist || scanCancellation.IsCancellationRequested)
                        return;

                    playlist.AddItems(batch);
                    UpdateItemCounter();
                });
            }
        }

        if (scanCancellation.IsCancellationRequested)
            return;
        if (pending.Count > 0)
        {
            var batch = pending.ToArray();
            await Dispatcher.InvokeAsync(() =>
            {
                if (_playlist != playlist || scanCancellation.IsCancellationRequested)
                    return;

                playlist.AddItems(batch);
                UpdateItemCounter();
            });
        }
        if (!started)
        {
            var first = await Dispatcher.InvokeAsync(() =>
            {
                if (_playlist != playlist || scanCancellation.IsCancellationRequested)
                    return null;

                return startFile is null ? playlist.StartRandom() : playlist.StartAt(startFile);
            });
            if (first is not null)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    EmptyState.Visibility = Visibility.Collapsed;
                    ShowItem(first);
                });
            }
            else
                await Dispatcher.InvokeAsync(() => ShowMessage("NoMediaMessage"));
        }
    }

    private void FolderMonitor_MediaAdded(MediaItem item) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (_playlist is null || !File.Exists(item.Path))
                return;
            _playlist.AddItems([item]);
            UpdateItemCounter();
            PreloadUpcoming();
        });

    private void FolderMonitor_MediaRemoved(string path) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (_playlist is null)
                return;
            var currentRemoved = _playlist.RemoveItems([path]);
            _imagePreloader.Clear();
            UpdateItemCounter();
            if (currentRemoved)
                GoNext();
        });

    private async void ShowItem(MediaItem item)
    {
        var transitionVersion = ++_transitionVersion;
        _playbackCancellation?.Cancel();
        StopGif();
        _playbackCancellation = new CancellationTokenSource();
        _currentVideoAudible = false;
        SetButtonIcon(AudioButton, "\uE767", Localization.Get("Unmute"));
        var relativePath = _rootFolder is null ? item.Path : Path.GetRelativePath(_rootFolder, item.Path);
        PathBanner.Text = relativePath.Replace('\\', '/').Replace("/", " / ");
        var mediaName = Localization.Get("MediaName",
            Localization.Get(item.Kind == MediaKind.Image ? "Image" : "Video"), Path.GetFileName(item.Path));
        AutomationProperties.SetName(ImageView, mediaName);
        AutomationProperties.SetName(VideoView, mediaName);
        UpdateItemCounter();
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
            _videoProgressTimer.Start();
            AnimateTransition(VideoView);
            PreloadUpcoming();
        }
        SetButtonIcon(PauseButton, "\uE769", Localization.Get("Pause"));
        _paused = false;
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

    private async Task WaitThenNext(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _settings.ImageDurationSeconds)), token);
            if (!token.IsCancellationRequested && !_paused) Dispatcher.Invoke(GoNext);
        }
        catch (TaskCanceledException) { }
    }

    private void GoNext()
    {
        if (_playlist?.Next() is { } next)
        {
            ShowItem(next);
            return;
        }

        StopGif();
        StopVideoProgress();
        VideoView.Stop();
        VideoView.Source = null;
        ResetImageTransition();
        ImageView.Source = null;
        ImageView.Visibility = Visibility.Collapsed;
        VideoView.Visibility = Visibility.Collapsed;
        ShowMessage("NoMediaMessage");
    }

    private void GoPrevious()
    {
        if (_playlist?.Previous() is { } previous) ShowItem(previous);
    }

    private void TogglePause()
    {
        _paused = !_paused;
        if (VideoView.Visibility == Visibility.Visible)
        {
            if (_paused) VideoView.Pause(); else VideoView.Play();
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

    private void AnimateKenBurns(UIElement element, int transitionVersion)
    {
        var scale = new ScaleTransform(1, 1);
        var translate = new TranslateTransform();
        var transforms = new TransformGroup();
        transforms.Children.Add(scale);
        transforms.Children.Add(translate);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = transforms;

        var duration = TimeSpan.FromSeconds(Math.Max(1, _settings.ImageDurationSeconds));
        var direction = transitionVersion % 2 == 0 ? 1 : -1;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 1.12, duration));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 1.12, duration));
        translate.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-18 * direction, 18 * direction, duration));
        translate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(10 * direction, -10 * direction, duration));
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
        AccessToastDismissHint.Text = Localization.Get("PressToDismiss");
        AccessToast.SetValue(AutomationProperties.NameProperty, Localization.Get("DismissFileError"));
        VideoProgress.ToolTip = Localization.Get("VideoPosition");
        SetButtonIcon(PreviousButton, "\uE100", Localization.Get("PreviousItem"));
        SetButtonIcon(NextButton, "\uE101", Localization.Get("NextItem"));
        SetButtonIcon(RevealButton, "\uE8B7", Localization.Get("Reveal"));
        UpdateFullscreenButton(WindowStyle == WindowStyle.None);
        SetButtonIcon(AudioButton, _currentVideoAudible ? "\uE74F" : "\uE767",
            Localization.Get(_currentVideoAudible ? "Mute" : "Unmute"));
        SetButtonIcon(PauseButton, _paused ? "\uE768" : "\uE769",
            Localization.Get(_paused ? "Resume" : "Pause"));
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
        Cursor = Cursors.Arrow;
        Controls.IsEnabled = true;
        Controls.Opacity = 1;
        _controlsTimer.Stop();
        _controlsTimer.Start();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab && !Controls.IsEnabled)
        {
            Controls.IsEnabled = true;
            Controls.Opacity = 1;
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space: TogglePause(); break;
            case Key.Right: GoNext(); break;
            case Key.Left: GoPrevious(); break;
            case Key.F: SetFullscreen(WindowStyle != WindowStyle.None); break;
            case Key.V: ToggleMediaFilter(); break;
            case Key.M: ToggleAudio(); break;
            case Key.E: RevealCurrentInExplorer(); break;
            case Key.Escape:
                if (SettingsPanel.Visibility == Visibility.Visible) SettingsPanel.Visibility = Visibility.Collapsed;
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
        e.Handled = true;
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
        SetButtonIcon(AudioButton, _currentVideoAudible ? "\uE74F" : "\uE767",
            Localization.Get(_currentVideoAudible ? "Mute" : "Unmute"));
    }

    private void Settings_Click(object sender, RoutedEventArgs e) =>
        SettingsPanel.Visibility = SettingsPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;

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
            _ = LoadFolderAsync(dialog.SelectedPath, null);
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _updateManager?.PrepareForExit();
        _playbackCancellation?.Cancel();
        _scanCancellation?.Cancel();
        _folderMonitor?.Dispose();
        _folderMonitor = null;
        StopGif();
        StopVideoProgress();
        VideoView.Stop();
        _imagePreloader.Dispose();
        _settings.Save();
    }
}
