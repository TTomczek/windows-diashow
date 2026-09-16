using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace diashow.UiTests;

public sealed class MainWindowUiTests : UiTestBase
{
    private const string FirstImage = "001.png";
    private const string SecondImage = "002.png";

    public MainWindowUiTests() : base(CreateTestData, settings => settings.ImageDurationSeconds = 1)
    {
    }

    [Fact]
    public void ApplicationStartsWithExpectedWindowTitle()
    {
        Assert.Equal("Diashow", MainWindow.Title);
    }

    [Fact]
    public void Paused_slideshow_advances_after_reveal_and_resume()
    {
        WaitUntil(() => HasName("1 / 2"), TimeSpan.FromSeconds(10));

        var bounds = MainWindow.BoundingRectangle;
        Mouse.MoveTo(
            bounds.X + bounds.Width / 2,
            bounds.Y + bounds.Height - 50);
        WaitUntil(() => HasName("Pause"), TimeSpan.FromSeconds(2));
        ClickButton("Pause");
        ClickButton("Reveal in explorer");
        Thread.Sleep(1500);
        MainWindow.SetForeground();
        MainWindow.Focus();
        Mouse.MoveTo(
            bounds.X + bounds.Width / 2,
            bounds.Y + bounds.Height - 50);
        Keyboard.Press(VirtualKeyShort.SPACE);

        WaitUntil(() => HasName("2 / 2"), TimeSpan.FromSeconds(10));
    }

    private bool HasName(string name) =>
        MainWindow.FindAllDescendants().Any(element => element.Name == name);

    private void ClickButton(string name)
    {
        AutomationElement? button = null;
        WaitUntil(() =>
        {
            button = MainWindow.FindFirstDescendant(cf => cf.ByName(name));
            return button?.IsEnabled == true;
        }, TimeSpan.FromSeconds(2));
        (button ?? throw new InvalidOperationException($"The '{name}' button was not found."))
            .Patterns.Invoke.Pattern.Invoke();
    }

    private static (string Path, Action Cleanup) CreateTestData()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"diashow-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, FirstImage), OnePixelPng);
        File.WriteAllBytes(Path.Combine(folder, SecondImage), OnePixelPng);
        return (folder, () => Directory.Delete(folder, recursive: true));
    }

    private static readonly byte[] OnePixelPng =
        Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            Thread.Sleep(100);
        }

        Assert.True(condition(), "The expected UI state was not reached before the timeout.");
    }
}
