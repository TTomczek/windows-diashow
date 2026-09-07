using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace diashow;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _controlsTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private Playlist? _playlist;
    private CancellationTokenSource? _playbackCancellation;
    private string? _requestedPath;
    private bool _paused;
    private bool _currentVideoAudible;

    public MainWindow(string[] args)
    {
        InitializeComponent();
        _requestedPath = args.FirstOrDefault();
        _controlsTimer.Tick += (_, _) => Controls.Opacity = 0;
        VideoView.Volume = 0;
        ExplorerIntegration.Install();
        ApplySettingsToUi();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_settings.StartFullscreen) SetFullscreen(true);
        var input = ResolveInput(_requestedPath);
        if (input is null)
        {
            ShowMessage("Choose a folder to start a slideshow.");
            return;
        }
        LoadFolder(input.Value.Folder, input.Value.StartFile);
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

    private void LoadFolder(string folder, string? startFile)
    {
        _settings.LastFolder = folder;
        _settings.Save();
        var items = MediaDiscovery.Find(folder);
        _playlist = new Playlist(items, _settings.Order, _settings.IncludeVideos);
        var first = _playlist.StartAt(startFile);
        if (first is null)
        {
            ShowMessage("No playable media was found in this folder.");
            return;
        }
        EmptyState.Visibility = Visibility.Collapsed;
        ShowItem(first);
    }

    private void ShowItem(MediaItem item)
    {
        _playbackCancellation?.Cancel();
        _playbackCancellation = new CancellationTokenSource();
        _currentVideoAudible = false;
        AudioButton.Content = "Unmute";
        StatusText.Text = $"{Path.GetFileName(item.Path)}  •  {(_playlist?.CanGoBack == true ? "previous available" : "first item")}";
        if (item.Kind == MediaKind.Image)
        {
            VideoView.Stop();
            VideoView.Visibility = Visibility.Collapsed;
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(item.Path);
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                ImageView.Source = image;
                ImageView.Visibility = Visibility.Visible;
                AnimateTransition(ImageView);
                _ = WaitThenNext(_playbackCancellation.Token);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                ShowMessage($"Skipped unreadable file: {Path.GetFileName(item.Path)}");
                GoNext();
            }
        }
        else
        {
            ImageView.Visibility = Visibility.Collapsed;
            VideoView.Visibility = Visibility.Visible;
            VideoView.Source = new Uri(item.Path);
            VideoView.Volume = 0;
            VideoView.Play();
            AnimateTransition(VideoView);
        }
        PauseButton.Content = "Pause";
        _paused = false;
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
        if (_playlist?.Next() is { } next) ShowItem(next);
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
        PauseButton.Content = _paused ? "Resume" : "Pause";
    }

    private void ToggleVideos()
    {
        _settings.IncludeVideos = !_settings.IncludeVideos;
        _settings.Save();
        VideoToggle.Content = $"Videos: {(_settings.IncludeVideos ? "on" : "off")}";
        _playlist?.SetOptions(_settings.Order, _settings.IncludeVideos);
        if (!_settings.IncludeVideos && _playlist?.Current?.Kind == MediaKind.Video) GoNext();
    }

    private void SetFullscreen(bool enabled)
    {
        WindowStyle = enabled ? WindowStyle.None : WindowStyle.SingleBorderWindow;
        WindowState = enabled ? WindowState.Maximized : WindowState.Normal;
        _settings.StartFullscreen = enabled;
        _settings.Save();
    }

    private void AnimateTransition(UIElement element)
    {
        if (_settings.Transition != TransitionMode.Fade) return;
        element.Opacity = 0;
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1,
            TimeSpan.FromSeconds(Math.Max(0.05, _settings.FadeDurationSeconds))));
    }

    private void ShowMessage(string message)
    {
        EmptyMessage.Text = message;
        EmptyState.Visibility = Visibility.Visible;
        StatusText.Text = message;
    }

    private void ApplySettingsToUi()
    {
        DurationBox.Text = _settings.ImageDurationSeconds.ToString("0.##");
        FadeBox.Text = _settings.FadeDurationSeconds.ToString("0.##");
        OrderBox.SelectedIndex = _settings.Order == PlaybackOrder.Random ? 1 : 0;
        TransitionBox.SelectedIndex = _settings.Transition == TransitionMode.Fade ? 1 : 0;
        VideoToggle.Content = $"Videos: {(_settings.IncludeVideos ? "on" : "off")}";
    }

    private void Window_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        Controls.Opacity = 1;
        _controlsTimer.Stop();
        _controlsTimer.Start();
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space: TogglePause(); break;
            case Key.Right: GoNext(); break;
            case Key.Left: GoPrevious(); break;
            case Key.F: SetFullscreen(WindowStyle != WindowStyle.None); break;
            case Key.V: ToggleVideos(); break;
            case Key.Escape:
                if (SettingsPanel.Visibility == Visibility.Visible) SettingsPanel.Visibility = Visibility.Collapsed;
                else if (WindowStyle == WindowStyle.None) SetFullscreen(false);
                break;
        }
    }

    private void VideoView_MediaEnded(object sender, RoutedEventArgs e) => GoNext();

    private void VideoView_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        ShowMessage($"Skipped unreadable file: {Path.GetFileName(VideoView.Source?.LocalPath)}");
        GoNext();
    }

    private void Pause_Click(object sender, RoutedEventArgs e) => TogglePause();
    private void Next_Click(object sender, RoutedEventArgs e) => GoNext();
    private void Previous_Click(object sender, RoutedEventArgs e) => GoPrevious();
    private void VideoToggle_Click(object sender, RoutedEventArgs e) => ToggleVideos();
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => SetFullscreen(WindowStyle != WindowStyle.None);

    private void Audio_Click(object sender, RoutedEventArgs e)
    {
        if (VideoView.Visibility != Visibility.Visible) return;
        _currentVideoAudible = !_currentVideoAudible;
        VideoView.Volume = _currentVideoAudible ? 1 : 0;
        AudioButton.Content = _currentVideoAudible ? "Mute" : "Unmute";
    }

    private void Settings_Click(object sender, RoutedEventArgs e) =>
        SettingsPanel.Visibility = SettingsPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(DurationBox.Text, out var duration))
            _settings.ImageDurationSeconds = Math.Clamp(duration, 1, 3600);
        if (double.TryParse(FadeBox.Text, out var fade))
            _settings.FadeDurationSeconds = Math.Clamp(fade, 0.05, 10);
        _settings.Order = OrderBox.SelectedIndex == 1 ? PlaybackOrder.Random : PlaybackOrder.Filename;
        _settings.Transition = TransitionBox.SelectedIndex == 1 ? TransitionMode.Fade : TransitionMode.Instant;
        _settings.Save();
        _playlist?.SetOptions(_settings.Order, _settings.IncludeVideos);
        SettingsPanel.Visibility = Visibility.Collapsed;
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Choose a folder for the slideshow",
            SelectedPath = _settings.LastFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            SettingsPanel.Visibility = Visibility.Collapsed;
            LoadFolder(dialog.SelectedPath, null);
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _playbackCancellation?.Cancel();
        VideoView.Stop();
        _settings.Save();
    }
}
