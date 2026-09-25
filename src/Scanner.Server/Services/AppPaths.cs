using System.IO;
using System.Text.Json;
using Scanner.Contracts;

namespace Scanner.Server.Services;

public sealed class AppPaths
{
    public string Root { get; }
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string Repository { get; }
    public AppPaths(string[] args)
    {
        var index = Array.IndexOf(args, "--data-dir");
        Root = Path.GetFullPath(index >= 0 && index + 1 < args.Length ? args[index + 1] :
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OEPS", "Scanner", "data"));
        Directory.CreateDirectory(Root);
        var parent = new DirectoryInfo(AppContext.BaseDirectory);
        while (parent is not null && !Directory.Exists(Path.Combine(parent.FullName, "recognition"))) parent = parent.Parent;
        Repository = parent?.FullName ?? AppContext.BaseDirectory;
    }
    public static async Task AtomicJsonAsync<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(file, value, ProtocolJson.Options);
                await file.FlushAsync();
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
