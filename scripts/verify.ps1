[CmdletBinding()]
param(
    [switch]$SkipNpmInstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-LastExitCode([string]$Step)
{
    if ($LASTEXITCODE -ne 0)
    {
        throw "$Step failed with exit code $LASTEXITCODE."
    }
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot
try
{
    $entries = Get-Content -LiteralPath 'pluginmaster.json' -Raw -Encoding utf8 |
        ConvertFrom-Json
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    foreach ($entry in $entries)
    {
        $packagePath = "plugins\$($entry.InternalName)\latest.zip"
        if (!(Test-Path -LiteralPath $packagePath))
        {
            throw "Missing package for $($entry.InternalName)."
        }

        $package = [System.IO.Compression.ZipFile]::OpenRead(
            (Resolve-Path -LiteralPath $packagePath))
        try
        {
            $manifestEntry = $package.Entries |
                Where-Object FullName -eq "$($entry.InternalName).json"
            $dllEntry = $package.Entries |
                Where-Object FullName -eq "$($entry.InternalName).dll"
            if ($null -eq $manifestEntry -or $null -eq $dllEntry)
            {
                throw "$($entry.InternalName) package is missing its manifest or DLL."
            }

            $reader = [System.IO.StreamReader]::new($manifestEntry.Open())
            try
            {
                $packageManifest = $reader.ReadToEnd() | ConvertFrom-Json
            }
            finally
            {
                $reader.Dispose()
            }

            if ([string]$packageManifest.InternalName -ne [string]$entry.InternalName -or
                [string]$packageManifest.AssemblyVersion -ne [string]$entry.AssemblyVersion -or
                [int]$packageManifest.DalamudApiLevel -ne [int]$entry.DalamudApiLevel)
            {
                throw "$($entry.InternalName) manifest and package metadata do not match."
            }
        }
        finally
        {
            $package.Dispose()
        }
    }

    if (!$SkipNpmInstall)
    {
        & npm.cmd ci --prefix 'workers\dalamud-unlock'
        Assert-LastExitCode 'Unlock Worker install'
    }

    & npm.cmd run check --prefix 'workers\dalamud-unlock'
    Assert-LastExitCode 'Unlock Worker checks'

    & git diff --check
    Assert-LastExitCode 'Git diff check'

    Write-Host 'All verification checks passed.'
}
finally
{
    Pop-Location
}
