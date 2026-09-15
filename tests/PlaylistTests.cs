using System.Diagnostics;

namespace diashow.Tests;

public sealed class PlaylistTests
{
    private static MediaItem Image(string path) => new(path, MediaKind.Image);
    private static MediaItem Video(string path) => new(path, MediaKind.Video);

    [Fact]
    public void Filename_order_sorts_initial_items_and_advances_in_order()
    {
        var playlist = new Playlist(
            [Image("z.jpg"), Image("A.jpg"), Image("m.jpg")],
            PlaybackOrder.Filename,
            includeVideos: true);

        Assert.Equal("A.jpg", playlist.Next()!.Path);
        Assert.Equal("m.jpg", playlist.Next()!.Path);
        Assert.Equal("z.jpg", playlist.Next()!.Path);
    }

    [Fact]
    public void Creation_date_order_supports_both_directions()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"diashow-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var older = Path.Combine(folder, "older.jpg");
            var newer = Path.Combine(folder, "newer.jpg");
            File.WriteAllText(older, string.Empty);
            File.WriteAllText(newer, string.Empty);
            File.SetCreationTimeUtc(older, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            File.SetCreationTimeUtc(newer, new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            var playlist = new Playlist(
                [Image(newer), Image(older)],
                PlaybackOrder.CreationDate,
                includeVideos: true,
                SortDirection.Ascending);

            Assert.Equal(older, playlist.Next()!.Path);
            Assert.Equal(newer, playlist.Next()!.Path);

            playlist.SetOptions(PlaybackOrder.CreationDate, MediaFilter.Both, SortDirection.Descending);

            Assert.Equal(newer, playlist.Next()!.Path);
            Assert.Equal(older, playlist.Next()!.Path);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData(PlaybackOrder.Filename, SortDirection.Ascending)]
    [InlineData(PlaybackOrder.Filename, SortDirection.Descending)]
    [InlineData(PlaybackOrder.CreationDate, SortDirection.Ascending)]
    [InlineData(PlaybackOrder.CreationDate, SortDirection.Descending)]
    [InlineData(PlaybackOrder.Random, SortDirection.Ascending)]
    [InlineData(PlaybackOrder.Random, SortDirection.Descending)]
    public void Every_ordering_variant_returns_each_item_once(
        PlaybackOrder order,
        SortDirection direction)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"diashow-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var paths = new[]
            {
                Path.Combine(folder, "alpha.jpg"),
                Path.Combine(folder, "bravo.jpg"),
                Path.Combine(folder, "charlie.jpg")
            };
            foreach (var path in paths)
                File.WriteAllText(path, string.Empty);
            File.SetCreationTimeUtc(paths[0], new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            File.SetCreationTimeUtc(paths[1], new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            File.SetCreationTimeUtc(paths[2], new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            var playlist = new Playlist(
                paths.Select(Image),
                order,
                includeVideos: true,
                direction);
            var result = Enumerable.Range(0, paths.Length)
                .Select(_ => playlist.Next()!.Path)
                .Select(Path.GetFileName)
                .ToArray();

            Assert.Equal(paths.Select(Path.GetFileName).ToHashSet(), result.ToHashSet());
            Assert.DoesNotContain(result, static path => path is null);
            if (order is PlaybackOrder.Filename or PlaybackOrder.CreationDate)
            {
                var expected = paths.Select(Path.GetFileName).ToArray();
                if (direction == SortDirection.Descending)
                    Array.Reverse(expected);
                Assert.Equal(expected, result);
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Excluding_videos_affects_count_and_navigation()
    {
        var playlist = new Playlist(
            [Image("a.jpg"), Video("b.mp4"), Image("c.png")],
            PlaybackOrder.Filename,
            includeVideos: false);

        Assert.Equal(2, playlist.TotalCount);
        Assert.Equal("a.jpg", playlist.Next()!.Path);
        Assert.Equal("c.png", playlist.Next()!.Path);
        Assert.Equal("a.jpg", playlist.Next()!.Path);
    }

    [Fact]
    public void Videos_only_filter_excludes_images()
    {
        var playlist = new Playlist(
            [Image("a.jpg"), Video("b.mp4"), Image("c.png")],
            PlaybackOrder.Filename,
            MediaFilter.Videos);

        Assert.Equal(1, playlist.TotalCount);
        Assert.Equal("b.mp4", playlist.Next()!.Path);
        Assert.Equal("b.mp4", playlist.Next()!.Path);
    }

    [Fact]
    public void Previous_does_not_move_before_first_item()
    {
        var playlist = new Playlist([Image("a.jpg")], PlaybackOrder.Filename, includeVideos: true);

        var first = playlist.Next();

        Assert.Same(first, playlist.Previous());
        Assert.False(playlist.CanGoBack);
        Assert.Equal(1, playlist.CurrentPosition);
    }

    [Fact]
    public void StartAt_is_case_insensitive_and_unknown_path_falls_back_to_next()
    {
        var playlist = new Playlist(
            [Image("a.jpg"), Image("b.jpg")],
            PlaybackOrder.Filename,
            includeVideos: true);

        Assert.Equal("b.jpg", playlist.StartAt("B.JPG")!.Path);

        var fallback = new Playlist([Image("a.jpg")], PlaybackOrder.Filename, includeVideos: true);
        Assert.Equal("a.jpg", fallback.StartAt("missing.jpg")!.Path);
    }

    [Fact]
    public void Preload_candidates_never_returns_more_than_requested_plus_current()
    {
        var playlist = new Playlist(
            [Image("a.jpg"), Image("b.jpg"), Image("c.jpg")],
            PlaybackOrder.Filename,
            includeVideos: true);
        playlist.Next();

        Assert.Equal(["a.jpg", "b.jpg"], playlist.PreloadCandidates(1).Select(item => item.Path));
        Assert.Equal(["a.jpg"], playlist.PreloadCandidates(-1).Select(item => item.Path));
    }

    [Fact]
    public void AddItems_keeps_filename_order_and_updates_total_count()
    {
        var playlist = new Playlist([Image("a.jpg")], PlaybackOrder.Filename, includeVideos: true);

        playlist.AddItems([Image("c.jpg"), Video("b.mp4")]);

        Assert.Equal(3, playlist.TotalCount);
        Assert.Equal(["a.jpg", "b.mp4", "c.jpg"],
            Enumerable.Range(0, 3).Select(_ => playlist.Next()!.Path));
    }

    [Fact]
    public void AddItems_100000_items_completes_within_startup_budget()
    {
        var playlist = new Playlist([], PlaybackOrder.Filename, includeVideos: true);
        var items = Enumerable.Range(0, 100_000)
            .Select(index => Image($"{index:D6}.jpg"))
            .ToArray();
        var stopwatch = Stopwatch.StartNew();

        foreach (var batch in items.Chunk(500))
            playlist.AddItems(batch);

        stopwatch.Stop();

        Assert.Equal(items.Length, playlist.TotalCount);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(30),
            $"Adding 100,000 items took {stopwatch.Elapsed}.");
    }

    [Theory]
    [InlineData(PlaybackOrder.Filename, SortDirection.Ascending)]
    [InlineData(PlaybackOrder.Filename, SortDirection.Descending)]
    [InlineData(PlaybackOrder.CreationDate, SortDirection.Ascending)]
    [InlineData(PlaybackOrder.CreationDate, SortDirection.Descending)]
    [InlineData(PlaybackOrder.Random, SortDirection.Ascending)]
    [InlineData(PlaybackOrder.Random, SortDirection.Descending)]
    public void AddItems_1000_items_checks_startup_time_for_each_ordering_variant(
        PlaybackOrder order,
        SortDirection direction)
    {
        var playlist = new Playlist([], order, includeVideos: true, direction);
        var items = Enumerable.Range(0, 1_000)
            .Select(index => Image($"{index:D6}.jpg"))
            .ToArray();
        var stopwatch = Stopwatch.StartNew();

        foreach (var batch in items.Chunk(500))
            playlist.AddItems(batch);

        stopwatch.Stop();

        Assert.Equal(items.Length, playlist.TotalCount);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"{order} {direction} took {stopwatch.Elapsed}.");
    }

    [Fact]
    public void RemoveItems_removes_deleted_current_and_allows_advancing()
    {
        var playlist = new Playlist(
            [Image("a.jpg"), Image("b.jpg"), Image("c.jpg")],
            PlaybackOrder.Filename,
            includeVideos: true);
        Assert.Equal("a.jpg", playlist.Next()!.Path);
        Assert.Equal("b.jpg", playlist.Next()!.Path);

        Assert.True(playlist.RemoveItems(["b.jpg"]));

        Assert.Equal(2, playlist.TotalCount);
        Assert.Equal("c.jpg", playlist.Next()!.Path);
        Assert.DoesNotContain(playlist.PreloadCandidates(10), item => item.Path == "b.jpg");
    }

    [Fact]
    public void SetOptions_rebuilds_remaining_items_using_new_filter()
    {
        var playlist = new Playlist(
            [Image("a.jpg"), Video("b.mp4"), Image("c.jpg")],
            PlaybackOrder.Filename,
            includeVideos: true);
        Assert.Equal("a.jpg", playlist.Next()!.Path);

        playlist.SetOptions(PlaybackOrder.Filename, includeVideos: false);

        Assert.Equal(2, playlist.TotalCount);
        Assert.Equal("c.jpg", playlist.Next()!.Path);
    }

    [Fact]
    public void Current_position_counts_only_items_in_active_filter()
    {
        var playlist = new Playlist(
            [Image("a.jpg"), Video("b.mp4"), Image("c.jpg")],
            PlaybackOrder.Filename,
            MediaFilter.Both);
        playlist.Next();
        playlist.Next();

        playlist.SetOptions(PlaybackOrder.Filename, MediaFilter.Images);

        Assert.Equal(1, playlist.CurrentPosition);
        Assert.Equal(2, playlist.TotalCount);
        Assert.Equal("c.jpg", playlist.Next()!.Path);
        Assert.Equal(2, playlist.CurrentPosition);
    }

    [Fact]
    public void Preview_items_follow_playback_history_and_jump_without_reordering_it()
    {
        var playlist = new Playlist(
            [Image("a.jpg"), Image("b.jpg"), Image("c.jpg"), Image("d.jpg")],
            PlaybackOrder.Filename,
            includeVideos: true);

        Assert.Equal("a.jpg", playlist.Next()!.Path);
        Assert.Equal("b.jpg", playlist.Next()!.Path);
        Assert.Equal("c.jpg", playlist.Next()!.Path);

        Assert.Equal(["b.jpg", "a.jpg"], playlist.PreviewPreviousItems(3).Select(item => item.Path));
        Assert.Equal(["d.jpg"], playlist.PreviewUpcomingItems(3).Select(item => item.Path));

        Assert.Equal("a.jpg", playlist.JumpTo("A.JPG")!.Path);
        Assert.Equal("a.jpg", playlist.Current!.Path);
        Assert.Equal(["b.jpg", "c.jpg", "d.jpg"], playlist.PreviewUpcomingItems(3).Select(item => item.Path));
        Assert.Equal("b.jpg", playlist.Next()!.Path);
    }

    [Fact]
    public void Preview_items_obey_the_active_media_filter()
    {
        var playlist = new Playlist(
            [Image("a.jpg"), Video("b.mp4"), Image("c.jpg")],
            PlaybackOrder.Filename,
            MediaFilter.Both);
        playlist.Next();
        playlist.Next();
        playlist.SetOptions(PlaybackOrder.Filename, MediaFilter.Images);

        Assert.Equal(["a.jpg"], playlist.PreviewPreviousItems(3).Select(item => item.Path));
        Assert.Equal(["c.jpg"], playlist.PreviewUpcomingItems(3).Select(item => item.Path));
        Assert.Null(playlist.JumpTo("b.mp4"));
    }

    [Fact]
    public void Empty_playlist_reports_no_current_item_and_returns_null()
    {
        var playlist = new Playlist([], PlaybackOrder.Random, includeVideos: true);

        Assert.Null(playlist.Current);
        Assert.Equal(0, playlist.CurrentPosition);
        Assert.False(playlist.CanGoBack);
        Assert.Null(playlist.Next());
        Assert.Null(playlist.StartRandom());
    }
}
