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

public enum MediaFilter
{
    Images,
    Videos,
    Both
}

public sealed class AppSettings
{
    public string? Language { get; set; }
    public double ImageDurationSeconds { get; set; } = 5;
    public bool StartFullscreen { get; set; }
    public PlaybackOrder Order { get; set; } = PlaybackOrder.Filename;
    public MediaFilter MediaFilter { get; set; } = MediaFilter.Both;
    public TransitionMode Transition { get; set; } = TransitionMode.Instant;
    public double FadeDurationSeconds { get; set; } = 0.35;
    public bool PreloadEnabled { get; set; } = true;
    public int PreloadCount { get; set; } = 4;
    public string? LastFolder { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "diashow", "settings.json");

    public static AppSettings Load()
        => Load(FilePath);

    public static AppSettings Load(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(filePath)) ?? new AppSettings();
        }
        catch (JsonException) { }
        catch (IOException) { }
        return new AppSettings();
    }

    public void Save()
        => Save(FilePath);

    public void Save(string filePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
