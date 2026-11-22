# Build, run and contribute

Prerequisites, build instructions, CI/packaging and contribution notes.

## Build & run

### Prerequisites

- **Windows 10 1809** or newer (WinUI 3 requirement).
- **.NET 8 SDK** with Windows targeting pack (`net8.0-windows10.0.22621.0`).
- **Visual Studio 2022** 17.14+ (for `.slnx`) or Visual Studio 2026, with the
  *Windows App SDK C# Templates*, *Desktop development with C++*, and *C++ CMake tools for Windows* components.
  Install the ARM64 C++ tools when targeting ARM64.
- **Windows SDK shader compiler (`fxc.exe`)** to build the embedded
  noise-reduction and unsharp-mask shaders. The build finds it under
  `WindowsSdkDir` or the standard Windows Kits installation; set
  `-p:FxcPath="C:\path\to\fxc.exe"` for a custom location.

### From the command line

```powershell
git clone https://github.com/dunclaw/PhotoLibrarian.git
cd PhotoLibrarian
msbuild PhotoLibrarian.slnx /restore /m /p:Configuration=Release /p:Platform=x64
```

### From Visual Studio

Open `PhotoLibrarian.slnx`, set **PhotoLibrarian** as the startup project, pick the **x64** platform, and hit F5.
For command-line compilation use a Visual Studio Developer PowerShell and
Visual Studio's `MSBuild.exe`, not `dotnet build`: the solution contains native VC projects.
Native projects select `DefaultPlatformToolset` from the active Visual Studio installation;
no compiler executable path or MSVC minor version is checked in.

The RAW wrapper, thread-safe LibRaw, zlib, Panorama wrapper and test-only codec
probes are explicit `.vcxproj` projects with build-order references, architecture
mapping, static CRT, and normal MSBuild incremental/clean/rebuild tracking.
LibRaw and zlib sources and notices are acquired into `native\obj\sources`;
dependency versions and checksums are centralized in `native\Dependencies.props`.
The first build requires HTTPS access; cached builds and the installed app need no network.
`Prepare-RawSources.ps1` only verifies/extracts sources and assembles required
licenses. LibRaw and zlib have no CMake or vcpkg build dependency.

OpenCV's upstream CMake remains solely for configuration/code generation of its
selected modules. `Prepare-OpenCv.ps1` downloads/verifies the source and generates
VC projects; `OpenCv.vcxproj` compiles them using MSBuild. Its short cache path
is under `%LOCALAPPDATA%\PhotoLibrarian\OpenCv`, isolated per worktree,
architecture, platform toolset and discovered MSVC version, and its
configuration fingerprint includes the active Visual Studio instance. This
avoids stale NMake/Ninja compiler caches after toolset updates. None of our
wrappers is compiled by a script.

### CI and Microsoft Store packages

GitHub Actions builds the app, runs the xUnit tests, and creates unsigned,
self-contained x64 and ARM64 MSIX packages plus a combined Store bundle.
See [Windows CI and Store releases](STORE-RELEASE.md) for workflow triggers,
versioning, local packaging, and Partner Center submission instructions.
Microsoft signs the Store-distributed package; CI does not need a certificate.

Native compiler/linker failures now propagate directly through MSBuild project
references. Acquisition failures and checksum mismatches also stop the build.
`.\tools\Test-RawDecoderBuildContract.ps1` checks corrupt-source rejection with
both PowerShell hosts and the build-only native reference contract in CI.

## Contributing

This is a personal project but PRs are welcome. Please keep changes scoped, run the existing solution build (`msbuild PhotoLibrarian.slnx /restore /m /p:Configuration=Release /p:Platform=x64`), and follow the existing style — small files, clear separation between `Core` services and UI viewmodels.

Grid diagnostics (`[VIRTUAL]` and `[GRIDVIEW]`) are emitted only by Debug builds,
through the shared background logger rather than directly from scroll/layout
callbacks. They appear in debugger output and the local
`%LOCALAPPDATA%\PhotoLibrarian\Logs\PhotoLibrarian.log`; disabling
`App.EnableDebugLogging` disables these messages too. The bounded queue drops
messages if the writer falls behind rather than blocking scrolling.

---
