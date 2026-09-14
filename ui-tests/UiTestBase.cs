using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.UIA3;

namespace diashow.UiTests;

public abstract class UiTestBase : IDisposable
{
    private readonly Application _application;
    private readonly UIA3Automation _automation;

    protected Window MainWindow { get; }

    protected UiTestBase()
    {
        var executablePath = Path.Combine(AppContext.BaseDirectory, "Diashow.exe");
        _application = Application.Launch(executablePath);
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
        _application.Close();
        _automation.Dispose();
        _application.Dispose();
    }
}
