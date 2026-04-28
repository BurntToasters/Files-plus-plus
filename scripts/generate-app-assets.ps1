param(
    [string]$SourceLogoPath = "assets/files++_logo.png",
    [string]$OutputDir = "src/FilesPlusPlus.App/Assets"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path $PSScriptRoot -Parent

if (-not [System.IO.Path]::IsPathRooted($SourceLogoPath))
{
    $SourceLogoPath = Join-Path $repoRoot $SourceLogoPath
}

if (-not [System.IO.Path]::IsPathRooted($OutputDir))
{
    $OutputDir = Join-Path $repoRoot $OutputDir
}

if (-not (Test-Path -LiteralPath $SourceLogoPath))
{
    throw "Source logo not found: $SourceLogoPath"
}

$magick = Get-Command magick -ErrorAction SilentlyContinue
if (-not $magick)
{
    throw "ImageMagick (magick) is not installed or not on PATH."
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

function New-SquareLogo([string]$BaseName, [int]$BaseSize)
{
    $basePath = Join-Path $OutputDir "$BaseName.png"
    & magick $SourceLogoPath `
        -background none `
        -resize "$BaseSize`x$BaseSize" `
        -gravity center `
        -extent "$BaseSize`x$BaseSize" `
        $basePath

    if ($LASTEXITCODE -ne 0)
    {
        throw "Failed generating $basePath"
    }

    foreach ($scale in 100, 125, 150, 200, 400)
    {
        $size = [int][Math]::Round($BaseSize * ($scale / 100.0), 0, [MidpointRounding]::AwayFromZero)
        $scaledPath = Join-Path $OutputDir "$BaseName.scale-$scale.png"
        & magick $SourceLogoPath `
            -background none `
            -resize "$size`x$size" `
            -gravity center `
            -extent "$size`x$size" `
            $scaledPath

        if ($LASTEXITCODE -ne 0)
        {
            throw "Failed generating $scaledPath"
        }
    }
}

function New-WideLogo([string]$BaseName, [int]$BaseWidth, [int]$BaseHeight)
{
    $basePath = Join-Path $OutputDir "$BaseName.png"
    & magick $SourceLogoPath `
        -background none `
        -resize "$BaseWidth`x$BaseHeight" `
        -gravity center `
        -extent "$BaseWidth`x$BaseHeight" `
        $basePath

    if ($LASTEXITCODE -ne 0)
    {
        throw "Failed generating $basePath"
    }

    foreach ($scale in 100, 125, 150, 200, 400)
    {
        $width = [int][Math]::Round($BaseWidth * ($scale / 100.0), 0, [MidpointRounding]::AwayFromZero)
        $height = [int][Math]::Round($BaseHeight * ($scale / 100.0), 0, [MidpointRounding]::AwayFromZero)
        $scaledPath = Join-Path $OutputDir "$BaseName.scale-$scale.png"
        & magick $SourceLogoPath `
            -background none `
            -resize "$width`x$height" `
            -gravity center `
            -extent "$width`x$height" `
            $scaledPath

        if ($LASTEXITCODE -ne 0)
        {
            throw "Failed generating $scaledPath"
        }
    }
}

Write-Host "Generating app assets from $SourceLogoPath ..."

New-SquareLogo -BaseName "Square44x44Logo" -BaseSize 44
New-SquareLogo -BaseName "Square150x150Logo" -BaseSize 150
New-SquareLogo -BaseName "Square310x310Logo" -BaseSize 310
New-SquareLogo -BaseName "StoreLogo" -BaseSize 50
New-WideLogo -BaseName "Wide310x150Logo" -BaseWidth 310 -BaseHeight 150
New-WideLogo -BaseName "SplashScreen" -BaseWidth 620 -BaseHeight 300

$unplated44Path = Join-Path $OutputDir "Square44x44Logo.targetsize-44_altform-unplated.png"
& magick $SourceLogoPath `
    -background none `
    -resize "44x44" `
    -gravity center `
    -extent "44x44" `
    $unplated44Path

if ($LASTEXITCODE -ne 0)
{
    throw "Failed generating $unplated44Path"
}

$icoPath = Join-Path $OutputDir "AppIcon.ico"
& magick $SourceLogoPath `
    -background none `
    -resize "256x256" `
    -define icon:auto-resize=256,128,64,48,32,24,16 `
    $icoPath

if ($LASTEXITCODE -ne 0)
{
    throw "Failed generating $icoPath"
}

Write-Host "Generated assets in $OutputDir"
