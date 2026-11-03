using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace diashow;

public sealed record PlaylistIpcRequest(
    int Version,
    string Operation,
    string Capability,
    string? PlaylistId = null,
    string? Name = null,
    string[]? Paths = null,
    PlaylistEndMode? EndMode = null);

public sealed record PlaylistIpcResponse(
    int Version,
    bool Success,
    string? Error = null,
    string? Message = null,
    IReadOnlyList<PlaylistView>? Playlists = null,
    PlaylistView? Playlist = null);

public sealed class PlaylistIpcServer : IDisposable
{
    public const string PipeName = "Diashow.Playlists.v1";
    private const int MaximumMessageLength = 64 * 1024;
    private const int MaximumConcurrentClients = 8;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PlaylistManager _manager;
    private readonly Func<IEnumerable<MediaItem>> _scannedItems;
    private readonly ConcurrentDictionary<string, string> _capabilities = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _connections = new(MaximumConcurrentClients);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _serverTask;

    public PlaylistIpcServer(PlaylistManager manager, Func<IEnumerable<MediaItem>> scannedItems)
    {
        _manager = manager;
        _scannedItems = scannedItems;
        _serverTask = RunAsync(_cancellation.Token);
    }

    public (string ClientId, string Capability) IssueCapability()
    {
        var clientId = Guid.NewGuid().ToString("N");
        var capability = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _capabilities[HashCapability(capability)] = clientId;
        return (clientId, capability);
    }

    public void RevokeCapabilities() => _capabilities.Clear();

    public void Dispose()
    {
        _cancellation.Cancel();
        try
        {
            _serverTask.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        _cancellation.Dispose();
        _connections.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                MaximumConcurrentClients,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                if (!_connections.Wait(0))
                {
                    pipe.Dispose();
                    continue;
                }
                _ = HandleConnectionAsync(pipe, cancellationToken);
            }
            catch
            {
                pipe.Dispose();
                if (cancellationToken.IsCancellationRequested)
                    return;
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleConnectionAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        using (pipe)
        {
            try
            {
                var header = new byte[sizeof(int)];
                await pipe.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
                var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header);
                if (length is <= 0 or > MaximumMessageLength)
                {
                    await WriteResponseAsync(pipe, Error("invalid_request", "Invalid message length."),
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                var payload = new byte[length];
                await pipe.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
                PlaylistIpcRequest? request;
                try
                {
                    request = JsonSerializer.Deserialize<PlaylistIpcRequest>(payload, JsonOptions);
                }
                catch (JsonException)
                {
                    request = null;
                }

                var response = request is null
                    ? Error("invalid_request", "The request is not valid JSON.")
                    : HandleRequest(request);
                await WriteResponseAsync(pipe, response, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException)
            {
            }
            finally
            {
                _connections.Release();
            }
        }
    }

    private PlaylistIpcResponse HandleRequest(PlaylistIpcRequest request)
    {
        if (request.Version != 1)
            return Error("unsupported_version", "Only protocol version 1 is supported.");
        if (request.Capability is null || !TryGetClientId(request.Capability, out var clientId))
            return Error("unauthorized", "The capability is invalid or has been revoked.");

        var root = _manager.CurrentFolder;
        if (root is null)
            return Error("no_folder", "No folder is currently loaded.");

        try
        {
            var items = _scannedItems();
            switch (request.Operation)
            {
                case "list":
                    return new PlaylistIpcResponse(
                        1, true, Playlists: _manager.List(clientId, items));
                case "inspect":
                {
                    var playlist = FindVisiblePlaylist(request.PlaylistId, clientId, items);
                    return new PlaylistIpcResponse(1, true, Playlist: playlist);
                }
                case "create":
                {
                    var id = _manager.Create(
                        request.Name ?? string.Empty,
                        request.EndMode ?? PlaylistEndMode.LoopPlaylist,
                        clientId);
                    return new PlaylistIpcResponse(1, true, Playlist: FindVisiblePlaylist(id, clientId, items));
                }
                case "rename":
                    _manager.Rename(
                        RequirePlaylistId(request.PlaylistId),
                        request.Name ?? string.Empty,
                        clientId);
                    return new PlaylistIpcResponse(
                        1, true, Playlist: FindVisiblePlaylist(request.PlaylistId, clientId, items));
                case "delete":
                    _manager.Delete(RequirePlaylistId(request.PlaylistId), clientId);
                    return new PlaylistIpcResponse(1, true);
                case "add":
                case "remove":
                {
                    var paths = ResolvePaths(root, request.Paths);
                    _manager.UpdateMembership(
                        root,
                        RequirePlaylistId(request.PlaylistId),
                        paths,
                        request.Operation == "add",
                        clientId);
                    return new PlaylistIpcResponse(
                        1, true, Playlist: FindVisiblePlaylist(request.PlaylistId, clientId, items));
                }
                case "set_end_mode":
                    if (request.EndMode is not { } endMode || !Enum.IsDefined(endMode))
                        return Error("invalid_request", "A valid playlist end mode is required.");
                    _manager.SetEndMode(
                        RequirePlaylistId(request.PlaylistId), endMode, clientId);
                    return new PlaylistIpcResponse(
                        1, true, Playlist: FindVisiblePlaylist(request.PlaylistId, clientId, items));
                default:
                    return Error("invalid_request", "The requested operation is not supported.");
            }
        }
        catch (PlaylistRequestException exception)
        {
            return Error(exception.Code, exception.Message);
        }
        catch (ArgumentException exception)
        {
            return Error("invalid_request", exception.Message);
        }
    }

    private PlaylistView FindVisiblePlaylist(
        string? playlistId,
        string clientId,
        IEnumerable<MediaItem> items)
    {
        var id = RequirePlaylistId(playlistId);
        return _manager.List(clientId, items).FirstOrDefault(playlist => playlist.Id == id)
            ?? throw new PlaylistRequestException("not_found", "The requested playlist does not exist.");
    }

    private static string RequirePlaylistId(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? throw new PlaylistRequestException("invalid_request", "A playlist ID is required.")
            : id;

    private static IReadOnlyList<string> ResolvePaths(string root, string[]? paths)
    {
        if (paths is null || paths.Length > 10_000)
            throw new PlaylistRequestException("invalid_request", "A list of at most 10,000 paths is required.");

        var resolved = new List<string>(paths.Length);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
                throw new PlaylistRequestException("invalid_request", "Paths must be folder-relative.");

            var fullPath = Path.GetFullPath(Path.Combine(root, path));
            if (!PlaylistPathIdentity.TryGetRelativePath(root, fullPath, out _))
                throw new PlaylistRequestException("out_of_scope", "A path is outside the loaded folder.");
            resolved.Add(fullPath);
        }
        return resolved;
    }

    private bool TryGetClientId(string capability, out string clientId)
    {
        clientId = string.Empty;
        var candidate = Convert.FromHexString(HashCapability(capability));
        foreach (var entry in _capabilities)
        {
            var stored = Convert.FromHexString(entry.Key);
            if (CryptographicOperations.FixedTimeEquals(candidate, stored))
            {
                clientId = entry.Value;
                return true;
            }
        }
        return false;
    }

    private static string HashCapability(string capability) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(capability)));

    private static PlaylistIpcResponse Error(string error, string message) =>
        new(1, false, error, message);

    private static async Task WriteResponseAsync(
        Stream pipe,
        PlaylistIpcResponse response,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
        if (payload.Length > MaximumMessageLength)
            payload = JsonSerializer.SerializeToUtf8Bytes(
                Error("response_too_large", "The response exceeds the protocol size limit."), JsonOptions);

        var header = new byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await pipe.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await pipe.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
