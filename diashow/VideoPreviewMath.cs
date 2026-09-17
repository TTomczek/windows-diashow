namespace diashow;

internal static class VideoPreviewMath
{
    public static int GetBucket(double seconds, double bucketDurationSeconds, TimeSpan duration)
    {
        if (bucketDurationSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(bucketDurationSeconds));

        var maxBucket = (int)Math.Ceiling(duration.TotalSeconds / bucketDurationSeconds);
        return Math.Clamp(
            (int)Math.Round(Math.Max(0, seconds) / bucketDurationSeconds),
            0,
            maxBucket);
    }

    public static string FormatTimestamp(double seconds, TimeSpan duration)
    {
        var position = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return duration.TotalHours >= 1
            ? $"{(int)position.TotalHours:00}:{position.Minutes:00}:{position.Seconds:00} / " +
              $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{position:mm\\:ss} / {duration:mm\\:ss}";
    }
}
