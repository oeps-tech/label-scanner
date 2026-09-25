using System.Security.Cryptography;
using System.Text;

namespace Scanner.Desktop;

public sealed class AppInstanceCoordinator : IDisposable
{
    private static readonly string UserSuffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserDomainName + "\\" + Environment.UserName)))[..20];
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activation;
    private readonly RegisteredWaitHandle _registration;
    public static string AppMutexName(string app) => @"Local\OEPS.Scanner." + app + ".App." + UserSuffix;
    public static string LauncherMutexName(string app) => @"Local\OEPS.Scanner." + app + ".Launcher." + UserSuffix;
    private static string ActivationEventName(string app) => @"Local\OEPS.Scanner." + app + ".Activate." + UserSuffix;

    private AppInstanceCoordinator(Mutex mutex, string app, Action activate)
    {
        _mutex = mutex;
        _activation = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName(app));
        _registration = ThreadPool.RegisterWaitForSingleObject(_activation, (_, _) => activate(), null, Timeout.Infinite, false);
    }
    public static AppInstanceCoordinator? TryAcquire(string app, Action activate)
    {
        var mutex = new Mutex(false, AppMutexName(app), out var created);
        if (created) return new(mutex, app, activate);
        mutex.Dispose(); ActivateExisting(app); return null;
    }
    public static bool IsAppRunning(string app)
    {
        if (!Mutex.TryOpenExisting(AppMutexName(app), out var mutex)) return false;
        mutex.Dispose(); return true;
    }
    public static void ActivateExisting(string app)
    {
        if (EventWaitHandle.TryOpenExisting(ActivationEventName(app), out var activation)) using (activation) activation.Set();
    }
    public static void SignalReadyFromArguments(string[] args)
    {
        var index = Array.IndexOf(args, "--startup-ready");
        if (index < 0 || index + 1 >= args.Length || !args[index + 1].StartsWith(@"Local\OEPS.Scanner.Ready.", StringComparison.Ordinal)) return;
        if (EventWaitHandle.TryOpenExisting(args[index + 1], out var ready)) using (ready) ready.Set();
    }
    public void Dispose() { _registration.Unregister(null); _activation.Dispose(); _mutex.Dispose(); }
}
