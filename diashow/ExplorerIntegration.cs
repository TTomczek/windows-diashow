using System.Runtime.InteropServices;

namespace diashow;

public static class ExplorerIntegration
{
    public static void Reveal(string path)
    {
        Reveal(path, OpenFolderAndSelect);
    }

    internal static void Reveal(string path, Action<string> reveal)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("The media file no longer exists.", path);

        reveal(Path.GetFullPath(path));
    }

    private static void OpenFolderAndSelect(string path)
    {
        var result = SHParseDisplayName(
            path, IntPtr.Zero, out var itemIdList, 0, out _);
        Marshal.ThrowExceptionForHR(result);
        try
        {
            result = SHOpenFolderAndSelectItems(itemIdList, 0, IntPtr.Zero, 0);
            Marshal.ThrowExceptionForHR(result);
        }
        finally
        {
            ILFree(itemIdList);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(
        string name,
        IntPtr bindingContext,
        out IntPtr itemIdList,
        uint attributes,
        out uint attributesRetrieved);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(
        IntPtr folder,
        uint itemCount,
        IntPtr items,
        uint flags);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr itemIdList);
}
