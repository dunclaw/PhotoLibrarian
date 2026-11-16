# Copilot Instructions



## App preferences

A fast, strictly local (no cloud service) photo library manager for Windows — built with WinUI 3 and Win2D.
This app is designed for average users to manage photos. No complicated external dependencies on separately installed command line tools or obscure settings should be added. Design for the user in mind. No python dependency should be used - this must be a self-contained local install only. Data should all be processed locally and stored with the photo itself where possible - no cloud service dependency or secret database where critical information resides. It's OK to use a db for quick access and indexing, but photo libraries should be portable to another computer just by moving the photos. Tagging and metadata should be preserved. Extreme care should be taken to not do anything potentially destructive to a user's photos without explicit instructions and backups should be made where feasible for recovery.
The app is primarily in C# but native C++ dependencies and code are acceptable if necessary for performance.
Keep the main UI concise and uncluttered. Put long explanatory text and usage instructions in contextual ? help, tooltips, or help files rather than permanently occupying space in the main UI.
Do not impose arbitrary image-size or megapixel limits unless explicitly instructed by the user. Support full-resolution photos from modern cameras; use local/tiled processing, cancellation and actual platform/resource error reporting rather than disabling features based on image dimensions. Do not impose arbitrary counts on user edits or strokes.
Do not use exceptions for normal flow control. Expected validation outcomes, unsupported operations and routine user actions must use explicit result/status values; reserve exceptions for genuinely unexpected conditions or failures.

## Metadata writing

- Route photo metadata writes through one shared metadata-writing path. Extend `MetadataWriterService` or its shared successor for new metadata capabilities; features must not independently choose or implement embedded versus sidecar storage.
- Store metadata in the photo when its format supports the required write. Use an XMP sidecar only when the format cannot support that metadata write (for example, RAW originals). If writing a supported format fails, report the error instead of falling back to a sidecar.
- Preserve existing metadata and respect sidecar ownership, including RAW/raster companions. Handle legacy sidecars safely: preserve unrelated properties and do not remove or supersede an authoritative value until its embedded replacement has been verified.
- Keep writes local, portable, and non-destructive. Use the existing transaction and backup mechanisms where applicable, verify round trips with generated fixtures, and do not rely on the cache database as the sole source of portable metadata.


## Build, toolchains and packages

- Prefer standard Visual Studio VC/MSBuild projects, project references and native build items. Avoid CMake and custom build scripts where standard constructs suffice; any unavoidable upstream generator or acquisition tooling must be narrowly scoped and documented, not hide application compilation.
- Never check in machine-specific or version-specific compiler executable paths. Select toolchains through supported Visual Studio/MSBuild mechanisms so toolset upgrades do not depend on old compiler locations or stale caches.
- Copilot sessions must never change persistent User or Machine environment variables, including `PATH`: do not use `setx`, `Environment.SetEnvironmentVariable` with User/Machine targets, or registry environment writes.
- Prefer executable paths or supported toolchain discovery. Confine any required `PATH` or toolchain setup to a child process (for example, run `vcvars` and MSBuild in the same `cmd.exe` invocation), and avoid accumulating duplicate `PATH` entries. If a reusable shell's process environment must be modified, save its original values and restore them in `finally`.
- Do not edit PowerShell profiles or invoke installers that persist environment changes as a workaround. Do not repair, trim, or reset the user's existing environment; this policy is prevention, not permission to clean up `PATH`.
- Use package-manager-native acquisition and centralized manifests/version management where available. Keep intentional dependency pins reproducible and updateable in one place; do not duplicate package versions or add ad hoc bootstrap scripts when standard package management suffices.
- Preserve architecture/configuration integration, incremental and clean/rebuild behavior, static-linkage choices, and required third-party licenses/source notices when changing native dependencies.

## C# Style Preferences

- All C# code files must begin with `// Copyright (c) Duncan Lawler. All Rights Reserved.`
- Use good C# style with appropriate commenting and readability. 
- Avoid long obtuse and hard to read constructs like multiple nested lambda statements together on a single line
- Be aware of debuggability when using async, threading, and anonymous lambda constructs that make debugging and decoding call stacks difficult to follow
- Exceptions should never be used for normal flow control. Expected validation outcomes, unsupported operations and routine user actions must use explicit result/status values; reserve exceptions for genuinely unexpected conditions or failures.

