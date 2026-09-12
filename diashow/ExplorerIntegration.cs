using System.Diagnostics;

namespace diashow;

public static class ExplorerIntegration
{
    public static void Reveal(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("The media file no longer exists.", path);

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{path}\"",
            UseShellExecute = true
        });
    }
}
