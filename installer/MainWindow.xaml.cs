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

    public MainWindow()
    {
        InitializeComponent();
        InstallPathTextBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Diashow");
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
            StatusText.Text = "Downloading the latest release...";
            var executable = await DownloadLatestReleaseAsync();
            var installDirectory = Path.GetFullPath(InstallPathTextBox.Text);
            Directory.CreateDirectory(installDirectory);
            var installedExecutable = Path.Combine(installDirectory, "Diashow.exe");
            await using (executable)
            await using (var destination = File.Create(installedExecutable))
            {
                await executable.CopyToAsync(destination);
            }

            if (ContextMenuCheckBox.IsChecked == true)
                InstallContextMenu(installedExecutable);
            if (StartMenuCheckBox.IsChecked == true)
                InstallStartMenuShortcut(installedExecutable);

            StatusText.Text = "Diashow was installed successfully.";
            InstallButton.IsEnabled = false;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or
               UnauthorizedAccessException or ArgumentException or NotSupportedException or
               InvalidOperationException or JsonException or KeyNotFoundException or COMException)
        {
            ShowError($"Installation failed: {exception.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static async Task<Stream> DownloadLatestReleaseAsync()
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

        var downloadUrl = asset.GetProperty("browser_download_url").GetString();
        if (string.IsNullOrWhiteSpace(downloadUrl))
            throw new InvalidOperationException("The latest release has no download URL.");
        var response = await HttpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync();
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
        InstallButton.IsEnabled = !isBusy;
        ContextMenuCheckBox.IsEnabled = !isBusy;
        StartMenuCheckBox.IsEnabled = !isBusy;
        InstallPathTextBox.IsEnabled = !isBusy;
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
