using System.Diagnostics;
using Microsoft.Win32;

namespace diashow;

public static class ExplorerIntegration
{
    public static void Install()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe)) return;
        Add(@"Software\Classes\Directory\shell\diashow", "Start diashow", $"\"{exe}\" \"%1\"");
        Add(@"Software\Classes\*\shell\diashow", "Start diashow", $"\"{exe}\" \"%1\"");
    }

    private static void Add(string keyPath, string label, string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        key?.SetValue(null, label);
        using var commandKey = Registry.CurrentUser.CreateSubKey($"{keyPath}\\command");
        commandKey?.SetValue(null, command);
    }

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
