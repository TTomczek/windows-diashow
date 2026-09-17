namespace diashow.Tests;

public sealed class VideoPreviewTests
{
    [Theory]
    [InlineData(-2, 0)]
    [InlineData(0, 0)]
    [InlineData(0.49, 0)]
    [InlineData(0.51, 1)]
    [InlineData(2.6, 3)]
    [InlineData(99, 10)]
    public void GetBucket_clamps_and_rounds_to_the_video_duration(
        double seconds, int expectedBucket)
    {
        var bucket = VideoPreviewMath.GetBucket(
            seconds, bucketDurationSeconds: 1, TimeSpan.FromSeconds(10));

        Assert.Equal(expectedBucket, bucket);
    }

    [Fact]
    public void GetBucket_rejects_a_non_positive_bucket_duration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VideoPreviewMath.GetBucket(1, 0, TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [InlineData(83, "01:23 / 05:47")]
    [InlineData(0, "00:00 / 05:47")]
    public void FormatTimestamp_uses_mm_ss_for_short_videos(
        double seconds, string expected)
    {
        Assert.Equal(
            expected,
            VideoPreviewMath.FormatTimestamp(seconds, TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(47)));
    }

    [Fact]
    public void FormatTimestamp_uses_hours_when_duration_reaches_one_hour()
    {
        var result = VideoPreviewMath.FormatTimestamp(
            3723, TimeSpan.FromHours(1) + TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(7));

        Assert.Equal("01:02:03 / 01:05:07", result);
    }

    [Fact]
    public void Extract_cancellation_is_observed_before_opening_a_file()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var extractor = new VideoFrameExtractor();

        Assert.Throws<OperationCanceledException>(() =>
            extractor.Extract("missing-video.mp4", 1, cancellation.Token));
    }

    [Fact]
    public void Extract_returns_null_for_a_missing_video()
    {
        using var extractor = new VideoFrameExtractor();

        Assert.Null(extractor.Extract(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mp4"),
            1,
            CancellationToken.None));
    }
}
