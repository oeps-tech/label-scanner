using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Scanner.Updates;

public sealed record InstalledVersion(string DirectoryName, string Version);
public sealed record UpdateState(InstalledVersion? Current = null, InstalledVersion? Previous = null);
public sealed record UpdateManifest(string Version, int RuntimeMajor, string Architecture, string Executable);
public sealed class IncompatibleUpdateException(string message) : Exception(message);

/// <summary>Immutable installed directories; the state changes only after the child reports startup ready.</summary>
public sealed class UpdateStore
{
    public const string ExecutableName = "Scanner.Server.exe";
    public const long MaximumPackageBytes = 4L * 1024 * 1024 * 1024;
    private const long MaximumExtractedBytes = 12L * 1024 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly string _root;
    public string VersionsDirectory => Path.Combine(_root, "versions");
    public string StagingDirectory => Path.Combine(_root, "staging");
    private string StatePath => Path.Combine(_root, "update-state.json");

    public UpdateStore(string userDataRoot)
    {
        _root = Path.GetFullPath(userDataRoot);
        Directory.CreateDirectory(_root);
        RejectReparsePoint(_root);
        Directory.CreateDirectory(VersionsDirectory);
        Directory.CreateDirectory(StagingDirectory);
        RejectReparsePoint(VersionsDirectory);
        RejectReparsePoint(StagingDirectory);
    }

