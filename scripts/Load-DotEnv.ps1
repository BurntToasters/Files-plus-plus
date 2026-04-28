function Load-DotEnv {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path))
    {
        return
    }

    $lines = Get-Content -LiteralPath $Path
    foreach ($line in $lines)
    {
        $trimmed = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith("#"))
        {
            continue
        }

        $delimiterIndex = $trimmed.IndexOf("=")
        if ($delimiterIndex -lt 1)
        {
            continue
        }

        $key = $trimmed.Substring(0, $delimiterIndex).Trim()
        if ([string]::IsNullOrWhiteSpace($key))
        {
            continue
        }

        $value = $trimmed.Substring($delimiterIndex + 1).Trim()
        if (
            ($value.StartsWith('"') -and $value.EndsWith('"')) -or
            ($value.StartsWith("'") -and $value.EndsWith("'"))
        )
        {
            $value = $value.Substring(1, $value.Length - 2)
        }

        if (-not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($key)))
        {
            continue
        }

        [Environment]::SetEnvironmentVariable($key, $value)
    }
}
