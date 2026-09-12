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
