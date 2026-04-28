param(
    [string]$Configuration = "",
    [string]$Version = "",
    [string]$RuntimeIdentifier = "",
    [string]$ProjectPath = "",
    [string]$OutputRoot = "",
    [switch]$EnableSigning
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

function Resolve-Bool([string]$EnvName, [bool]$DefaultValue)
{
    $envValue = [Environment]::GetEnvironmentVariable($EnvName)
    if ([string]::IsNullOrWhiteSpace($envValue))
    {
        return $DefaultValue
    }

    switch ($envValue.Trim().ToLowerInvariant())
    {
        { $_ -in @("1", "true", "yes", "y", "on") } { return $true }
        { $_ -in @("0", "false", "no", "n", "off") } { return $false }
        default { throw "Invalid boolean value '$envValue' for $EnvName." }
    }
}

$configuration = Resolve-Setting $Configuration "FILESPP_CONFIGURATION" "Release"
$version = Resolve-Setting $Version "FILESPP_MSIX_VERSION" "1.0.0.0"
$runtimeIdentifier = Resolve-Setting $RuntimeIdentifier "FILESPP_RUNTIME_IDENTIFIER" "win-x64"
$projectPath = Resolve-Setting $ProjectPath "FILESPP_PROJECT_PATH" "src/FilesPlusPlus.App/FilesPlusPlus.App.csproj"
$outputRoot = Resolve-Setting $OutputRoot "FILESPP_OUTPUT_ROOT" "artifacts"
$targetFramework = Resolve-Setting "" "FILESPP_WINDOWS_TFM" "net10.0-windows10.0.22621.0"
$buildMode = Resolve-Setting "" "FILESPP_MSIX_BUILD_MODE" "SideloadOnly"

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

$publishOut = Join-Path $outputRoot ("msix/" + $runtimeIdentifier)
New-Item -ItemType Directory -Path $publishOut -Force | Out-Null

$signingEnabled = if ($EnableSigning.IsPresent) { $true } else { Resolve-Bool "FILESPP_MSIX_SIGNING_ENABLED" $false }
$signingLiteral = if ($signingEnabled) { "true" } else { "false" }

$publishArgs = @(
    "publish", $projectPath,
    "-c", $configuration,
    "-r", $runtimeIdentifier,
    "-f", $targetFramework,
    "-p:WindowsPackageType=MSIX",
    "-p:WindowsAppSDKSelfContained=false",
    "-p:GenerateAppxPackageOnBuild=true",
    "-p:AppxBundle=Never",
    "-p:PackageVersion=$version",
    "-p:AppxPackageSigningEnabled=$signingLiteral",
    "-p:AppxPackageDir=$publishOut\",
    "-p:UapAppxPackageBuildMode=$buildMode"
)

if ($signingEnabled)
{
    $certificatePath = Resolve-Setting "" "FILESPP_MSIX_CERT_PFX_PATH" ""
    if ([string]::IsNullOrWhiteSpace($certificatePath))
    {
        throw "FILESPP_MSIX_CERT_PFX_PATH is required when signing is enabled."
    }

    if (-not [System.IO.Path]::IsPathRooted($certificatePath))
    {
        $certificatePath = Join-Path $repoRoot $certificatePath
    }

    if (-not (Test-Path -LiteralPath $certificatePath))
    {
        throw "MSIX certificate not found: $certificatePath"
    }

    $publishArgs += "-p:PackageCertificateKeyFile=$certificatePath"

    $certificatePassword = Resolve-Setting "" "FILESPP_MSIX_CERT_PASSWORD" ""
    if (-not [string]::IsNullOrWhiteSpace($certificatePassword))
    {
        $publishArgs += "-p:PackageCertificatePassword=$certificatePassword"
    }
}

Write-Host "Building MSIX for $runtimeIdentifier ($version)..."
& dotnet @publishArgs

if ($LASTEXITCODE -ne 0)
{
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

Write-Host "MSIX artifacts written under $publishOut"
