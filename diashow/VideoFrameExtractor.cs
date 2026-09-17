using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;

namespace diashow;

internal static class VideoFrameExtractor
{
    public static BitmapSource? Extract(string path, double seconds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var capture = new VideoCapture(path);
        if (!capture.IsOpened())
            return null;

        capture.Set(VideoCaptureProperties.PosMsec, Math.Max(0, seconds) * 1000);
        cancellationToken.ThrowIfCancellationRequested();
        using var frame = new Mat();
        if (!capture.Read(frame) || frame.Empty())
            return null;

        using var converted = new Mat();
        Cv2.CvtColor(frame, converted, ColorConversionCodes.BGR2BGRA);
        var pixels = new byte[checked(converted.Rows * converted.Cols * converted.ElemSize())];
        Marshal.Copy(converted.Data, pixels, 0, pixels.Length);

        var bitmap = BitmapSource.Create(
            converted.Cols,
            converted.Rows,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            converted.Cols * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
