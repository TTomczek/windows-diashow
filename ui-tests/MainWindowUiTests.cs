namespace diashow.UiTests;

public sealed class MainWindowUiTests : UiTestBase
{
    [Fact]
    public void ApplicationStartsWithExpectedWindowTitle()
    {
        Assert.Equal("Diashow", MainWindow.Title);
    }
}
