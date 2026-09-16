using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.UIA3;
using FlaUiApplication = FlaUI.Core.Application;

namespace diashow.UiTests;

public abstract class UiTestBase : IDisposable
{
    private static readonly object LaunchLock = new();
    private readonly FlaUiApplication _application;
    private readonly UIA3Automation _automation;
    private readonly Action? _cleanup;
    private readonly string _settingsDirectory;
    protected Window MainWindow { get; }

    protected UiTestBase(
        Func<(string Path, Action Cleanup)>? testDataFactory = null,
        Action<AppSettings>? configureSettings = null)
    {
        var testData = testDataFactory?.Invoke();
        _cleanup = testData?.Cleanup;
        _settingsDirectory = Path.Combine(Path.GetTempPath(), $"diashow-ui-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_settingsDirectory);
        var settings = new AppSettings
        {
            Language = "en",
            QueuePreviewVisible = true,
            ImageDurationSeconds = 60
        };
        configureSettings?.Invoke(settings);
        settings.Save(Path.Combine(_settingsDirectory, "settings.json"));
        var executablePath = Path.Combine(AppContext.BaseDirectory, "Diashow.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = testData is null ? string.Empty : $"\"{testData.Value.Path}\"",
            UseShellExecute = false
        };
        startInfo.Environment["APPDATA"] = _settingsDirectory;
        startInfo.Environment["DIASHOW_SETTINGS_PATH"] =
            Path.Combine(_settingsDirectory, "settings.json");
        lock (LaunchLock)
        {
            var previousAppData = Environment.GetEnvironmentVariable("APPDATA");
            try
            {
                Environment.SetEnvironmentVariable("APPDATA", _settingsDirectory);
                _application = FlaUiApplication.Launch(startInfo);
            }
            finally
            {
                Environment.SetEnvironmentVariable("APPDATA", previousAppData);
            }
        }
        _automation = new UIA3Automation();
        MainWindow = _application.GetMainWindow(_automation, TimeSpan.FromSeconds(15))
            ?? throw new InvalidOperationException("The Diashow window did not start.");
        var artifactDirectory = Path.Combine(
            Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Directory.GetCurrentDirectory(),
            "TestResults",
            "ui-screenshots");
        Directory.CreateDirectory(artifactDirectory);
        Capture.Element(MainWindow).ToFile(Path.Combine(artifactDirectory, "launch.png"));
    }

    public void Dispose()
    {
        try
        {
            _application.Close();
            _automation.Dispose();
            _application.Dispose();
        }
        finally
        {
            _cleanup?.Invoke();
            if (Directory.Exists(_settingsDirectory))
                Directory.Delete(_settingsDirectory, recursive: true);
        }
    }
}