    public UpdateState LoadState()
    {
        foreach (var path in new[] { StatePath, StatePath + ".bak" })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var state = JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(path), JsonOptions);
                if (state is null) continue;
                var current = IsValidInstalled(state.Current) ? state.Current : null;
                var previous = IsValidInstalled(state.Previous) ? state.Previous : null;
                if (current is not null || previous is not null) return new(current, previous);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException) { }
        }
        return new();
    }

    public string GetExecutablePath(InstalledVersion installed)
    {
        if (!IsSafeDirectoryName(installed.DirectoryName)) throw new InvalidDataException("Invalid installed version directory.");
        return Path.Combine(VersionsDirectory, installed.DirectoryName, "app", ExecutableName);
    }

    public bool IsValidInstalled(InstalledVersion? installed)
    {
        if (installed is null || !IsSafeDirectoryName(installed.DirectoryName) || !SemanticVersion.TryParse(installed.Version, out _)) return false;
        var directory = Path.Combine(VersionsDirectory, installed.DirectoryName);
        try
        {
            if (!Directory.Exists(directory)) return false;
            RejectReparsePoint(directory);
            RejectReparsePoint(Path.Combine(directory, "app"));
            ValidateManifest(directory, SemanticVersion.Parse(installed.Version), 10);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or IncompatibleUpdateException or FormatException or KeyNotFoundException or InvalidOperationException) { return false; }
    }

    public void MarkWorking(InstalledVersion installed)
    {
        if (!IsValidInstalled(installed)) throw new InvalidDataException("The installed application is incomplete.");
        var state = LoadState();
        if (state.Current?.DirectoryName == installed.DirectoryName) return;
        // Recovery to the previous version must not replace its backup with a version that just failed.
        var previous = state.Previous?.DirectoryName == installed.DirectoryName ? null : state.Current;
        var next = new UpdateState(installed, previous);
        var temporary = StatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(next, JsonOptions));
            if (File.Exists(StatePath)) File.Replace(temporary, StatePath, StatePath + ".bak");
            else File.Move(temporary, StatePath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<InstalledVersion> DownloadAndStageAsync(HttpClient http, ReleaseInfo release, CancellationToken cancellationToken = default)
    {
        GitHubReleaseClient.RequireHttps(release.PackageUrl);
        GitHubReleaseClient.RequireHttps(release.ChecksumUrl);
        using var checksumResponse = await http.GetAsync(release.ChecksumUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        checksumResponse.EnsureSuccessStatusCode();
        var checksumText = System.Text.Encoding.UTF8.GetString(await GitHubReleaseClient.ReadBoundedAsync(checksumResponse, 4096, cancellationToken));
        var expectedHash = ParseChecksum(checksumText, Path.GetFileName(release.PackageUrl.AbsolutePath));
        var archivePath = Path.Combine(StagingDirectory, Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using var response = await http.GetAsync(release.PackageUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is Uri finalUri) GitHubReleaseClient.RequireHttps(finalUri);
            if (response.Content.Headers.ContentLength is long declared && (declared <= 0 || declared > MaximumPackageBytes))
                throw new InvalidDataException("Update package has an invalid size.");
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long total = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    total += count;
                    if (total > MaximumPackageBytes) throw new InvalidDataException("Update package is too large.");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
                if (total == 0 || response.Content.Headers.ContentLength is long length && total != length)
                    throw new InvalidDataException("Update package download was incomplete.");
            }
            return await StagePackageAsync(archivePath, expectedHash, release.Version, 10, cancellationToken);
        }
        finally { if (File.Exists(archivePath)) File.Delete(archivePath); }
    }

    public async Task<InstalledVersion> StagePackageAsync(string archivePath, string expectedSha256, SemanticVersion expectedVersion,
        int supportedRuntimeMajor = 10, CancellationToken cancellationToken = default)
    {
        if (!Regex.IsMatch(expectedSha256, "^[A-Fa-f0-9]{64}$")) throw new InvalidDataException("Invalid SHA-256 checksum.");
        var archiveInfo = new FileInfo(archivePath);
        if (archiveInfo.Length is <= 0 or > MaximumPackageBytes) throw new InvalidDataException("Invalid update package size.");
        await using var package = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(package, cancellationToken));
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actualHash), Convert.FromHexString(expectedSha256)))
            throw new InvalidDataException("Update checksum does not match; the package was rejected.");
        package.Position = 0;
        var stage = Path.Combine(StagingDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            using var archive = new ZipArchive(package, ZipArchiveMode.Read, true);
            await ExtractSafelyAsync(archive, stage, cancellationToken);
            ValidateManifest(stage, expectedVersion, supportedRuntimeMajor);
            var installed = new InstalledVersion(expectedVersion + "-" + Guid.NewGuid().ToString("N"), expectedVersion.ToString());
            Directory.Move(stage, Path.Combine(VersionsDirectory, installed.DirectoryName));
            return installed;
        }
        finally { DeleteStagingDirectory(stage); }
    }

    public static string ParseChecksum(string text, string expectedFilename)
    {
        var lines = text.Trim('\uFEFF', ' ', '\r', '\n', '\t').Split('\n');
        if (lines.Length != 1) throw new InvalidDataException("Expected one SHA-256 checksum for the package.");
        var match = Regex.Match(lines[0].Trim(), @"^([A-Fa-f0-9]{64})(?:\s+\*?(.+))?$");
        if (!match.Success || match.Groups[2].Success && !string.Equals(match.Groups[2].Value, expectedFilename, StringComparison.Ordinal))
            throw new InvalidDataException("The release checksum has an invalid format or filename.");
        return match.Groups[1].Value;
    }

    private static async Task ExtractSafelyAsync(ZipArchive archive, string destination, CancellationToken cancellationToken)
    {
        if (archive.Entries.Count is 0 or > 50000) throw new InvalidDataException("Update archive has an invalid number of entries.");
        long total = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            var isDirectory = name.EndsWith('/');
            var segments = name.TrimEnd('/').Split('/');
            if (segments.Length == 0 || segments[0] != "app" || segments.Any(IsUnsafeSegment))
                throw new InvalidDataException("Update archive contains an unsafe path.");
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Update archive contains a symbolic link or reparse point.");
            if (!names.Add(name.TrimEnd('/'))) throw new InvalidDataException("Update archive contains duplicate paths.");
            total += entry.Length;
            if (entry.Length > 4L * 1024 * 1024 * 1024 || total > MaximumExtractedBytes
                || entry.Length > 1024 * 1024 && entry.Length / Math.Max(1, entry.CompressedLength) > 200)
                throw new InvalidDataException("Update archive exceeds safe extraction limits.");
            var target = Path.GetFullPath(Path.Combine(destination, Path.Combine(segments)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update path escapes staging.");
            if (isDirectory) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = new byte[81920];
            long written = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                written += count;
                if (written > entry.Length) throw new InvalidDataException("Update archive entry exceeded its declared size.");
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }
            if (written != entry.Length) throw new InvalidDataException("Update archive entry is incomplete.");
        }
    }

    private static bool IsUnsafeSegment(string segment) => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."
        || segment.EndsWith('.') || segment.EndsWith(' ') || segment.Any(c => c < 32 || "<>:\"|?*".Contains(c))
        || Regex.IsMatch(segment, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static bool IsSafeDirectoryName(string? directory) => !string.IsNullOrEmpty(directory) && directory.Length <= 150
        && Regex.IsMatch(directory, @"^[A-Za-z0-9][A-Za-z0-9.+-]*$") && !directory.Contains("..");

    private static void ValidateManifest(string directory, SemanticVersion expectedVersion, int supportedRuntimeMajor)
    {
        var appDirectory = Path.Combine(directory, "app");
        var manifestPath = Path.Combine(appDirectory, "update-manifest.json");
        if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 8192) throw new InvalidDataException("Update manifest is missing or invalid.");
        RejectReparsePoint(manifestPath);
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllText(manifestPath), JsonOptions)
            ?? throw new InvalidDataException("Update manifest is empty.");
        if (!SemanticVersion.TryParse(manifest.Version, out var version) || version!.ToString() != expectedVersion.ToString())
            throw new InvalidDataException("The release version does not match its package manifest.");
        if (manifest.RuntimeMajor != supportedRuntimeMajor || manifest.Architecture != "x64")
            throw new IncompatibleUpdateException($"Release {manifest.Version} requires .NET Desktop Runtime {manifest.RuntimeMajor} and {manifest.Architecture}. Install its new full installer to upgrade; the existing app will continue to run.");
        if (manifest.Executable != ExecutableName) throw new InvalidDataException("Unexpected update executable.");
        foreach (var file in new[] { ExecutableName, "Scanner.Server.dll", "Scanner.Core.dll", "Scanner.Server.deps.json", "Scanner.Server.runtimeconfig.json", "Scanner.TestClient.exe", "Scanner.TestClient.dll", "Scanner.Contracts.dll" })
        {
            var path = Path.Combine(appDirectory, file);
            if (!File.Exists(path) || new FileInfo(path).Length == 0) throw new InvalidDataException($"Update package is missing {file}.");
            RejectReparsePoint(path);
        }
        using var runtime = JsonDocument.Parse(File.ReadAllText(Path.Combine(appDirectory, "Scanner.Server.runtimeconfig.json")));
        if (!runtime.RootElement.TryGetProperty("runtimeOptions", out var options)
            || !options.TryGetProperty("frameworks", out var frameworks) || frameworks.ValueKind != JsonValueKind.Array
            || !frameworks.EnumerateArray().Any(f => f.TryGetProperty("name", out var frameworkName)
            && frameworkName.GetString() == "Microsoft.WindowsDesktop.App" && f.TryGetProperty("version", out var frameworkVersion)
            && frameworkVersion.GetString()?.StartsWith(supportedRuntimeMajor + ".0.", StringComparison.Ordinal) == true))
            throw new IncompatibleUpdateException("The package requires an unsupported Desktop Runtime. Use the release's full installer.");
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Update storage must not contain reparse points.");
    }

    private void DeleteStagingDirectory(string path)
    {
        var resolved = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(resolved);
        if (!string.Equals(parent, StagingDirectory, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid staging cleanup path.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
}
