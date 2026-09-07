using System.Text.Json;

namespace diashow;

public enum PlaybackOrder
{
    Filename,
    Random
}

public enum TransitionMode
{
    Instant,
    Fade
}

public sealed class AppSettings
{
    public double ImageDurationSeconds { get; set; } = 5;
    public bool StartFullscreen { get; set; }
    public PlaybackOrder Order { get; set; } = PlaybackOrder.Filename;
    public bool IncludeVideos { get; set; } = true;
    public TransitionMode Transition { get; set; } = TransitionMode.Instant;
    public double FadeDurationSeconds { get; set; } = 0.35;
    public bool PreloadEnabled { get; set; } = true;
    public int PreloadCount { get; set; } = 4;
    public string? LastFolder { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "diashow", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (JsonException) { }
        catch (IOException) { }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
