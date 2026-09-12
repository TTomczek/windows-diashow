namespace diashow.Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public void Defaults_are_safe_for_a_new_installation()
    {
        var settings = new AppSettings();

        Assert.Null(settings.Language);
        Assert.Equal(5, settings.ImageDurationSeconds);
        Assert.Equal(PlaybackOrder.Filename, settings.Order);
        Assert.Equal(MediaFilter.Both, settings.MediaFilter);
        Assert.Equal(TransitionMode.Instant, settings.Transition);
        Assert.True(settings.PreloadEnabled);
        Assert.Equal(4, settings.PreloadCount);
        Assert.Null(settings.LastFolder);
    }

    [Fact]
    public void Save_and_load_round_trip_all_user_options()
    {
        var path = Path.Combine(Path.GetTempPath(), "diashow-tests", Guid.NewGuid().ToString(), "settings.json");
        var expected = new AppSettings
        {
            ImageDurationSeconds = 12.5,
            Language = "de",
            StartFullscreen = true,
            Order = PlaybackOrder.Random,
            MediaFilter = MediaFilter.Images,
            Transition = TransitionMode.Fade,
            FadeDurationSeconds = 0.8,
            PreloadEnabled = false,
            PreloadCount = 9,
            LastFolder = "C:\\Pictures"
        };

        expected.Save(path);
        var actual = AppSettings.Load(path);

        Assert.Equal(expected.ImageDurationSeconds, actual.ImageDurationSeconds);
        Assert.Equal(expected.Language, actual.Language);
        Assert.Equal(expected.StartFullscreen, actual.StartFullscreen);
        Assert.Equal(expected.Order, actual.Order);
        Assert.Equal(expected.MediaFilter, actual.MediaFilter);
        Assert.Equal(expected.Transition, actual.Transition);
        Assert.Equal(expected.FadeDurationSeconds, actual.FadeDurationSeconds);
        Assert.Equal(expected.PreloadEnabled, actual.PreloadEnabled);
        Assert.Equal(expected.PreloadCount, actual.PreloadCount);
        Assert.Equal(expected.LastFolder, actual.LastFolder);
    }

    [Fact]
    public void Load_returns_defaults_for_missing_or_invalid_json()
    {
        var folder = Path.Combine(Path.GetTempPath(), "diashow-tests", Guid.NewGuid().ToString());
        var missing = Path.Combine(folder, "missing.json");
        var invalid = Path.Combine(folder, "invalid.json");
        Directory.CreateDirectory(folder);
        File.WriteAllText(invalid, "{ invalid");

        Assert.Equal(5, AppSettings.Load(missing).ImageDurationSeconds);
        Assert.Equal(PlaybackOrder.Filename, AppSettings.Load(invalid).Order);
    }

}
