using System.ComponentModel;
using System.Diagnostics;
using System.Security;

namespace Diashow.Updater;

internal static class Program
{
    private const string MutexName = "Diashow.Updater.SingleOperation";

    [STAThread]
    private static int Main(string[] args)
    {
        var options = UpdateArguments.Parse(args);
        if (options is null)
            return 2;

        using var mutex = new Mutex(false, MutexName, out var createdNew);
        if (!createdNew)
            return 0;

        try
        {
            WaitForApplication(options.ProcessId);
            ApplyUpdate(options.TargetPath, options.StagedPath);
            return 0;
        }
        catch (UnauthorizedAccessException) when (!options.Elevated)
        {
            return StartElevated(options) ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or
            ArgumentException or NotSupportedException or SecurityException)
        {
            return 1;
        }
    }

    private static void WaitForApplication(int processId)
    {
        if (processId == Environment.ProcessId)
            return;

        try
        {
            using var process = Process.GetProcessById(processId);
            process.WaitForExit();
        }
        catch (ArgumentException)
        {
        }
    }

    private static void ApplyUpdate(string targetPath, string stagedPath)
    {
        if (!File.Exists(stagedPath))
            throw new FileNotFoundException("The staged update is missing.", stagedPath);
        if (string.Equals(Path.GetFullPath(targetPath), Path.GetFullPath(stagedPath),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The staged update cannot be the installed executable.");

        var backupPath = targetPath + ".backup";
        var replaced = false;
        try
        {
            if (File.Exists(backupPath))
                File.Delete(backupPath);

            if (File.Exists(targetPath))
            {
                File.Replace(stagedPath, targetPath, backupPath, true);
            }
            else
            {
                File.Move(stagedPath, targetPath);
            }

            replaced = true;
        }
        catch
        {
            if (File.Exists(backupPath))
            {
                try
                {
                    TryDelete(targetPath);
                    File.Move(backupPath, targetPath);
                }
                catch
                {
                }
            }

            throw;
        }
        finally
        {
            if (replaced)
            {
                TryDelete(stagedPath);
                TryDelete(stagedPath + ".sha256");
                TryDelete(stagedPath + ".sig");
                TryDelete(backupPath);
            }
        }
    }

    private static bool StartElevated(UpdateArguments options)
    {
        try
        {
            var elevatedArguments = options.ToCommandLine(includeElevated: true);
            Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                Arguments = elevatedArguments,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            });
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceError("Unable to delete update file {0}: {1}", path, exception);
        }
    }

    private sealed record UpdateArguments(
        string TargetPath,
        string StagedPath,
        int ProcessId,
        bool Elevated)
    {
        public static UpdateArguments? Parse(string[] args)
        {
            string? target = null;
            string? staged = null;
            var processId = 0;
            var elevated = false;

            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--target" when index + 1 < args.Length:
                        target = Path.GetFullPath(args[++index]);
                        break;
                    case "--staged" when index + 1 < args.Length:
                        staged = Path.GetFullPath(args[++index]);
                        break;
                    case "--pid" when index + 1 < args.Length &&
                                      int.TryParse(args[++index], out processId):
                        break;
                    case "--elevated":
                        elevated = true;
                        break;
                    default:
                        return null;
                }
            }

            return string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(staged)
                ? null
                : new UpdateArguments(target, staged, processId, elevated);
        }

        public string ToCommandLine(bool includeElevated)
        {
            var commandLine = $"--target {Quote(TargetPath)} --staged {Quote(StagedPath)} --pid {ProcessId}";
            return includeElevated ? $"{commandLine} --elevated" : commandLine;
        }

        private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
    }
}
