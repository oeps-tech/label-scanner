using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Scanner.Updates;

internal static class UpdateChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "oeps-update-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new UpdateStore(Path.Combine(root, "installation"));
        var first = await Stage("0.1.0");
        check(store.LoadState().Current is null, "Staging does not activate a version before startup readiness");
        store.MarkWorking(first);
        var next = await Stage("0.2.0");
        check(store.LoadState().Current == first, "Failed startup leaves working version intact");
        store.MarkWorking(next);
        check(store.LoadState().Current == next && store.LoadState().Previous == first, "Update preserves previous working version");
        store.MarkWorking(first);
        check(store.LoadState().Current == first && store.LoadState().Previous is null, "Recovery does not retain a failed version as fallback");
        await Reject("1.0.0", badHash: true);
        await Reject("1.0.0", runtime: 11);
        await Reject("1.0.0", omitWorker: true);
        foreach (var extra in new[] { "../outside.txt", "app/../../outside.txt", "app/CON.txt", "app/UPDATE-MANIFEST.json" }) await Reject("1.0.0", extra: extra);
        check(store.LoadState().Current == first && !Directory.EnumerateFileSystemEntries(store.StagingDirectory).Any(), "Rejected updates leave working state and staging clean");
        foreach (var draft in new[] { false, true })
        {
            var json = JsonSerializer.Serialize(new { draft, prerelease = false, tag_name = "v0.2.0", published_at = "2026-09-26T00:00:00Z", assets = new[] {
                new { name = "OEPS.Scanner-0.2.0-win-x64.zip", browser_download_url = "https://github.com/oeps-tech/label-scanner/releases/download/v0.2.0/OEPS.Scanner-0.2.0-win-x64.zip" },
                new { name = "OEPS.Scanner-0.2.0-win-x64.zip.sha256", browser_download_url = "https://github.com/oeps-tech/label-scanner/releases/download/v0.2.0/OEPS.Scanner-0.2.0-win-x64.zip.sha256" }
            }});
            using var http = new HttpClient(new Response(json));
            var release = await new GitHubReleaseClient(http, "oeps-tech", "label-scanner", "OEPS.Scanner").GetLatestReleaseAsync();
            check(draft ? release is null : release?.VersionText == "0.2.0", "Only published stable releases are offered");
        }
        using (var http = new HttpClient(new Response("{}", HttpStatusCode.NotFound)))
            check(await new GitHubReleaseClient(http, "oeps-tech", "label-scanner", "OEPS.Scanner").GetLatestReleaseAsync() is null, "No published release does not prevent startup");

        async Task<InstalledVersion> Stage(string version)
        {
            var package = Package(root, version);
            return await store.StagePackageAsync(package.Path, package.Hash, SemanticVersion.Parse(version));
        }
        async Task Reject(string version, bool badHash = false, int runtime = 10, bool omitWorker = false, string? extra = null)
        {
            var package = Package(root, version, runtime, omitWorker, extra);
            var rejected = false;
            try { await store.StagePackageAsync(package.Path, badHash ? new string('0', 64) : package.Hash, SemanticVersion.Parse(version)); }
            catch (Exception ex) when (ex is InvalidDataException or IncompatibleUpdateException) { rejected = true; }
            check(rejected, "Invalid checksum, incomplete, incompatible or unsafe update rejected");
        }
    }
    private static (string Path, string Hash) Package(string root, string version, int runtime = 10, bool omitWorker = false, string? extra = null)
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            void Add(string name, string text) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(text); }
            Add("app/update-manifest.json", JsonSerializer.Serialize(new { version, runtimeMajor = runtime, architecture = "x64", executable = "Scanner.Server.exe" }));
            Add("app/Scanner.Server.runtimeconfig.json", "{\"runtimeOptions\":{\"frameworks\":[{\"name\":\"Microsoft.WindowsDesktop.App\",\"version\":\"10.0.0\"}]}}");
            foreach (var file in new[] { "Scanner.Server.exe", "Scanner.Server.dll", "Scanner.Core.dll", "Scanner.Server.deps.json", "Scanner.TestClient.exe", "Scanner.TestClient.dll", "Scanner.Contracts.dll", "Scanner.Client.dll", "Scanner.Desktop.dll", "Scanner.Updates.dll", "Scanner.TestClient.deps.json", "Scanner.TestClient.runtimeconfig.json", "worker/python/python.exe", "worker/python/python312.dll", "worker/python/python312.zip", "worker/python/python312._pth", "worker/recognition/worker.py", "worker/recognition/ipc.py", "worker/recognition/__init__.py" })
                if (!omitWorker || file != "worker/recognition/worker.py") Add("app/" + file, "fixture");
            if (extra is not null) Add(extra, "fixture");
        }
        return (path, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }
    private sealed class Response(string json, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json), RequestMessage = request });
    }
}
