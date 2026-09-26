# Build, installation and updates

Windows x64 is the supported desktop target. Both GUIs show v0.1.0. The SDK is pinned to .NET 10.0.400 (LTS); `scripts/setup.ps1` verifies the official SDK archive's SHA-512 before extraction. .NET dependency lock files are checked in. See `recognition/requirements.lock` for Python versions.

From the repository:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/setup.ps1
powershell -ExecutionPolicy Bypass -File scripts/build.ps1
powershell -ExecutionPolicy Bypass -File scripts/test.ps1
powershell -ExecutionPolicy Bypass -File scripts/run.ps1 -App Both
```

`Run.cmd` builds and opens both GUIs. `scripts/run.ps1 -App Server` and `-App TestClient` run them individually. VS Code includes the equivalent launch configurations, tests and package tasks.

No OCR/model provisioning is required. The worker uses only OpenCV, NumPy and ZXing-C++ and operates offline after dependency installation.

To create release artifacts:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/package.ps1 -Version 0.1.0
```

The package command runs tests, publishes both WPF applications, bundles a clean Python worker containing only the locked decoder distributions, creates `artifacts/OEPS.Scanner-0.1.0-win-x64.zip` plus SHA-256, builds `artifacts/OEPS.Scanner-0.1.0-setup-win-x64.msi` plus SHA-256, and packs the independent client DLL ZIP and NuGet libraries. `-WorkerDirectory` accepts a previously packaged worker. `-SkipMsi` makes the app update ZIP and SDK artifacts; that app ZIP requires the .NET 10 Desktop and ASP.NET runtimes from a full installation. `-SkipTests` is available only after tests were already run and is recorded in the release handoff.

Install the MSI for a complete per-user runtime distribution. No administrator, SDK, Python package installation or developer tools are required for operators. The installer creates separate desktop and Start menu shortcuts for OEPS Scanner Server and OEPS Scanner Test Client. The staged `full-installer` folder can also be copied intact and launched with `launcher/Scanner.Launcher.exe --install-package <full-path-to-ZIP>` (add `--app TestClient` for the client).

The stable launcher and shared runtime sit under `%LOCALAPPDATA%/OEPS Scanner Installer`. Application versions live under `%LOCALAPPDATA%/OEPS/Scanner/installation/versions`; current and previous working version pointers are updated atomically only after a GUI readiness acknowledgement. Checksum, manifest, path traversal, size and runtime checks precede activation. If the new GUI exits before ready, the launcher tries the saved version. It never terminates an operating GUI to force rollback.

Client settings are `%LOCALAPPDATA%/OEPS/Scanner/client-settings.json`; server settings stay in their separate per-user data root. Removing the MSI preserves settings and any files left by older versions. The current scanner creates no observation, feedback, image or model archive. To inspect update failures use `%LOCALAPPDATA%/OEPS/Scanner/installation/launcher.log`.

The launcher checks the public GitHub release at startup and asks before installing a newer version. Checks time out after eight seconds; unavailable internet or a declined update uses the installed version. `--no-update-check` skips the lookup. Close both applications before updating: the launcher defers update checks while either app is running to keep their versions consistent. Both GUIs show their version at the bottom right and check for an update notification every 30 minutes without interrupting scanning. Runtime-major upgrades require a new full installer. The build scripts create artifacts locally; publishing to GitHub is a separate release step.

Validation of an MSI install/uninstall on a clean Windows machine, physical camera optics/focus and LAN/firewall routing must be reported separately from compilation and automated tests.

References: [.NET 10 download and support information](https://dotnet.microsoft.com/en-us/download/dotnet/10.0), [official SDK release metadata](https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json), [WiX 4 documentation](https://docs.firegiant.com/wix/).
