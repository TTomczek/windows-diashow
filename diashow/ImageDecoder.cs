using System.Windows.Media.Imaging;

namespace diashow;

public static class ImageDecoder
{
    public static BitmapSource Decode(string path)
    {
        try
        {
            return DecodeCore(path, BitmapCreateOptions.PreservePixelFormat);
        }
        catch (FileFormatException) when (CanRetryWithIgnoredMetadata(path))
        {
            return DecodeCore(path,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile);
        }
    }

    private static BitmapSource DecodeCore(string path, BitmapCreateOptions options)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        var decoder = BitmapDecoder.Create(
            stream, options, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
            throw new InvalidDataException("The image contains no frames.");
        var image = decoder.Frames[0];
        image.Freeze();
        return image;
    }

    private static bool CanRetryWithIgnoredMetadata(string path) =>
        string.Equals(Path.GetExtension(path), ".jpg", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetExtension(path), ".jpeg", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetExtension(path), ".tif", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetExtension(path), ".tiff", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetExtension(path), ".webp", StringComparison.OrdinalIgnoreCase);
}
