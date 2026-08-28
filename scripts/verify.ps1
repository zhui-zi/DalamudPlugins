[CmdletBinding()]
param(
    [switch]$SkipRestore,
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
    if (!$SkipRestore)
    {
        & dotnet restore 'plugins\KeitaToolbox\KeitaToolbox.csproj'
        Assert-LastExitCode 'Plugin restore'
    }

    & dotnet build 'plugins\KeitaToolbox\KeitaToolbox.csproj' `
        --configuration Release `
        --no-restore `
        --no-incremental
    Assert-LastExitCode 'Plugin build'

    & dotnet test `
        --project 'tests\KeitaToolbox.CoreChecks\KeitaToolbox.CoreChecks.csproj' `
        --configuration Release `
        --minimum-expected-tests 31
    Assert-LastExitCode 'Core tests'

    $architectureLimits = @{
        'plugins\KeitaToolbox\Plugin.Settings.cs' = 400
        'plugins\KeitaToolbox\OccultPotFeature.cs' = 5000
        'plugins\KeitaToolbox\OccultPotFeature.AutoDig.cs' = 3500
        'plugins\KeitaToolbox\OccultPotFeature.CofferHunt.cs' = 1000
        'plugins\KeitaToolbox\OccultPotFeature.TrackerModels.cs' = 200
        'plugins\KeitaToolbox\AsyncOperationGate.cs' = 100
    }
    foreach ($entry in $architectureLimits.GetEnumerator())
    {
        $lineCount = (Get-Content -LiteralPath $entry.Key).Count
        if ($lineCount -gt $entry.Value)
        {
            throw "$($entry.Key) exceeded its $($entry.Value)-line architecture limit."
        }
    }

    if (!$SkipNpmInstall)
    {
        & npm.cmd ci --prefix 'workers\dalamud-unlock'
        Assert-LastExitCode 'Unlock Worker install'

        & npm.cmd ci --prefix 'workers\keita-toolbox-stats'
        Assert-LastExitCode 'Stats Worker install'
    }

    & npm.cmd run check --prefix 'workers\dalamud-unlock'
    Assert-LastExitCode 'Unlock Worker checks'

    & npm.cmd run check --prefix 'workers\keita-toolbox-stats'
    Assert-LastExitCode 'Stats Worker checks'

    & git diff --check
    Assert-LastExitCode 'Git diff check'

    Write-Host 'All verification checks passed.'
}
finally
{
    Pop-Location
}
