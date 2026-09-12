namespace diashow.Tests;

public sealed class ExplorerIntegrationTests
{
    [Fact]
    public void Reveal_reports_a_missing_media_file()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "missing.jpg");

        var exception = Assert.Throws<FileNotFoundException>(() => ExplorerIntegration.Reveal(path));

        Assert.Equal(path, exception.FileName);
    }
}
