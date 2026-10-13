# Source provenance

The Windows Forms GUI, menu construction, process manager, release updater, installer, loader and tests in this repository were authored for this project. The original requested interface followed BakkesMod's menu and tray workflow. The public version uses authored native menu construction and contains no copied Qt UI resource or BakkesMod C++ injection implementation.

The loader uses normal Windows loading and initialization APIs. It requires the pinned supported game build and exact process creation identity. The separately published module supplies the authored native lobby guard and local verification service.

No HOI4 executable, proprietary asset, decompiled game implementation, session credential, private log or user profile is distributed. Native build fingerprints and their evidence are documented in the module repository. The updater trusts releases from the configured publisher, validates GitHub's asset digest and each module file hash, and preserves installed versions on failure.
