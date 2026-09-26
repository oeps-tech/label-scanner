# Installer architecture

`scripts/Build-Msi.ps1` emits WiX 4.0.6 source from the staged tree. The per-user MSI installs a small stable launcher, the shared .NET 10 Core/Desktop/ASP.NET runtimes, and a checksummed versioned app ZIP under `%LOCALAPPDATA%/OEPS Scanner Installer`. Desktop and Start menu shortcuts launch Server and Test Client independently.

The launcher verifies SHA-256, archive paths, size limits and the runtime/architecture manifest before staging an immutable app directory under `%LOCALAPPDATA%/OEPS/Scanner/installation/versions`. It records the working version only after the chosen GUI signals startup readiness. A failing startup retries the saved previous version; a still-running child is preserved for inspection. Settings live outside installed versions and survive upgrades/uninstallation.

This follows the sibling `raw-material-sticker` and `generate-kicad-production-files` installation architecture. Their update-store, semantic-version, checksum, ZIP-validation, WiX runtime and shortcut patterns were adapted for two applications and a larger bundled Python worker. Scanner has its own product/upgrade/component IDs and installation paths.

Application update ZIPs contain `app/update-manifest.json`. Full installers are required when the runtime major changes. The launcher checks the public `oeps-tech/label-scanner` release at startup and asks before downloading a newer version. An offline station falls back to the installed app; `--no-update-check` skips the lookup. Updates wait until both apps are closed. A version footer and background update notification appear in both GUIs. Publishing is separate from building the installer.
