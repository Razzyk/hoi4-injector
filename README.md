# HOI4 Injector

Windows launcher with automatic DLL loading, a native lobby verification manager and automatic updates from the separate [HOI4 Admission Mod](https://github.com/Razzyk/hoi4-admission-mod) release repository. Injection is enabled by default.

## Install or run

- **Installer:** download [HOI4Injector-Setup.exe](https://github.com/Razzyk/hoi4-injector/releases/latest/download/HOI4Injector-Setup.exe), run it, then open **HOI4 Injector** from the Start menu. Installation is for the current Windows account and needs no administrator access.
- **Portable:** download [HOI4-Injector-win-x64.zip](https://github.com/Razzyk/hoi4-injector/releases/latest/download/HOI4-Injector-win-x64.zip), extract every file, then run `HOI4Injector.exe`.

Start the injector before starting HOI4 through Steam. It detects the game, waits for it to respond, loads the bundled admission DLL and shows its verification state. If the Steam installation is elsewhere, setup searches the Steam libraries or lets you select `hoi4.exe`. Closing the manager window hides it to the tray; use its tray menu to exit. Windows sign-in startup is optional.

Requires **Windows x64, .NET Framework 4.8 and HOI4 1.19.3.0** with SHA256 `7dc947be34970da1e1c787bcddbe7f62b610ff258aebf8f06aa8b348f3a031d5`. Other game builds are refused. The release includes a compatible module, so first run does not depend on downloading one. Binaries are unsigned.

The native module enforces normal joining: an injected host refuses a client without a valid DLL ticket, and accepts a verified client's request. The completed local game test confirmed that the injected client joined and the plain client stayed out. HOI4 displayed a timeout for the plain copy; **Last refused join** in this manager displays the host's actual reason.

## Automatic DLL updates

The injector checks the latest stable release of `Razzyk/hoi4-admission-mod` on startup and every fifteen minutes. **Help > Check for DLL updates** triggers another check. Public downloads require no GitHub token.

Downloads must match GitHub's release-asset SHA256 digest. The updater also validates package paths, file hashes, protocol, minimum injector version and supported game identity. New modules are stored in separate versioned directories. While HOI4 is running, an update is staged and the manager asks you to close HOI4. When no game is running, it switches the session and verification service to the downloaded module. Network failures retain the installed module. A release requiring a newer injector is refused until the injector itself is updated.

Private sessions, permissions, logs and downloaded versions live in `%LOCALAPPDATA%\HOI4Injector`, separately from the installed application. The installer and uninstaller preserve this data. Do not distribute `session.json`; it contains player credentials. `release-source.json` identifies the trusted module repository. Injector releases and module releases are maintained separately.

## Multiplayer scope

The included verification service uses local Windows named pipes. Verified clients currently need to run on the same machine. **Verified players on other computers need a networked verification service**, which is not implemented in this release. This is a local admission gate, not remote anti-cheat attestation. Live normal joining was demonstrated; hotjoin, reconnect and crossplay still need their own live tests. Existing admitted players are not continuously kicked after lease expiry.

## Build and release

Windows x64, Zig 0.15.2 and the .NET Framework 4.8 compiler are required. Put the four files from a module release into `bundled-module/`, then run:

```powershell
.\build.ps1 -Zig C:\tools\zig\zig.exe
.\tests\Test-Updater.ps1
.\tests\InstallerTests.ps1
.\tests\Test-Coordinator.ps1
```

The Windows workflow builds, tests and publishes installer/portable assets for `v0.4.0` and later matching version tags. Source includes the loader; no switch is required to enable injection. The published source contains no proprietary game files, private sessions or BakkesMod UI resources.

For isolated diagnostics, `--data-dir <directory>` overrides user data. `--init <session-directory> --game-exe <hoi4.exe>` creates a session; `--headless-seconds <1..600>` runs bounded detection. `--launch-test <fresh-session-directory> --game-exe <hoi4.exe>` launches two injected copies and one excluded plain copy. Their Windows captions identify each copy. Separate profiles do not create separate Steam accounts.
