# Windows CI and Microsoft Store releases

The `Windows CI` GitHub Actions workflow uses GitHub-hosted Windows runners.
There is no runner to register or machine to maintain. Actions is already
enabled for this public repository, and standard hosted runner execution is
free. Commit and push the workflow to start using it; merging it into `master`
makes the manual **Run workflow** button available.

## What CI does

- On pull requests, pushes to `master`, `v*` tags, and manual runs, builds the
  application and test projects in Release x64 and runs the normal xUnit suite.
- Uploads xUnit XML results, including when tests fail. Uses xUnit's standalone
  runner because the current test dependencies do not support the legacy
  `dotnet test` VSTest invocation with the .NET 10 SDK.
- Independently publishes self-contained x64 and ARM64 application layouts,
  including both .NET and the Windows App SDK, and validates/packages them with
  the Windows SDK's MakeAppx tool.
- Uploads unsigned `.msix` files and a combined unsigned `.msixbundle`.
  Packaging is independent of the test job for inspection, but a failing test
  job still fails the overall workflow. Do not release a failing run.

The five explicit model-integration tests are not run by default. They require
prepared external model assets and should be run separately. CI does not
download or redistribute those models.

## Package identity and version

`packaging\Package.appxmanifest` contains the reserved Partner Center identity:

| Field | Value |
| --- | --- |
| Package/Identity/Name | `43930Lawler.PhotoLibrarian` |
| Package/Identity/Publisher | `CN=E5DA63EF-5FAE-4960-A409-F9702F1D9A3D` |
| Package/Properties/PublisherDisplayName | `Lawler` |

These are public package metadata, not credentials. Keep the identity stable
across releases. The package is a full-trust desktop application with the
`runFullTrust` restricted capability, not an AppContainer application. Explain
this capability in the Store submission: the app needs desktop WinUI APIs and
user-selected photo-library filesystem access, including metadata edits.

Automatic builds use `1.0.<GitHub run number>.0`. A tag such as `v1.2.3` uses
`1.2.3.0`; alternatively enter `1.2.3.0` when manually running the workflow.
Store versions have four fields, each at most 65535, a nonzero major field,
and a final field of zero. Choose a version greater than every version already
submitted to the Store; CI cannot inspect Partner Center to enforce this.

The manifest targets Windows 10 version 2004 (build 19041) and later, matching
the project's minimum target version. Package logos are generated from the
existing app icon. Normal local builds and tests remain unpackaged.

## Submit a release

1. Run **Actions > Windows CI > Run workflow** with the intended version,
   or push a version tag. Wait for every job to pass.
2. Download and extract the `store-msixbundle` artifact from that run.
3. In Partner Center, open the reserved PhotoLibrarian app, create a submission,
   and upload the `.msixbundle` on the **Packages** page.
4. Complete the listing, screenshots, age ratings, privacy disclosures, and
   restricted-capability explanation, then submit for certification.

Microsoft signs the package distributed through the Store. No signing
certificate, PFX, Azure signing account, or GitHub signing secret is needed for
this workflow. The unsigned artifacts cannot be installed by double-clicking
on an ordinary machine; they are Store submission packages, not public
sideload installers. Uploading them to GitHub Releases would not make them
installable.

Before the first public release, exercise installation, launch, photo edits,
model import, upgrades, and uninstall through a Store private audience/flight
or a separately test-signed package. Packaging validation does not replace
Windows App Certification Kit checks or Store certification. Packaged desktop
apps can have different app-data virtualization behavior from unpackaged
builds; verify cache/settings behavior and do not assume an existing unpackaged
profile is automatically migrated.

Automatic Partner Center submission is deliberately not enabled. That requires
separate Partner Center API enrollment and credentials; a developer account
and Store package identity alone are not sufficient.

## Build locally

Requires Windows, .NET 10 SDK, and Windows SDK packaging tools. Running the
tests also requires the .NET 8 runtime. Hosted CI installs both SDKs.

```powershell
dotnet build src\PhotoLibrarian.Tests\PhotoLibrarian.Tests.csproj -c Release -p:Platform=x64
dotnet run --project src\PhotoLibrarian.Tests\PhotoLibrarian.Tests.csproj -c Release -p:Platform=x64 --no-build
.\tools\Build-Msix.ps1 -Architecture x64 -Version 1.2.3.0
.\tools\Build-Msix.ps1 -Architecture ARM64 -Version 1.2.3.0
```

Each architecture needs a fresh `artifacts\layout-<architecture>` directory.
For repeat builds, supply a new `-OutputDirectory` rather than reusing an old
publish layout. This prevents removed files from leaking into a new package.

References:
[MSIX package creation](https://learn.microsoft.com/en-us/windows/msix/package/create-app-package-with-makeappx-tool),
[Store signing](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options),
[Store submissions](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/create-app-submission).
