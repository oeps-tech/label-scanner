using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Scanner.Updates;

public sealed record ReleaseInfo(SemanticVersion Version, string VersionText, Uri PackageUrl, Uri ChecksumUrl);

public sealed class GitHubReleaseClient
{
    private readonly HttpClient _http;
    private readonly string _owner;
    private readonly string _repository;
    private readonly string _packagePrefix;

    public GitHubReleaseClient(HttpClient http, string owner, string repository, string packagePrefix)
    {
        _http = http;
        if (!Regex.IsMatch(owner, @"^[A-Za-z0-9_.-]+$") || !Regex.IsMatch(repository, @"^[A-Za-z0-9_.-]+$"))
            throw new ArgumentException("Configure a valid public GitHub owner and repository.");
        if (!Regex.IsMatch(packagePrefix, @"^[A-Za-z0-9_.-]+$")) throw new ArgumentException("Invalid update package prefix.");
        (_owner, _repository, _packagePrefix) = (owner, repository, packagePrefix);
    }

    public async Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{_owner}/{_repository}/releases/latest");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("OEPS-Scanner", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 429)
            throw new HttpRequestException("GitHub denied the update check or its public rate limit was reached. The installed app can still run.");
        response.EnsureSuccessStatusCode();
        var bytes = await ReadBoundedAsync(response, 2 * 1024 * 1024, cancellationToken);
        try { return ParseRelease(bytes); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        { throw new InvalidDataException("GitHub returned malformed release metadata; the installed app can still run.", ex); }
    }

    private ReleaseInfo? ParseRelease(byte[] bytes)
    {
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()
            || !root.TryGetProperty("published_at", out var published) || published.ValueKind == JsonValueKind.Null) return null;
        if (!SemanticVersion.TryParse(root.GetProperty("tag_name").GetString(), out var version) || version!.IsPrerelease) return null;
        var packageName = $"{_packagePrefix}-{version}-win-x64.zip";
        Uri? package = null, checksum = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            if (name != packageName && name != packageName + ".sha256") continue;
            var url = new Uri(asset.GetProperty("browser_download_url").GetString()!, UriKind.Absolute);
            RequireHttps(url);
            if (name == packageName) package = url; else checksum = url;
        }
        if (package is null || checksum is null) throw new InvalidDataException($"Release {version} is missing {packageName} or its .sha256 checksum.");
        return new(version, version.ToString(), package, checksum);
    }

    internal static void RequireHttps(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("Update downloads must use HTTPS.");
    }

    internal static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int maximumBytes, CancellationToken cancellationToken)
    {
        if (response.RequestMessage?.RequestUri is Uri finalUri) RequireHttps(finalUri);
        if (response.Content.Headers.ContentLength > maximumBytes) throw new InvalidDataException("Update response exceeds the permitted size.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int count;
        while ((count = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (destination.Length + count > maximumBytes) throw new InvalidDataException("Update response exceeds the permitted size.");
            destination.Write(buffer, 0, count);
        }
        return destination.ToArray();
    }
}
