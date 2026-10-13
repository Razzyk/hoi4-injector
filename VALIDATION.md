# Validation

The Windows x64 injector, native loader, setup program and uninstaller build with warnings treated as errors.

- 22 updater scenarios passed: stable-version selection, offline use, digest/file validation, staging versus activation, malformed or unsafe archives, compatibility, immutable versions and failed downloads preserving the installation.
- 35 installer checks passed, including path containment, links, duplicate/missing entries, ownership markers, known-file-only removal and rollback when a binary cannot be replaced. The actual embedded setup payload extracted successfully in an isolated folder.
- 6 coordinator cases / 63 assertions passed: activation deferred while games run, older sessions migrate to the current module, identities and permissions remain intact, failed service replacement restores the saved configuration even if cleanup fails, game-start races defer activation, and custom data roots survive autostart.
- The enabled native loader rejected an unsupported executable named `hoi4.exe` before loading any DLL.
- The production updater downloaded the real public module v0.3.0 release without authentication, verified GitHub's asset digest and package files, staged it while preserving the old selection, and activated it in isolated fixture storage. The older-version fixture was synthetic; no fixture DLL or service was executed.

The module's existing real normal-join test demonstrated an injected client joining an injected host and a plain client being refused. Native module behavior is unchanged by the repository split. The module repository documents its native tests, supported executable fingerprints and multiplayer limits.

The service currently supports participants on one Windows machine. Remote verification, live positive hotjoin/reconnect and crossplay remain outstanding capabilities. A valid update must match this injector's supported game/protocol; unsupported game builds are refused.
