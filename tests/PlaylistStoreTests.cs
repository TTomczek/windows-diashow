using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;

namespace diashow.Tests;

public sealed class PlaylistStoreTests
{
    [Fact]
    public void Path_identity_is_versioned_case_insensitive_and_rejects_out_of_scope_paths()
    {
        var root = Path.Combine(Path.GetTempPath(), "playlist-root");
        var item = Path.Combine(root, "Photos", "Image.jpg");

        Assert.StartsWith("root-v1:", PlaylistPathIdentity.RootHash(root));
        Assert.StartsWith("item-v1:", PlaylistPathIdentity.RelativePathHash(root, item));
        Assert.Equal(PlaylistPathIdentity.RootHash(root), PlaylistPathIdentity.RootHash(root.ToUpperInvariant()));
        Assert.Equal(
            PlaylistPathIdentity.RelativePathHash(root, item),
            PlaylistPathIdentity.RelativePathHash(root, item.ToUpperInvariant()));
        Assert.False(PlaylistPathIdentity.TryGetRelativePath(
            root, Path.Combine(root + "-sibling", "image.jpg"), out _));
    }

    [Fact]
    public void Store_persists_membership_hashes_not_folder_or_file_paths()
    {
        using var sandbox = new PlaylistSandbox();
        var path = sandbox.CreateFile("private-photo.jpg");
        var manager = sandbox.CreateManager();
        manager.LoadFolder(sandbox.Root);
        var id = manager.Create("Summer trip", PlaylistEndMode.LoopPlaylist);

        manager.UpdateMembership(id, [path], add: true);

        var json = File.ReadAllText(sandbox.StorePath);
        Assert.Contains("Summer trip", json);
        Assert.Contains("\"SchemaVersion\": 2", json);
        Assert.DoesNotContain(sandbox.Root, json);
        Assert.DoesNotContain("private-photo.jpg", json);
    }

    [Fact]
    public void Favorite_always_exists_and_is_scoped_to_each_folder()
    {
        using var sandbox = new PlaylistSandbox();
        var path = sandbox.CreateFile("favorite.png");
        var manager = sandbox.CreateManager();
        manager.LoadFolder(sandbox.Root);
        manager.SetFavorite(path, true);
        manager.CompleteScan(sandbox.Root);

        var otherFolder = Path.Combine(sandbox.Parent, "other-folder");
        Directory.CreateDirectory(otherFolder);
        Assert.Single(manager.LoadFolder(otherFolder), playlist => playlist.IsFavorite);
        Assert.Equal(0, manager.List().Single(playlist => playlist.IsFavorite).ItemCount);

        manager.LoadFolder(sandbox.Root);
        var favorite = manager.List(scannedItems: [MediaDiscovery.CreateItem(path)])
            .Single(playlist => playlist.IsFavorite);
        Assert.Equal(1, favorite.ItemCount);
        Assert.True(manager.IsFavorite(path));
    }

    [Fact]
    public void Duplicate_members_are_idempotent_and_stale_entries_can_be_cleaned()
    {
        using var sandbox = new PlaylistSandbox();
        var path = sandbox.CreateFile("stale.png");
        var manager = sandbox.CreateManager();
        manager.LoadFolder(sandbox.Root);
        var id = manager.Create("Custom", PlaylistEndMode.LoopPlaylist);

        manager.UpdateMembership(id, [path, path], add: true);
        manager.CompleteScan(sandbox.Root);
        var active = manager.List(scannedItems: [MediaDiscovery.CreateItem(path)])
            .Single(item => item.Id == id);
        Assert.Equal(1, active.ItemCount);
        Assert.Equal(0, active.StaleItemCount);

        File.Delete(path);
        var stale = manager.List(scannedItems: []).Single(item => item.Id == id);
        Assert.Equal(0, stale.ItemCount);
        Assert.Equal(1, stale.StaleItemCount);

        File.WriteAllText(path, "updated-media");
        manager.UpdateMembership(id, [path], add: true);
        var refreshed = manager.List(scannedItems: [MediaDiscovery.CreateItem(path)])
            .Single(item => item.Id == id);
        Assert.Equal(1, refreshed.ItemCount);
        Assert.Equal(0, refreshed.StaleItemCount);

        File.Delete(path);
        manager.CleanupStale(id, []);
        var cleaned = manager.List(scannedItems: []).Single(item => item.Id == id);
        Assert.Equal(0, cleaned.StaleItemCount);
    }

    [Fact]
    public void External_playlists_are_owner_scoped_until_the_app_shares_them()
    {
        using var sandbox = new PlaylistSandbox();
        var path = sandbox.CreateFile("shared.png");
        var manager = sandbox.CreateManager();
        manager.LoadFolder(sandbox.Root);
        var id = manager.Create("Client list", PlaylistEndMode.LoopPlaylist, "client-a");

        Assert.Throws<PlaylistRequestException>(() =>
            manager.Rename(id, "Renamed", "client-b"));

        manager.SetShared(id, true);
        manager.UpdateMembership(id, [path], add: true, clientId: "client-b");
        Assert.Contains(manager.List("client-b", [MediaDiscovery.CreateItem(path)]),
            playlist => playlist.Id == id && playlist.ItemCount == 1);
    }

    [Fact]
    public void Malformed_and_out_of_scope_membership_requests_are_rejected()
    {
        using var sandbox = new PlaylistSandbox();
        var manager = sandbox.CreateManager();
        manager.LoadFolder(sandbox.Root);
        var id = manager.Create("Custom", PlaylistEndMode.LoopPlaylist);
        var outside = sandbox.CreateOutsideFile("outside.png");

        var error = Assert.Throws<PlaylistRequestException>(() =>
            manager.UpdateMembership(id, [outside], add: true));

        Assert.Equal("out_of_scope", error.Code);
    }

