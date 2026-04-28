# Files++

Files++ is a Windows-only file explorer companion app built with `WinUI 3 + Windows App SDK` on `.NET 10`.

## Current v1 foundation

- Tabbed explorer shell with add/close/reorder tabs
- Sidebar locations and drives
- Navigation controls: back, forward, up, breadcrumb
- File list in details layout with sort controls
- Integrated search box with Windows index support + fallback search
- File operations queue: rename/delete (recycle-bin aware), plus copy/move service support
- Session persistence for tabs, selected tab index, window layout, and sidebar pins
- Packaging scripts for both `MSIX` and `MSI` artifact paths

## Repo layout

- `src/FilesPlusPlus.App` - WinUI desktop app shell and view models
- `src/FilesPlusPlus.Core` - domain models, interfaces, and core services
- `tests/FilesPlusPlus.Core.Tests` - unit/integration-style tests for core behavior
- `scripts` - build/package/release automation scripts

## Requirements

- Windows 10 22H2+ (or newer)
- .NET SDK 10
- Visual Studio 2022/2026 with Windows App SDK tooling
- Optional for MSI: WiX Toolset CLI (`wix`)
- Optional for GitHub release uploads: GitHub CLI (`gh`)

## Environment config (`.env`)

Copy `.env.example` to `.env` and fill in values as needed:

```powershell
Copy-Item .env.example .env
```

Supported keys include:

- Build defaults (`FILESPP_CONFIGURATION`, `FILESPP_RUNTIME_IDENTIFIER`, `FILESPP_WINDOWS_TFM`, `FILESPP_OUTPUT_ROOT`)
- MSIX settings (`FILESPP_MSIX_VERSION`, `FILESPP_MSIX_SIGNING_ENABLED`, certificate path/password)
- MSI settings (`FILESPP_MSI_VERSION`, WiX path/source/arch)
- Release settings (`FILESPP_RELEASE_ASSET_ROOTS`, `FILESPP_GH_REPO`, `GH_TOKEN`)

The PowerShell build scripts and GitHub release script auto-load `.env` if present.

## Build and run

```powershell
npm run assets:generate

dotnet restore src/FilesPlusPlus.Core/FilesPlusPlus.Core.csproj
dotnet restore src/FilesPlusPlus.App/FilesPlusPlus.App.csproj
dotnet restore tests/FilesPlusPlus.Core.Tests/FilesPlusPlus.Core.Tests.csproj

dotnet build src/FilesPlusPlus.Core/FilesPlusPlus.Core.csproj -c Debug
dotnet build src/FilesPlusPlus.App/FilesPlusPlus.App.csproj -c Debug
dotnet build tests/FilesPlusPlus.Core.Tests/FilesPlusPlus.Core.Tests.csproj -c Debug
dotnet run --project src/FilesPlusPlus.App/FilesPlusPlus.App.csproj
```

## Tests

```powershell
dotnet test tests/FilesPlusPlus.Core.Tests/FilesPlusPlus.Core.Tests.csproj
```

## Packaging

Before packaging, regenerate branding assets from `assets/files++_logo.png`:

```powershell
npm run assets:generate
```

MSIX:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build-msix.ps1 -Version 1.0.0.0
```

MSIX (signed):

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build-msix.ps1 -EnableSigning
```

MSI:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build-msi.ps1 -Version 1.0.0
```

## Release workflow

Build artifacts first, then publish all `.msix/.msi` assets in one GitHub release:

```powershell
node scripts/release-github.mjs v1.0.0
```
