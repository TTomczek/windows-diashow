namespace diashow.Tests;

public sealed class ImageDecoderTests
{
    private const string OneByOnePng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";

    [Fact]
    public void Decode_reads_dimensions_and_freezes_the_result()
    {
        var path = CreatePng();

        var image = ImageDecoder.Decode(path);

        Assert.Equal(1, image.PixelWidth);
        Assert.Equal(1, image.PixelHeight);
        Assert.True(image.IsFrozen);
    }

    [Fact]
    public void Decode_rejects_missing_and_invalid_files()
    {
        var folder = CreateFolder();
        var missing = Path.Combine(folder, "missing.png");
        var invalid = Path.Combine(folder, "invalid.png");
        File.WriteAllText(invalid, "not an image");

        Assert.Throws<FileNotFoundException>(() => ImageDecoder.Decode(missing));
        Assert.Throws<NotSupportedException>(() => ImageDecoder.Decode(invalid));
    }

    [Fact]
    public async Task Preloader_caches_images_and_clear_forces_a_new_decode()
    {
        var path = CreatePng();
        using var preloader = new ImagePreloader(capacity: 2);

        var first = await preloader.GetAsync(path, CancellationToken.None);
        var cached = await preloader.GetAsync(path.ToUpperInvariant(), CancellationToken.None);
        preloader.Clear();
        var afterClear = await preloader.GetAsync(path, CancellationToken.None);

        Assert.Same(first, cached);
        Assert.NotSame(first, afterClear);
    }

    [Fact]
    public async Task Preloader_propagates_cancellation_and_does_not_cache_failures()
    {
        using var preloader = new ImagePreloader();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            preloader.GetAsync(Path.Combine(Path.GetTempPath(), "missing.png"), cancellation.Token));
    }

    private static string CreatePng()
    {
        var folder = CreateFolder();
        var path = Path.Combine(folder, "image.png");
        File.WriteAllBytes(path, Convert.FromBase64String(OneByOnePng));
        return path;
    }

    private static string CreateFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "diashow-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(folder);
        return folder;
    }
}
