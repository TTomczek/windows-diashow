using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace diashow;

public sealed class UpdateManager : IDisposable
{
    private const string Repository = "TTomczek/windows-diashow";
    private const string ReleaseApiUrl = $"https://api.github.com/repos/{Repository}/releases/latest";
    private const string UpdateDirectoryName = "Diashow";
    private const string SigningPublicKeyBase64 = "MCowBQYDK2VwAyEARxDBf3AQDYk0ZqBz/TpFSRfEc+T6nIK6fz/TTARZua4=";
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private const string OperationLockName = "Diashow.Update.SingleOperation";
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _stagedGate = new();
    private string? _stagedUpdate;
    private Task? _operation;

    public void Start()
    {
        if (_operation is not null)
            return;

        _stagedUpdate = FindStagedUpdate();
        _operation = CheckAndDownloadAsync(_shutdown.Token);
    }

    public void PrepareForExit()
    {
        _shutdown.Cancel();
        string? stagedUpdate;
        lock (_stagedGate)
        {
            stagedUpdate = _stagedUpdate;
            if (stagedUpdate is null)
                return;

            var updaterPath = Path.Combine(AppContext.BaseDirectory, "Diashow.Updater.exe");
            if (!File.Exists(updaterPath))
                return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = updaterPath,
                    Arguments = $"--target {Quote(Path.Combine(AppContext.BaseDirectory, "Diashow.exe"))} " +
                                $"--staged {Quote(stagedUpdate)} --pid {Environment.ProcessId}",
                    UseShellExecute = true,
                    WorkingDirectory = AppContext.BaseDirectory
                });
                _stagedUpdate = null;
            }
            catch (Exception exception) when (exception is InvalidOperationException or
                Win32Exception)
            {
                Trace.TraceError("Unable to start the updater: {0}", exception);
            }
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    private async Task CheckAndDownloadAsync(CancellationToken cancellationToken)
    {
        using var operationLock = new Semaphore(1, 1, OperationLockName);
        if (!operationLock.WaitOne(0))
            return;

        try
        {
            var installedVersion = GetInstalledVersion();
            using var releaseResponse = await HttpClient.GetAsync(
                ReleaseApiUrl, cancellationToken).ConfigureAwait(false);
            releaseResponse.EnsureSuccessStatusCode();
            await using var releaseStream = await releaseResponse.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var release = await JsonDocument.ParseAsync(
                releaseStream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var releaseInfo = ReleaseInfo.Parse(release.RootElement);
            if (!releaseInfo.Version.IsStable || releaseInfo.Version <= installedVersion)
                return;

            var updateDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                UpdateDirectoryName, "updates");
            Directory.CreateDirectory(updateDirectory);
            var token = cancellationToken;
            var temporaryExecutable = Path.Combine(updateDirectory, $"{Guid.NewGuid():N}.exe");
            var temporaryChecksum = Path.Combine(updateDirectory, $"{Guid.NewGuid():N}.sha256");
            var temporarySignature = Path.Combine(updateDirectory, $"{Guid.NewGuid():N}.sig");
            try
            {
                await DownloadAsync(releaseInfo.ExecutableUrl, temporaryExecutable, token)
                    .ConfigureAwait(false);
                await DownloadAsync(releaseInfo.ChecksumUrl, temporaryChecksum, token)
                    .ConfigureAwait(false);
                await DownloadAsync(releaseInfo.SignatureUrl, temporarySignature, token)
                    .ConfigureAwait(false);
                await VerifyAsync(
                    temporaryExecutable, temporaryChecksum, temporarySignature, token)
                    .ConfigureAwait(false);

                token.ThrowIfCancellationRequested();
                var stagedPath = Path.Combine(updateDirectory,
                    $"Diashow-{releaseInfo.Version}.exe");
                var stagedChecksumPath = stagedPath + ".sha256";
                var stagedSignaturePath = stagedPath + ".sig";
                lock (_stagedGate)
                {
                    if (token.IsCancellationRequested)
                        return;
                    File.Move(temporaryChecksum, stagedChecksumPath, true);
                    File.Move(temporarySignature, stagedSignaturePath, true);
                    File.Move(temporaryExecutable, stagedPath, true);
                    _stagedUpdate = stagedPath;
                }
            }
            finally
            {
                TryDelete(temporaryExecutable);
                TryDelete(temporaryChecksum);
                TryDelete(temporarySignature);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException or
            IOException or JsonException or CryptographicException or
            InvalidDataException or FormatException or ArgumentException or
            KeyNotFoundException or UriFormatException)
        {
            Trace.TraceError("Automatic update failed: {0}", exception);
        }
        finally
        {
            operationLock.Release();
        }
    }

    private static async Task VerifyAsync(
        string executablePath,
        string checksumPath,
        string signaturePath,
        CancellationToken cancellationToken)
    {
        var checksumBytes = await File.ReadAllBytesAsync(checksumPath, cancellationToken)
            .ConfigureAwait(false);
        var checksumText = Encoding.UTF8.GetString(checksumBytes);
        var checksum = ParseChecksum(checksumText);
        var signatureText = (await File.ReadAllTextAsync(signaturePath, cancellationToken)
            .ConfigureAwait(false)).Trim();
        var signature = Convert.FromBase64String(signatureText);
        var publicKey = Convert.FromBase64String(SigningPublicKeyBase64);
        if (publicKey.Length != 32 || signature.Length != 64 ||
            !Ed25519.Verify(signature, 0, publicKey, 0, checksumBytes, 0, checksumBytes.Length))
            throw new CryptographicException("The update signature is invalid.");

        await using var executable = File.OpenRead(executablePath);
        var actualHash = await SHA256.HashDataAsync(executable, cancellationToken)
            .ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(actualHash, checksum))
            throw new CryptographicException("The update checksum is invalid.");
    }

    private static void VerifyStagedUpdate(string executablePath)
    {
        var checksumBytes = File.ReadAllBytes(executablePath + ".sha256");
        var checksum = ParseChecksum(Encoding.UTF8.GetString(checksumBytes));
        var signatureText = File.ReadAllText(executablePath + ".sig").Trim();
        var signature = Convert.FromBase64String(signatureText);
        var publicKey = Convert.FromBase64String(SigningPublicKeyBase64);
        if (publicKey.Length != 32 || signature.Length != 64 ||
            !Ed25519.Verify(signature, 0, publicKey, 0, checksumBytes, 0, checksumBytes.Length))
            throw new CryptographicException("The staged update signature is invalid.");

        using var executable = File.OpenRead(executablePath);
        var actualHash = SHA256.HashData(executable);
        if (!CryptographicOperations.FixedTimeEquals(actualHash, checksum))
            throw new CryptographicException("The staged update checksum is invalid.");
    }

    private static byte[] ParseChecksum(string text)
    {
        var match = Regex.Match(text, @"\A([0-9a-fA-F]{64})  Diashow\.exe\r?\n?\z",
            RegexOptions.CultureInvariant);
        if (!match.Success)
            throw new InvalidDataException("The update checksum format is invalid.");
        return Convert.FromHexString(match.Groups[1].Value);
    }

    private static async Task DownloadAsync(
        Uri uri, string destination, CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync(
            uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var target = new FileStream(
            destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
    }

    private static string? FindStagedUpdate()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            UpdateDirectoryName, "updates");
        if (!Directory.Exists(directory))
            return null;

        var installedVersion = GetInstalledVersion();
        var candidates = new List<(string Path, SemanticVersion Version)>();
        foreach (var path in Directory.EnumerateFiles(directory, "Diashow-*.exe"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.StartsWith("Diashow-", StringComparison.OrdinalIgnoreCase) &&
                SemanticVersion.TryParse(name["Diashow-".Length..], out var version) &&
                version > installedVersion)
            {
                try
                {
                    VerifyStagedUpdate(path);
                    candidates.Add((path, version));
                }
                catch (Exception exception) when (exception is IOException or
                    UnauthorizedAccessException or CryptographicException or
                    InvalidDataException or FormatException)
                {
                    Trace.TraceError("Discarding invalid staged update {0}: {1}", path, exception);
                    TryDelete(path);
                    TryDelete(path + ".sha256");
                    TryDelete(path + ".sig");
                }
            }
        }

        return candidates.OrderByDescending(item => item.Version)
            .Select(item => item.Path).FirstOrDefault();
    }

    private static SemanticVersion GetInstalledVersion()
    {
        var version = typeof(UpdateManager).Assembly.GetName().Version;
        return new SemanticVersion(version?.Major ?? 0, version?.Minor ?? 0, version?.Build ?? 0, null);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Diashow", Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0"));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceError("Unable to delete update file {0}: {1}", path, exception);
        }
    }

    private sealed record ReleaseInfo(
        SemanticVersion Version,
        Uri ExecutableUrl,
        Uri ChecksumUrl,
        Uri SignatureUrl)
    {
        public static ReleaseInfo Parse(JsonElement root)
        {
            var tag = root.GetProperty("tag_name").GetString()
                ?? throw new InvalidDataException("The release has no version tag.");
            var version = SemanticVersion.Parse(tag);
            var assets = root.GetProperty("assets").EnumerateArray()
                .ToDictionary(item => item.GetProperty("name").GetString() ?? string.Empty,
                    item => item.GetProperty("browser_download_url").GetString()
                        ?? throw new InvalidDataException("The release asset has no URL."),
                    StringComparer.OrdinalIgnoreCase);
            return new ReleaseInfo(
                version,
                RequireHttps(assets["Diashow.exe"]),
                RequireHttps(assets["Diashow.exe.sha256"]),
                RequireHttps(assets["Diashow.exe.sig"]));
        }

        private static Uri RequireHttps(string value)
        {
            var uri = new Uri(value, UriKind.Absolute);
            return uri.Scheme == Uri.UriSchemeHttps
                ? uri
                : throw new InvalidDataException("Release assets must use HTTPS.");
        }
    }

    private readonly record struct SemanticVersion(
        int Major, int Minor, int Patch, string? PreRelease) : IComparable<SemanticVersion>
    {
        public bool IsStable => string.IsNullOrEmpty(PreRelease);

        public static SemanticVersion Parse(string value) =>
            TryParse(value, out var version)
                ? version
                : throw new FormatException($"Invalid semantic version: {value}");

        public static bool TryParse(string value, out SemanticVersion version)
        {
            var match = Regex.Match(value.Trim(), @"\Av?(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?\z");
            if (!match.Success ||
                !int.TryParse(match.Groups[1].Value, out var major) ||
                !int.TryParse(match.Groups[2].Value, out var minor) ||
                !int.TryParse(match.Groups[3].Value, out var patch))
            {
                version = default;
                return false;
            }

            version = new SemanticVersion(major, minor, patch,
                match.Groups[4].Success ? match.Groups[4].Value : null);
            return true;
        }

        public int CompareTo(SemanticVersion other)
        {
            var result = Major.CompareTo(other.Major);
            if (result != 0) return result;
            result = Minor.CompareTo(other.Minor);
            if (result != 0) return result;
            result = Patch.CompareTo(other.Patch);
            if (result != 0) return result;
            if (IsStable) return other.IsStable ? 0 : 1;
            if (other.IsStable) return -1;
            return string.CompareOrdinal(PreRelease, other.PreRelease);
        }

        public static bool operator >(SemanticVersion left, SemanticVersion right) =>
            left.CompareTo(right) > 0;
        public static bool operator <(SemanticVersion left, SemanticVersion right) =>
            left.CompareTo(right) < 0;
        public static bool operator <=(SemanticVersion left, SemanticVersion right) =>
            left.CompareTo(right) <= 0;
        public static bool operator >=(SemanticVersion left, SemanticVersion right) =>
            left.CompareTo(right) >= 0;

        public override string ToString() =>
            $"{Major}.{Minor}.{Patch}{(IsStable ? string.Empty : $"-{PreRelease}")}";
    }
}