    [Fact]
    public void Version_zero_store_is_migrated_to_the_current_schema()
    {
        using var sandbox = new PlaylistSandbox();
        File.WriteAllText(sandbox.StorePath, """{"SchemaVersion":0,"Folders":[]}""");
        var manager = sandbox.CreateManager();

        manager.LoadFolder(sandbox.Root);

        Assert.Contains(
            "\"SchemaVersion\": 2",
            File.ReadAllText(sandbox.StorePath),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Unsupported_future_schema_is_not_overwritten()
    {
        using var sandbox = new PlaylistSandbox();
        File.WriteAllText(sandbox.StorePath, """{"SchemaVersion":99,"Folders":[]}""");
        var manager = sandbox.CreateManager();

        Assert.Throws<InvalidDataException>(() => manager.LoadFolder(sandbox.Root));
        Assert.Contains("\"SchemaVersion\":99", File.ReadAllText(sandbox.StorePath));
    }

    [Fact]
    public async Task Concurrent_store_updates_keep_all_playlist_records_and_valid_json()
    {
        using var sandbox = new PlaylistSandbox();
        var first = sandbox.CreateManager();
        var second = sandbox.CreateManager();
        first.LoadFolder(sandbox.Root);
        second.LoadFolder(sandbox.Root);

        var writes = Enumerable.Range(0, 20)
            .Select(index => Task.Run(() =>
                (index % 2 == 0 ? first : second)
                .Create($"Playlist {index}", PlaylistEndMode.LoopPlaylist)));
        var ids = await Task.WhenAll(writes);

        var playlists = first.List().Where(playlist => !playlist.IsFavorite).ToArray();
        Assert.Equal(20, ids.Distinct().Count());
        Assert.Equal(20, playlists.Length);
        using var persisted = JsonDocument.Parse(File.ReadAllText(sandbox.StorePath));
        Assert.Equal(
            PlaylistStoreDocument.CurrentSchemaVersion,
            persisted.RootElement.GetProperty("SchemaVersion").GetInt32());
    }

    [Fact]
    public async Task Named_pipe_requires_an_app_issued_capability()
    {
        using var sandbox = new PlaylistSandbox();
        var manager = sandbox.CreateManager();
        var firstPath = sandbox.CreateFile("first.jpg");
        var secondPath = sandbox.CreateFile("second.jpg");
        MediaItem[] scannedItems = [MediaDiscovery.CreateItem(firstPath), MediaDiscovery.CreateItem(secondPath)];
        manager.LoadFolder(sandbox.Root, scannedItems);
        using var server = new PlaylistIpcServer(manager, () => scannedItems);
        var issued = server.IssueCapability();
        var playlistId = manager.Create(
            "External list", PlaylistEndMode.LoopPlaylist, issued.ClientId);
        manager.UpdateMembership(
            playlistId, [firstPath, secondPath], add: true, clientId: issued.ClientId);

        var unauthorized = await SendRequestAsync(new PlaylistIpcRequest(
            1, "list", "not-a-valid-key"));
        Assert.False(unauthorized.Success);
        Assert.Equal("unauthorized", unauthorized.Error);

        var authorized = await SendRequestAsync(new PlaylistIpcRequest(
            1, "list", issued.Capability));
        Assert.True(authorized.Success);
        var listedPlaylist = Assert.Single(authorized.Playlists!);
        Assert.Equal(2, listedPlaylist.ItemCount);
        Assert.Empty(listedPlaylist.Items);

        var firstPage = await SendRequestAsync(new PlaylistIpcRequest(
            1, "inspect", issued.Capability, playlistId, Offset: 0, Limit: 1));
        Assert.Equal(1, firstPage.NextOffset);
        Assert.Equal(["first.jpg"], firstPage.Playlist!.Items);
        var secondPage = await SendRequestAsync(new PlaylistIpcRequest(
            1, "inspect", issued.Capability, playlistId, Offset: 1, Limit: 1));
        Assert.Null(secondPage.NextOffset);
        Assert.Equal(["second.jpg"], secondPage.Playlist!.Items);
    }

    private static async Task<PlaylistIpcResponse> SendRequestAsync(PlaylistIpcRequest request)
    {
        await using var pipe = new NamedPipeClientStream(
            ".", PlaylistIpcServer.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await pipe.WriteAsync(header);
        await pipe.WriteAsync(payload);
        await pipe.FlushAsync();

        await pipe.ReadExactlyAsync(header);
        var responseLength = BinaryPrimitives.ReadInt32BigEndian(header);
        var response = new byte[responseLength];
        await pipe.ReadExactlyAsync(response);
        return JsonSerializer.Deserialize<PlaylistIpcResponse>(
            response, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private sealed class PlaylistSandbox : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(), $"diashow-playlists-{Guid.NewGuid():N}");

        public string Root => Path.Combine(_directory, "private-root");
        public string Parent => _directory;
        public string StorePath => Path.Combine(_directory, "store.json");

        public PlaylistSandbox()
        {
            Directory.CreateDirectory(Root);
        }

        public PlaylistManager CreateManager() => new(new FilePlaylistStore(StorePath));

        public string CreateFile(string name)
        {
            var path = Path.Combine(Root, name);
            File.WriteAllText(path, "media");
            return path;
        }

        public string CreateOutsideFile(string name)
        {
            var path = Path.Combine(_directory, name);
            File.WriteAllText(path, "media");
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
    }
}
