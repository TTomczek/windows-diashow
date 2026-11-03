using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace diashow;

public enum PlaylistEndMode
{
    LoopPlaylist,
    AppendNonPlaylistItems
}

public sealed class PlaylistStoreDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<StoredFolderPlaylists> Folders { get; set; } = [];
}

public sealed class StoredFolderPlaylists
{
    public string RootHash { get; set; } = string.Empty;
    public List<StoredPlaylist> Playlists { get; set; } = [];
}

public sealed class StoredPlaylist
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public PlaylistEndMode EndMode { get; set; }
    public string OwnerId { get; set; } = "local";
    public bool SharedWithClients { get; set; }
    public List<StoredPlaylistMembership> Members { get; set; } = [];
}

public sealed record StoredPlaylistMembership(
    string PathHash,
    long FileLength,
    long LastWriteUtcTicks);

public sealed record PlaylistView(
    string Id,
    string Name,
    PlaylistEndMode EndMode,
    bool IsFavorite,
    bool SharedWithClients,
    int ItemCount,
    int StaleItemCount,
    IReadOnlyList<string> Items);

public interface IPlaylistStore
{
    PlaylistStoreDocument Read();
    void Update(Action<PlaylistStoreDocument> update);
}

public sealed class FilePlaylistStore : IPlaylistStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _filePath;
    private readonly string _mutexName;

    public FilePlaylistStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Diashow",
            "playlists.json");
        var user = OperatingSystem.IsWindows()
            ? WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName
            : Environment.UserName;
        var userHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user)))[..16];
        _mutexName = OperatingSystem.IsWindows()
            ? $"Local\\Diashow.PlaylistStore.{userHash}"
            : $"Diashow.PlaylistStore.{userHash}";
    }

    public PlaylistStoreDocument Read() => WithLock(Load);

    public void Update(Action<PlaylistStoreDocument> update) =>
        WithLock(() =>
        {
            var document = Load();
            update(document);
            document.SchemaVersion = 1;
            Save(document);
            return true;
        });

    private T WithLock<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, _mutexName);
        try
        {
            mutex.WaitOne();
        }
        catch (AbandonedMutexException)
        {
        }

        try
        {
            return action();
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private PlaylistStoreDocument Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return new PlaylistStoreDocument();

            var document = JsonSerializer.Deserialize<PlaylistStoreDocument>(
                File.ReadAllText(_filePath), JsonOptions);
            if (document is { SchemaVersion: 1, Folders: not null })
                return document;
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return new PlaylistStoreDocument();
    }

    private void Save(PlaylistStoreDocument document)
    {
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("The playlist store must have a parent directory.");
        Directory.CreateDirectory(directory);
        RestrictToCurrentUser(directory, isDirectory: true);

        var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonOptions));
            RestrictToCurrentUser(temporaryPath, isDirectory: false);
            File.Move(temporaryPath, _filePath, overwrite: true);
            RestrictToCurrentUser(_filePath, isDirectory: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static void RestrictToCurrentUser(string path, bool isDirectory)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var user = WindowsIdentity.GetCurrent().User;
        if (user is null)
            return;

        var inheritance = isDirectory
            ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
            : InheritanceFlags.None;
        if (isDirectory)
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                user,
                FileSystemRights.FullControl,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
        }
        else
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                user,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
    }
}

public static class PlaylistPathIdentity
{
    public static string RootHash(string rootFolder) =>
        Hash("root-v1:" + Normalize(Path.GetFullPath(rootFolder)));

    public static string RelativePathHash(string rootFolder, string path)
    {
        if (!TryGetRelativePath(rootFolder, path, out var relativePath))
            throw new ArgumentException("The path is outside the scanned folder.", nameof(path));
        return Hash("item-v1:" + NormalizeRelative(relativePath));
    }

