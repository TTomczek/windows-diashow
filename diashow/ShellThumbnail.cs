using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace diashow;

internal static class ShellThumbnail
{
    private const int ErrorSuccess = 0;
    private const uint ThumbnailOnly = 0x00000008;
    private const uint BiggerSizeOk = 0x00000001;

    public static BitmapSource? Load(string path, int width, int height)
    {
        if (!File.Exists(path))
            return null;

        var iid = typeof(IShellItemImageFactory).GUID;
        var result = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
        if (result != ErrorSuccess || factory is null)
            return null;

        IntPtr bitmap = IntPtr.Zero;
        try
        {
            result = factory.GetImage(new Size(width, height), ThumbnailOnly | BiggerSizeOk, out bitmap);
            if (result != ErrorSuccess || bitmap == IntPtr.Zero)
                return null;

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            if (bitmap != IntPtr.Zero)
                DeleteObject(bitmap);
            Marshal.FinalReleaseComObject(factory);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        string path,
        IntPtr bindContext,
        [In] ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr objectHandle);

    [ComImport]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        int GetImage(Size size, uint flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Size(int width, int height)
    {
        public int Width = width;
        public int Height = height;
    }
}
