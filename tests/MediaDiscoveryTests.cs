namespace diashow.Tests;

public sealed class MediaDiscoveryTests
{
    [Fact]
    public void Find_includes_supported_images_and_treats_other_files_as_video()
    {
        var folder = CreateFolder();
        File.WriteAllText(Path.Combine(folder, "photo.JPG"), "");
        File.WriteAllText(Path.Combine(folder, "phone.HEIC"), "");
        File.WriteAllText(Path.Combine(folder, "clip.mp4"), "");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "");
        Directory.CreateDirectory(Path.Combine(folder, "nested"));
        File.WriteAllText(Path.Combine(folder, "nested", "scan.png"), "");

        var items = MediaDiscovery.Find(folder);

        Assert.Equal(
            ["clip.mp4", "nested\\scan.png", "notes.txt", "phone.HEIC", "photo.JPG"],
            items.Select(item => Path.GetRelativePath(folder, item.Path)));
        Assert.Equal(MediaKind.Image, items.Single(item => item.Path.EndsWith("photo.JPG")).Kind);
        Assert.Equal(MediaKind.Image, items.Single(item => item.Path.EndsWith("phone.HEIC")).Kind);
        Assert.Equal(MediaKind.Video, items.Single(item => item.Path.EndsWith("notes.txt")).Kind);
    }

    [Fact]
    public void Find_returns_empty_for_missing_folder()
    {
        Assert.Empty(MediaDiscovery.Find(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())));
    }

    [Fact]
    public async Task Stream_completes_and_returns_nested_media()
    {
        var folder = CreateFolder();
        File.WriteAllText(Path.Combine(folder, "one.jpg"), "");
        Directory.CreateDirectory(Path.Combine(folder, "nested"));
        File.WriteAllText(Path.Combine(folder, "nested", "two.mp4"), "");

        var reader = MediaDiscovery.Stream(folder);
        var items = await reader.ReadAllAsync().ToListAsync();

        Assert.Equal(2, items.Count);
        Assert.Contains(items, item => item.Path.EndsWith("one.jpg"));
        Assert.Contains(items, item => item.Path.EndsWith("two.mp4"));
    }

    [Fact]
    public async Task Stream_stops_when_cancelled_before_discovery()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var reader = MediaDiscovery.Stream(CreateFolder(), cancellation.Token);

        var items = await reader.ReadAllAsync().ToListAsync();

        Assert.Empty(items);
    }

    [Fact]
    public void IsCorruptJpeg_rejects_truncated_jpeg_and_ignores_non_jpeg()
    {
        var folder = CreateFolder();
        var truncated = Path.Combine(folder, "broken.jpg");
        File.WriteAllBytes(truncated, [0xFF, 0xD8, 0x00]);
        var text = Path.Combine(folder, "file.txt");
        File.WriteAllText(text, "not an image");

        Assert.True(MediaDiscovery.IsCorruptJpeg(truncated));
        Assert.False(MediaDiscovery.IsCorruptJpeg(text));
    }

    private static string CreateFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "diashow-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(folder);
        return folder;
    }
}