    public static bool TryGetRelativePath(string rootFolder, string path, out string relativePath)
    {
        relativePath = string.Empty;
        if (string.IsNullOrWhiteSpace(rootFolder) || string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootFolder));
            var fullPath = Path.GetFullPath(path);
            var candidate = Path.GetRelativePath(root, fullPath);
            if (Path.IsPathRooted(candidate) ||
                candidate == "." ||
                candidate.Equals("..", StringComparison.Ordinal) ||
                candidate.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                candidate.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
                return false;

            relativePath = candidate;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
                                           NotSupportedException)
        {
            return false;
        }
    }

    public static (long Length, long LastWriteUtcTicks) GetMetadata(string path)
    {
        var info = new FileInfo(path);
        info.Refresh();
        if (!info.Exists)
            throw new FileNotFoundException("The playlist item no longer exists.", path);
        return (info.Length, info.LastWriteTimeUtc.Ticks);
    }

    private static string Normalize(string path) =>
        path.Replace('/', '\\').TrimEnd('\\').ToUpperInvariant();

    private static string NormalizeRelative(string path) =>
        path.Replace('/', '\\').ToUpperInvariant();

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed class PlaylistManager
{
    public const string FavoriteId = "favorite";
    public const string LocalOwnerId = "local";
    private readonly IPlaylistStore _store;
    private string? _currentFolder;

    public PlaylistManager(IPlaylistStore store) => _store = store;

    public string? CurrentFolder => Volatile.Read(ref _currentFolder);

    public IReadOnlyList<PlaylistView> LoadFolder(string rootFolder, IEnumerable<MediaItem>? scannedItems = null)
    {
        var folder = Path.GetFullPath(rootFolder);
        Volatile.Write(ref _currentFolder, folder);
        var rootHash = PlaylistPathIdentity.RootHash(folder);
        _store.Update(document =>
        {
            var data = GetFolder(document, rootHash, create: true)!;
            EnsureFavorite(data);
        });
        return List(folder, scannedItems ?? []);
    }

    public IReadOnlyList<PlaylistView> List(
        string? clientId = null,
        IEnumerable<MediaItem>? scannedItems = null)
    {
        var folder = CurrentFolder;
        if (folder is null)
            return [];
        return List(folder, scannedItems ?? [], clientId);
    }

    public IReadOnlyList<PlaylistView> List(
        string rootFolder,
        IEnumerable<MediaItem> scannedItems,
        string? clientId = null)
    {
        var rootHash = PlaylistPathIdentity.RootHash(rootFolder);
        var items = BuildScannedItems(rootFolder, scannedItems);
        var folder = _store.Read().Folders.FirstOrDefault(entry => entry.RootHash == rootHash);
        var records = folder?.Playlists ?? [CreateFavorite()];
        return records
            .Where(record => clientId is null || CanAccess(record, clientId))
            .Select(record => ToView(rootFolder, record, items))
            .OrderByDescending(view => view.IsFavorite)
            .ThenBy(view => view.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public string Create(string name, PlaylistEndMode endMode, string ownerId = LocalOwnerId)
    {
        var normalizedName = ValidateName(name);
        var folder = RequireCurrentFolder();
        var id = Guid.NewGuid().ToString("N");
        _store.Update(document =>
        {
            var data = GetFolder(document, PlaylistPathIdentity.RootHash(folder), create: true)!;
            EnsureFavorite(data);
            EnsureUniqueName(data, normalizedName, null);
            data.Playlists.Add(new StoredPlaylist
            {
                Id = id,
                Name = normalizedName,
                EndMode = endMode,
                OwnerId = ownerId
            });
        });
        return id;
    }

    public void Rename(string id, string name, string clientId = LocalOwnerId)
    {
        if (id == FavoriteId)
            throw new PlaylistRequestException("invalid_operation", "Favorite cannot be renamed.");
        var normalizedName = ValidateName(name);
        UpdatePlaylist(id, clientId, (data, playlist) =>
        {
            EnsureUniqueName(data, normalizedName, id);
            playlist.Name = normalizedName;
        });
    }

    public void Delete(string id, string clientId = LocalOwnerId)
    {
        if (id == FavoriteId)
            throw new PlaylistRequestException("invalid_operation", "Favorite cannot be deleted.");
        var folder = RequireCurrentFolder();
        _store.Update(document =>
        {
            var data = GetFolder(document, PlaylistPathIdentity.RootHash(folder), create: true)!;
            var playlist = FindPlaylist(data, id);
            EnsureOwner(playlist, clientId, forWrite: true);
            data.Playlists.Remove(playlist);
        });
    }

    public void SetShared(string id, bool shared) =>
        UpdatePlaylist(id, LocalOwnerId, (_, playlist) => playlist.SharedWithClients = shared);

    public void SetEndMode(string id, PlaylistEndMode endMode, string clientId = LocalOwnerId) =>
        UpdatePlaylist(id, clientId, (_, playlist) => playlist.EndMode = endMode);

    public bool IsFavorite(string path)
    {
        var folder = RequireCurrentFolder();
        var hash = PlaylistPathIdentity.RelativePathHash(folder, path);
        var favorite = GetCurrentPlaylists().FirstOrDefault(item => item.Id == FavoriteId);
        return favorite?.Members.Any(member => member.PathHash == hash) == true;
    }

    public void SetFavorite(string path, bool isFavorite)
    {
        var folder = RequireCurrentFolder();
        UpdateMembership(folder, FavoriteId, [path], add: isFavorite, LocalOwnerId);
    }

    public void UpdateMembership(
        string playlistId,
        IEnumerable<string> paths,
        bool add,
        string clientId = LocalOwnerId)
    {
        var folder = RequireCurrentFolder();
        UpdateMembership(folder, playlistId, paths, add, clientId);
    }

    public void UpdateMembership(
        string rootFolder,
        string playlistId,
        IEnumerable<string> paths,
        bool add,
        string clientId)
    {
        var folder = Path.GetFullPath(rootFolder);
        var members = paths.Select(path =>
        {
            if (!PlaylistPathIdentity.TryGetRelativePath(folder, path, out _))
                throw new PlaylistRequestException("out_of_scope", "A path is outside the loaded folder.");
            if (add)
            {
                try
                {
                    var metadata = PlaylistPathIdentity.GetMetadata(path);
                    return new StoredPlaylistMembership(
                        PlaylistPathIdentity.RelativePathHash(folder, path),
                        metadata.Length,
                        metadata.LastWriteUtcTicks);
                }
                catch (FileNotFoundException exception)
                {
                    throw new PlaylistRequestException("stale", exception.Message);
                }
            }

            return new StoredPlaylistMembership(
                PlaylistPathIdentity.RelativePathHash(folder, path), 0, 0);
        }).ToArray();

        var rootHash = PlaylistPathIdentity.RootHash(folder);
        _store.Update(document =>
        {
            var data = GetFolder(document, rootHash, create: true)!;
            EnsureFavorite(data);
            var playlist = FindPlaylist(data, playlistId);
            EnsureOwner(playlist, clientId, forWrite: true);
            if (add)
            {
                var existing = playlist.Members.Select(member => member.PathHash)
                    .ToHashSet(StringComparer.Ordinal);
                playlist.Members.AddRange(members.Where(member => existing.Add(member.PathHash)));
            }
            else
            {
                var remove = members.Select(member => member.PathHash)
                    .ToHashSet(StringComparer.Ordinal);
                playlist.Members.RemoveAll(member => remove.Contains(member.PathHash));
            }
        });
    }

    public HashSet<string> GetPlayablePaths(
        string playlistId,
        IEnumerable<MediaItem> scannedItems)
    {
        var folder = RequireCurrentFolder();
        var stored = GetCurrentPlaylists().FirstOrDefault(item => item.Id == playlistId);
        var hashes = stored?.Members.ToDictionary(
            member => member.PathHash,
            member => (member.FileLength, member.LastWriteUtcTicks),
            StringComparer.Ordinal) ?? [];
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in scannedItems)
        {
            if (!PlaylistPathIdentity.TryGetRelativePath(folder, item.Path, out _))
                continue;
            var hash = PlaylistPathIdentity.RelativePathHash(folder, item.Path);
            if (!hashes.TryGetValue(hash, out var metadata))
                continue;
            try
            {
                var current = PlaylistPathIdentity.GetMetadata(item.Path);
                if (metadata.FileLength == current.Length &&
                    metadata.LastWriteUtcTicks == current.LastWriteUtcTicks)
                    result.Add(item.Path);
            }
            catch (FileNotFoundException)
            {
            }
        }
        return result;
    }

    public void CleanupStale(string id, IEnumerable<MediaItem> scannedItems)
    {
        var playableHashes = GetPlayablePaths(id, scannedItems)
            .Select(path => PlaylistPathIdentity.RelativePathHash(RequireCurrentFolder(), path))
            .ToHashSet(StringComparer.Ordinal);
        UpdatePlaylist(id, LocalOwnerId, (_, playlist) =>
            playlist.Members.RemoveAll(member => !playableHashes.Contains(member.PathHash)));
    }

    public string? GetRelativePathForMember(string playlistId, string path) =>
        PlaylistPathIdentity.TryGetRelativePath(RequireCurrentFolder(), path, out var relative)
            ? relative
            : null;

    private IReadOnlyList<PlaylistView> List(
        string rootFolder,
        IEnumerable<MediaItem> scannedItems) => List(rootFolder, scannedItems, null);

    private Dictionary<string, (MediaItem Item, long Length, long LastWriteUtcTicks)> BuildScannedItems(
        string rootFolder,
        IEnumerable<MediaItem> scannedItems)
    {
        var result = new Dictionary<string, (MediaItem, long, long)>(StringComparer.Ordinal);
        foreach (var item in scannedItems)
        {
            if (!PlaylistPathIdentity.TryGetRelativePath(rootFolder, item.Path, out _))
                continue;
            try
            {
                var metadata = PlaylistPathIdentity.GetMetadata(item.Path);
                result[PlaylistPathIdentity.RelativePathHash(rootFolder, item.Path)] =
                    (item, metadata.Length, metadata.LastWriteUtcTicks);
            }
            catch (FileNotFoundException)
            {
            }
        }
        return result;
    }

    private static PlaylistView ToView(
        string rootFolder,
        StoredPlaylist playlist,
        Dictionary<string, (MediaItem Item, long Length, long LastWriteUtcTicks)> scannedItems)
    {
        var paths = new List<string>();
        var staleCount = 0;
        foreach (var member in playlist.Members)
        {
            if (!scannedItems.TryGetValue(member.PathHash, out var scanned) ||
                scanned.Length != member.FileLength ||
                scanned.LastWriteUtcTicks != member.LastWriteUtcTicks)
            {
                staleCount++;
                continue;
            }
            if (PlaylistPathIdentity.TryGetRelativePath(rootFolder, scanned.Item.Path, out var relative))
                paths.Add(relative);
        }
        return new PlaylistView(
            playlist.Id,
            playlist.Id == FavoriteId ? Localization.Get("Favorite") : playlist.Name,
            playlist.EndMode,
            playlist.Id == FavoriteId,
            playlist.SharedWithClients,
            paths.Count,
            staleCount,
            paths);
    }

    private void UpdatePlaylist(
        string id,
        string clientId,
        Action<StoredFolderPlaylists, StoredPlaylist> update)
    {
        var folder = RequireCurrentFolder();
        _store.Update(document =>
        {
            var data = GetFolder(document, PlaylistPathIdentity.RootHash(folder), create: true)!;
            EnsureFavorite(data);
            var playlist = FindPlaylist(data, id);
            EnsureOwner(playlist, clientId, forWrite: true);
            update(data, playlist);
        });
    }

    private List<StoredPlaylist> GetCurrentPlaylists()
    {
        var folder = RequireCurrentFolder();
        var rootHash = PlaylistPathIdentity.RootHash(folder);
        return _store.Read().Folders.FirstOrDefault(entry => entry.RootHash == rootHash)?.Playlists ?? [];
    }

    private string RequireCurrentFolder() => CurrentFolder ??
        throw new PlaylistRequestException("no_folder", "No folder is currently loaded.");

    private static StoredFolderPlaylists? GetFolder(
        PlaylistStoreDocument document,
        string rootHash,
        bool create)
    {
        var folder = document.Folders.FirstOrDefault(entry => entry.RootHash == rootHash);
        if (folder is null && create)
        {
            folder = new StoredFolderPlaylists { RootHash = rootHash };
            document.Folders.Add(folder);
        }
        return folder;
    }

    private static void EnsureFavorite(StoredFolderPlaylists folder)
    {
        if (folder.Playlists.All(playlist => playlist.Id != FavoriteId))
            folder.Playlists.Insert(0, CreateFavorite());
    }

    private static StoredPlaylist CreateFavorite() => new()
    {
        Id = FavoriteId,
        Name = string.Empty,
        OwnerId = LocalOwnerId
    };

    private static StoredPlaylist FindPlaylist(StoredFolderPlaylists folder, string id) =>
        folder.Playlists.FirstOrDefault(playlist => playlist.Id == id)
        ?? throw new PlaylistRequestException("not_found", "The requested playlist does not exist.");

    private static bool CanAccess(StoredPlaylist playlist, string clientId) =>
        playlist.OwnerId == clientId || playlist.SharedWithClients;

    private static void EnsureOwner(StoredPlaylist playlist, string clientId, bool forWrite)
    {
        if (playlist.OwnerId != clientId && !(playlist.SharedWithClients && !forWrite))
            throw new PlaylistRequestException("unauthorized", "The client does not own this playlist.");
    }

    private static string ValidateName(string name)
    {
        var result = name?.Trim();
        if (string.IsNullOrWhiteSpace(result) || result.Length > 128 ||
            result.Any(char.IsControl) || result.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new PlaylistRequestException("invalid_request", "Playlist names must be 1-128 valid characters.");
        return result;
    }

    private static void EnsureUniqueName(StoredFolderPlaylists folder, string name, string? exceptId)
    {
        if (folder.Playlists.Any(playlist =>
                playlist.Id != exceptId && playlist.Id != FavoriteId &&
                string.Equals(playlist.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new PlaylistRequestException("name_conflict", "A playlist with that name already exists.");
    }
}

public sealed class PlaylistRequestException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public interface IPlaylistProvider
{
    int ContractVersion { get; }
    string ProviderId { get; }
    bool IsBuiltIn { get; }
}

public sealed class FavoritePlaylistProvider : IPlaylistProvider
{
    public int ContractVersion => 1;
    public string ProviderId => PlaylistManager.FavoriteId;
    public bool IsBuiltIn => true;
}

public sealed class ExternalPlaylistProvider : IPlaylistProvider
{
    public int ContractVersion => 1;
    public string ProviderId => "external";
    public bool IsBuiltIn => true;
}

public static class PlaylistProviders
{
    public static IReadOnlyList<IPlaylistProvider> BuiltIns { get; } =
        [new FavoritePlaylistProvider(), new ExternalPlaylistProvider()];
}
