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

    [Fact]
    public void Reveal_passes_the_existing_file_to_the_shell_selector()
    {
        var folder = Path.Combine(Path.GetTempPath(), "diashow-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "photo with spaces.jpg");
        File.WriteAllText(path, string.Empty);
        string? selectedPath = null;

        try
        {
            ExplorerIntegration.Reveal(path, selected => selectedPath = selected);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        Assert.Equal(Path.GetFullPath(path), selectedPath);
    }
}
