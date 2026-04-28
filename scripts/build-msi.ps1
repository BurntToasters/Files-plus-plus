param(
    [string]$Configuration = "",
    [string]$Version = "",
    [string]$RuntimeIdentifier = "",
    [string]$ProjectPath = "",
    [string]$OutputRoot = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot "Load-DotEnv.ps1")
Load-DotEnv -Path (Join-Path $repoRoot ".env")

function Resolve-Setting([string]$CliValue, [string]$EnvName, [string]$DefaultValue)
{
    if (-not [string]::IsNullOrWhiteSpace($CliValue))
    {
        return $CliValue
    }

    $envValue = [Environment]::GetEnvironmentVariable($EnvName)
    if (-not [string]::IsNullOrWhiteSpace($envValue))
    {
        return $envValue
    }

    return $DefaultValue
}

$configuration = Resolve-Setting $Configuration "FILESPP_CONFIGURATION" "Release"
$version = Resolve-Setting $Version "FILESPP_MSI_VERSION" "1.0.0"
$runtimeIdentifier = Resolve-Setting $RuntimeIdentifier "FILESPP_RUNTIME_IDENTIFIER" "win-x64"
$projectPath = Resolve-Setting $ProjectPath "FILESPP_PROJECT_PATH" "src/FilesPlusPlus.App/FilesPlusPlus.App.csproj"
$outputRoot = Resolve-Setting $OutputRoot "FILESPP_OUTPUT_ROOT" "artifacts"
$targetFramework = Resolve-Setting "" "FILESPP_WINDOWS_TFM" "net10.0-windows10.0.22621.0"
$wixArch = Resolve-Setting "" "FILESPP_MSI_WIX_ARCH" "x64"
$wxsPath = Resolve-Setting "" "FILESPP_MSI_WIX_SOURCE" "scripts/wix/FilesPlusPlus.wxs"

$dotnetCliHome = Resolve-Setting "" "FILESPP_DOTNET_CLI_HOME" ".dotnet-cli-home"
if (-not [System.IO.Path]::IsPathRooted($dotnetCliHome))
{
    $dotnetCliHome = Join-Path $repoRoot $dotnetCliHome
}

$env:DOTNET_CLI_HOME = $dotnetCliHome
New-Item -ItemType Directory -Path $env:DOTNET_CLI_HOME -Force | Out-Null

if (-not [System.IO.Path]::IsPathRooted($projectPath))
{
    $projectPath = Join-Path $repoRoot $projectPath
}

if (-not [System.IO.Path]::IsPathRooted($outputRoot))
{
    $outputRoot = Join-Path $repoRoot $outputRoot
}

if (-not [System.IO.Path]::IsPathRooted($wxsPath))
{
    $wxsPath = Join-Path $repoRoot $wxsPath
}

$publishOut = Join-Path $outputRoot ("publish/" + $runtimeIdentifier)
$msiOutDir = Join-Path $outputRoot "msi"
New-Item -ItemType Directory -Path $publishOut -Force | Out-Null
New-Item -ItemType Directory -Path $msiOutDir -Force | Out-Null

Write-Host "Publishing Files++ for MSI packaging..."
& dotnet publish $projectPath `
    -c $configuration `
    -r $runtimeIdentifier `
    -f $targetFramework `
    -p:WindowsPackageType=None `
    -p:WindowsAppSDKSelfContained=true `
    -p:PublishSingleFile=false `
    -o $publishOut

if ($LASTEXITCODE -ne 0)
{
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$wixPathOverride = Resolve-Setting "" "FILESPP_WIX_PATH" ""
if (-not [string]::IsNullOrWhiteSpace($wixPathOverride))
{
    $wix = Get-Item -LiteralPath $wixPathOverride -ErrorAction SilentlyContinue
    if (-not $wix)
    {
        throw "FILESPP_WIX_PATH is set but not found: $wixPathOverride"
    }
    $wixCommand = $wix.FullName
}
else
{
    $wix = Get-Command wix -ErrorAction SilentlyContinue
    if (-not $wix)
    {
        Write-Warning "WiX CLI was not found on PATH. Publish output is ready at $publishOut."
        Write-Warning "Install WiX v4/v5 CLI and rerun this script to produce an MSI."
        exit 0
    }
    $wixCommand = $wix.Source
}

$msiPath = Join-Path $msiOutDir ("FilesPlusPlus-" + $version + "-" + $runtimeIdentifier + ".msi")

Write-Host "Building MSI with WiX..."
& $wixCommand build $wxsPath `
    -d PublishDir="$publishOut" `
    -d ProductVersion="$version" `
    -arch $wixArch `
    -o $msiPath

if ($LASTEXITCODE -ne 0)
{
    throw "wix build failed with exit code $LASTEXITCODE."
}

Write-Host "MSI created: $msiPath"
