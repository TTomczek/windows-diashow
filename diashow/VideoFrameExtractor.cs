using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using Size = OpenCvSharp.Size;

namespace diashow;

internal sealed class VideoFrameExtractor : IDisposable
{
    private const int PreviewWidth = 384;
    private const int PreviewHeight = 216;
    private readonly object _sync = new();
    private VideoCapture? _capture;
    private string? _path;

    public BitmapSource? Extract(string path, double seconds, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureCapture(path);
            if (_capture is null)
                return null;

            _capture.Set(VideoCaptureProperties.PosMsec, Math.Max(0, seconds) * 1000);
            cancellationToken.ThrowIfCancellationRequested();
            using var frame = new Mat();
            if (!_capture.Read(frame) || frame.Empty())
                return null;

            var scale = Math.Min(
                1d,
                Math.Min((double)PreviewWidth / frame.Cols, (double)PreviewHeight / frame.Rows));
            var width = Math.Max(1, (int)Math.Round(frame.Cols * scale));
            var height = Math.Max(1, (int)Math.Round(frame.Rows * scale));
            using var resized = new Mat();
            Cv2.Resize(frame, resized, new Size(width, height), 0, 0, InterpolationFlags.Area);
            using var converted = new Mat();
            Cv2.CvtColor(resized, converted, ColorConversionCodes.BGR2BGRA);
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

    private void EnsureCapture(string path)
    {
        if (string.Equals(_path, path, StringComparison.OrdinalIgnoreCase) &&
            _capture?.IsOpened() == true)
            return;

        _capture?.Dispose();
        _capture = TryOpen(path, VideoAccelerationType.D3D11) ??
            TryOpen(path, VideoAccelerationType.Any) ??
            TryOpen(path, VideoAccelerationType.None);
        _path = _capture is null ? null : path;
    }

    private static VideoCapture? TryOpen(string path, VideoAccelerationType acceleration)
    {
        try
        {
            var capture = new VideoCapture(
                path,
                VideoCaptureAPIs.ANY,
                new VideoCapturePara(acceleration, 0));
            if (capture.IsOpened())
                return capture;
            capture.Dispose();
        }
        catch (OpenCVException)
        {
        }

        return null;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _capture?.Dispose();
            _capture = null;
            _path = null;
        }
    }
}
