param(
    [string]$ProjectPath = "src/FilesPlusPlus.App/FilesPlusPlus.App.csproj",
    [string]$Configuration = "Debug",
    [switch]$UseVsDevShell
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path $PSScriptRoot -Parent

$dotnetCliHome = Join-Path $repoRoot ".dotnet-cli-home"
$env:DOTNET_CLI_HOME = $dotnetCliHome
New-Item -ItemType Directory -Path $env:DOTNET_CLI_HOME -Force | Out-Null

if (-not [System.IO.Path]::IsPathRooted($ProjectPath))
{
    $ProjectPath = Join-Path $repoRoot $ProjectPath
}

if ($UseVsDevShell.IsPresent)
{
    # Section: Resolve and launch VS Dev Shell
    $devShellPath = [Environment]::GetEnvironmentVariable("FILESPP_VS_DEVSHELL_PATH")
    if ([string]::IsNullOrWhiteSpace($devShellPath))
    {
        $devShellPath = "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\Tools\Launch-VsDevShell.ps1"
    }

    if (-not (Test-Path -LiteralPath $devShellPath))
    {
        throw "Visual Studio Dev Shell script not found: $devShellPath"
    }

    # Why: VS Dev Shell mutates PATH with toolchain entries that conflict with WinAppSDK activation.
    $pathBefore = $env:PATH

    & $devShellPath -SkipAutomaticLocation

    Write-Host "Cleaning $ProjectPath ($Configuration) to refresh generated XAML artifacts..."
    & dotnet clean $ProjectPath -c $Configuration
    if ($LASTEXITCODE -ne 0)
    {
        throw "dotnet clean failed with exit code $LASTEXITCODE."
    }

    # Section: Build with retry
    # Why: WinUI XAML compilation intermittently fails due to transient file locks during process teardown.
    $buildAttempts = 3
    $buildOk = $false
    for ($i = 1; $i -le $buildAttempts; $i++)
    {
        Write-Host "Building $ProjectPath ($Configuration)... (attempt $i/$buildAttempts)"
        & dotnet build $ProjectPath -c $Configuration
        if ($LASTEXITCODE -eq 0)
        {
            $buildOk = $true
            break
        }
        if ($i -lt $buildAttempts)
        {
            Write-Host "Build failed (exit $LASTEXITCODE). Waiting 3s before retry..."
            Start-Sleep -Seconds 3
        }
    }
    if (-not $buildOk)
    {
        throw "dotnet build failed after $buildAttempts attempts (exit $LASTEXITCODE)."
    }

    # Section: Resolve compiled binary
    $projDir = Split-Path $ProjectPath -Parent
    $tfm     = "net10.0-windows10.0.22621.0"
    $rid     = "win-x64"
    $exeName = [System.IO.Path]::GetFileNameWithoutExtension($ProjectPath) + ".exe"
    $exePath = [System.IO.Path]::Combine($projDir, "bin", $Configuration, $tfm, $rid, $exeName)

    if (-not (Test-Path -LiteralPath $exePath))
    {
        throw "Compiled binary not found: $exePath"
    }

    # Section: Sanitize environment for app launch
    # Why: PATH restoration alone is insufficient; VS environment variables can still alter WinUI startup behavior.
    $env:PATH = $pathBefore
    $vsEnvVars = @(
        'VSINSTALLDIR','VCINSTALLDIR','VCToolsInstallDir','VCToolsRedistDir','VCToolsVersion',
        'WindowsSdkDir','WindowsSdkBinPath','WindowsSdkVerBinPath','WindowsSDKVersion',
        'WindowsSDKLibVersion','WindowsLibPath','UCRTVersion','UniversalCRTSdkDir',
        'INCLUDE','LIB','LIBPATH','EXTERNAL_INCLUDE','__VSCMD_PREINIT_PATH',
        '__VSCMD_script_err_count','VSCMD_ARG_app_plat','VSCMD_ARG_HOST_ARCH',
        'VSCMD_ARG_TGT_ARCH','VSCMD_VER','VS180COMNTOOLS','DevEnvDir','Framework40Version',
        'FrameworkDir','FrameworkDir64','FrameworkVersion','FrameworkVersion64',
        'NETFXSDKDir','Platform','PreferredToolArchitecture','VisualStudioVersion'
    )
    foreach ($name in $vsEnvVars)
    {
        if (Test-Path "Env:$name") { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
    }

    # Section: Launch and verify startup
    $launchAttempts = 2
    for ($i = 1; $i -le $launchAttempts; $i++)
    {
        if ($i -eq 1)
        {
            Write-Host "Starting $exeName..."
        }
        else
        {
            Write-Host "App exited before showing a window. Retrying launch ($i/$launchAttempts)..."
        }

        $process = Start-Process -FilePath $exePath -WorkingDirectory $projDir -PassThru

        # Why: Start-Process can return before the first UI window exists, so early exits must be retried.
        $deadline = [DateTime]::UtcNow.AddSeconds(4)
        $windowReady = $false
        do
        {
            Start-Sleep -Milliseconds 200
            $process.Refresh()
            if ($process.HasExited)
            {
                break
            }

            if ($process.MainWindowHandle -ne 0)
            {
                $windowReady = $true
                break
            }
        } while ([DateTime]::UtcNow -lt $deadline)

        if ($windowReady)
        {
            # Why: Some crashes happen right after first window creation, so a short stability check is required.
            $stableDeadline = [DateTime]::UtcNow.AddSeconds(5)
            do
            {
                Start-Sleep -Milliseconds 250
                $process.Refresh()
                if ($process.HasExited)
                {
                    break
                }
            } while ([DateTime]::UtcNow -lt $stableDeadline)

            if (-not $process.HasExited)
            {
                Write-Host "Started $exeName (pid $($process.Id))."
                break
            }
        }

        if (-not $process.HasExited)
        {
            # Why: MainWindowHandle may remain zero for a short period even when startup is still progressing.
            Write-Host "Started $exeName (pid $($process.Id)); window is still initializing."
            break
        }

        if ($i -eq $launchAttempts)
        {
            throw "App exited during startup with code $($process.ExitCode)."
        }
    }
}
else
{
    & dotnet run --project $ProjectPath -c $Configuration

    if ($LASTEXITCODE -ne 0)
    {
        throw "dotnet run failed with exit code $LASTEXITCODE."
    }
}
