using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;

namespace Diashow.Installer;

public partial class MainWindow : Window
{
    private const string Repository = "TTomczek/windows-diashow";
    private const string ReleaseApiUrl = $"https://api.github.com/repos/{Repository}/releases/latest";
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private string? _downloadedExecutable;
    private string? _downloadedUpdater;

    public MainWindow()
    {
        InitializeComponent();
        InstallPathTextBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Diashow");
        Loaded += Window_Loaded;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= Window_Loaded;
        SetBusy(true);
        StatusText.Text = "Downloading the latest release...";
        try
        {
            var progress = new Progress<double>(value =>
            {
                DownloadProgressBar.IsIndeterminate = false;
                DownloadProgressBar.Value = value;
                StatusText.Text = $"Downloading the latest release... {value:P0}";
            });
            (_downloadedExecutable, _downloadedUpdater) = await DownloadLatestReleaseAsync(progress);
            StatusText.Text = "Download complete. Choose your options and install.";
            InstallButton.IsEnabled = true;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or
               UnauthorizedAccessException or ArgumentException or NotSupportedException or
               InvalidOperationException or JsonException or KeyNotFoundException or COMException)
        {
            ShowError($"Download failed: {exception.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the Diashow installation folder",
            UseDescriptionForTitle = true,
            SelectedPath = InstallPathTextBox.Text
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
            InstallPathTextBox.Text = dialog.SelectedPath;
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (!Path.IsPathFullyQualified(InstallPathTextBox.Text))
        {
            ShowError("Please choose a valid absolute installation folder.");
            return;
        }

        SetBusy(true);
        try
        {
            if (_downloadedExecutable is null || !File.Exists(_downloadedExecutable) ||
                _downloadedUpdater is null || !File.Exists(_downloadedUpdater))
                throw new InvalidOperationException("The application or updater download is not available.");
            var installDirectory = Path.GetFullPath(InstallPathTextBox.Text);
            Directory.CreateDirectory(installDirectory);
            var installedExecutable = Path.Combine(installDirectory, "Diashow.exe");
            var installedUpdater = Path.Combine(installDirectory, "Diashow.Updater.exe");
            File.Move(_downloadedExecutable, installedExecutable, true);
            File.Move(_downloadedUpdater, installedUpdater, true);
            _downloadedExecutable = null;
            _downloadedUpdater = null;

            if (ContextMenuCheckBox.IsChecked == true)
                InstallContextMenu(installedExecutable);
            if (StartMenuCheckBox.IsChecked == true)
                InstallStartMenuShortcut(installedExecutable);

            Close();
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or
               UnauthorizedAccessException or ArgumentException or NotSupportedException or
               InvalidOperationException or JsonException or KeyNotFoundException or COMException)
        {
            ShowError($"Installation failed: {exception.Message}");
        }
        finally
        {
            if (_downloadedExecutable is not null && File.Exists(_downloadedExecutable))
                File.Delete(_downloadedExecutable);
            if (_downloadedUpdater is not null && File.Exists(_downloadedUpdater))
                File.Delete(_downloadedUpdater);
            SetBusy(false);
        }
    }

    private static async Task<(string Executable, string Updater)> DownloadLatestReleaseAsync(
        IProgress<double> progress)
    {
        using var releaseResponse = await HttpClient.GetAsync(ReleaseApiUrl);
        releaseResponse.EnsureSuccessStatusCode();
        await using var releaseJson = await releaseResponse.Content.ReadAsStreamAsync();
        using var release = await JsonDocument.ParseAsync(releaseJson);
        var asset = release.RootElement.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(item => string.Equals(
                item.GetProperty("name").GetString(), "Diashow.exe", StringComparison.OrdinalIgnoreCase));
        if (asset.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("The latest GitHub release does not contain Diashow.exe.");
        var updaterAsset = release.RootElement.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(item => string.Equals(
                item.GetProperty("name").GetString(), "Diashow.Updater.exe",
                StringComparison.OrdinalIgnoreCase));
        if (updaterAsset.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException(
                "The latest GitHub release does not contain Diashow.Updater.exe.");

        var downloadUrl = asset.GetProperty("browser_download_url").GetString();
        if (string.IsNullOrWhiteSpace(downloadUrl))
            throw new InvalidOperationException("The latest release has no download URL.");
        var updaterDownloadUrl = updaterAsset.GetProperty("browser_download_url").GetString();
        if (string.IsNullOrWhiteSpace(updaterDownloadUrl))
            throw new InvalidOperationException("The latest release has no updater download URL.");
        var temporaryExecutable = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.Diashow.exe");
        var temporaryUpdater = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.Diashow.Updater.exe");
        try
        {
            await DownloadAssetAsync(downloadUrl, temporaryExecutable, progress);
            await DownloadAssetAsync(updaterDownloadUrl, temporaryUpdater, null);
            return (temporaryExecutable, temporaryUpdater);
        }
        catch
        {
            File.Delete(temporaryExecutable);
            File.Delete(temporaryUpdater);
            throw;
        }
    }

    private static async Task DownloadAssetAsync(
        string downloadUrl, string temporaryFile, IProgress<double>? progress)
    {
        using var response = await HttpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var totalBytes = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var destination = new FileStream(
            temporaryFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        var buffer = new byte[81920];
        long downloadedBytes = 0;
        int bytesRead;
        while ((bytesRead = await source.ReadAsync(buffer)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, bytesRead));
            downloadedBytes += bytesRead;
            if (totalBytes is > 0)
                progress?.Report((double)downloadedBytes / totalBytes.Value);
        }

        progress?.Report(1);
    }

    private static void InstallContextMenu(string executable)
    {
        AddContextMenuEntry(@"Software\Classes\Directory\shell\diashow", executable);
        AddContextMenuEntry(@"Software\Classes\*\shell\diashow", executable);
    }

    private static void AddContextMenuEntry(string keyPath, string executable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath)
            ?? throw new InvalidOperationException("Unable to create the Explorer context-menu entry.");
        key.SetValue(null, "Start diashow");
        using var commandKey = key.CreateSubKey("command")
            ?? throw new InvalidOperationException("Unable to create the Explorer context-menu command.");
        commandKey.SetValue(null, $"\"{executable}\" \"%1\"");
    }

    private static void InstallStartMenuShortcut(string executable)
    {
        var startMenuDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
        Directory.CreateDirectory(startMenuDirectory);
        var shortcutPath = Path.Combine(startMenuDirectory, "Diashow.lnk");
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows shortcut support is unavailable.");
        var shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("Windows shortcut support is unavailable.");
        try
        {
            dynamic shortcut = shellType.InvokeMember(
                "CreateShortcut", BindingFlags.InvokeMethod, null, shell, [shortcutPath])
                ?? throw new InvalidOperationException("Unable to create the Start menu shortcut.");
            shortcut.TargetPath = executable;
            shortcut.WorkingDirectory = Path.GetDirectoryName(executable);
            shortcut.Description = "Diashow";
            shortcut.Save();
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    private void SetBusy(bool isBusy)
    {
        InstallButton.IsEnabled = !isBusy && _downloadedExecutable is not null &&
            _downloadedUpdater is not null;
        ContextMenuCheckBox.IsEnabled = !isBusy;
        StartMenuCheckBox.IsEnabled = !isBusy;
        InstallPathTextBox.IsEnabled = !isBusy;
        if (isBusy)
        {
            DownloadProgressBar.Value = 0;
            DownloadProgressBar.IsIndeterminate = true;
        }
    }

    private void ShowError(string message)
    {
        StatusText.Text = message;
        MessageBox.Show(this, message, "Diashow installer", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Diashow-Installer", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
