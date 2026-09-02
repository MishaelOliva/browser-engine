[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$sourceRepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publishScriptPath = Join-Path $sourceRepositoryRoot 'build\Publish-MishaWeb.ps1'
$parseTokens = $null
$parseErrors = $null
$publishAst = [System.Management.Automation.Language.Parser]::ParseFile(
    $publishScriptPath,
    [ref] $parseTokens,
    [ref] $parseErrors)
if ($parseErrors.Count -ne 0) {
    throw "Publish script has parser errors: $($parseErrors -join '; ')"
}

foreach ($functionName in @('Remove-CommittedBackupBestEffort', 'Move-OneClickRelease')) {
    $functionAst = $publishAst.Find(
        {
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                [string]::Equals(
                    $node.Name,
                    $functionName,
                    [System.StringComparison]::OrdinalIgnoreCase)
        },
        $true)
    if ($null -eq $functionAst) {
        throw "Could not find release function '$functionName'."
    }

    . ([scriptblock]::Create($functionAst.Extent.Text))
}

function Assert-ExactPath {
    param(
        [Parameter(Mandatory = $true)][string] $Candidate,
        [Parameter(Mandatory = $true)][string] $Expected,
        [Parameter(Mandatory = $true)][string] $Label
    )

    $candidateFull = [System.IO.Path]::GetFullPath($Candidate).TrimEnd('\', '/')
    $expectedFull = [System.IO.Path]::GetFullPath($Expected).TrimEnd('\', '/')
    if (-not [string]::Equals($candidateFull, $expectedFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label mismatch."
    }

    return $candidateFull
}

function Assert-ContainedPath {
    param(
        [Parameter(Mandatory = $true)][string] $Candidate,
        [Parameter(Mandatory = $true)][string] $Parent,
        [Parameter(Mandatory = $true)][string] $Label
    )

    $candidateFull = [System.IO.Path]::GetFullPath($Candidate).TrimEnd('\', '/')
    $parentFull = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\', '/')
    $prefix = $parentFull + [System.IO.Path]::DirectorySeparatorChar
    if (-not $candidateFull.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label escaped the fixture root."
    }

    return $candidateFull
}

function Assert-NoReparseTree {
    param([Parameter(Mandatory = $true)][string] $Path)
}

function Get-PayloadNames {
    param([Parameter(Mandatory = $true)][bool] $SelfContained)

    return @('MishaWeb.exe', 'MishaWeb.dll')
}

function Get-LegacyOneClickNames {
    return @('MishaWeb.legacy')
}

function Test-ReleaseOutput {
    param(
        [Parameter(Mandatory = $true)][string] $OutputPath,
        [Parameter(Mandatory = $true)] $Definition
    )

    if ($script:forceValidationFailure) {
        throw 'forced pre-commit validation failure'
    }

    return [pscustomobject]@{ OutputPath = $OutputPath }
}

function Remove-ContainedDirectory {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Parent
    )

    [void] (Assert-ContainedPath -Candidate $Path -Parent $Parent -Label 'Recursive delete target')
    if ($script:forceCleanupFailure) {
        throw 'forced locked-backup cleanup failure'
    }

    if (Test-Path -LiteralPath $Path) {
        Remove-Item -Force -Recurse -LiteralPath $Path
    }
}

function New-ReleaseFixture {
    param([Parameter(Mandatory = $true)][string] $Name)

    $fixtureRepository = Join-Path $script:fixtureRoot $Name
    $fixtureBuild = Join-Path $fixtureRepository 'build'
    $fixtureStaging = Join-Path $script:fixtureRoot "$Name-staging"
    New-Item -ItemType Directory -Path $fixtureRepository, $fixtureBuild, $fixtureStaging | Out-Null

    $names = @((Get-PayloadNames -SelfContained $false) + $script:manifestName + $script:checksumName)
    foreach ($name in $names) {
        Set-Content -NoNewline -LiteralPath (Join-Path $fixtureRepository $name) -Value "old:$name"
        Set-Content -NoNewline -LiteralPath (Join-Path $fixtureStaging $name) -Value "new:$name"
    }
    Set-Content -NoNewline -LiteralPath (Join-Path $fixtureRepository 'MishaWeb.legacy') -Value 'old:legacy'

    return [pscustomobject]@{
        Repository = $fixtureRepository
        Build = $fixtureBuild
        Staging = $fixtureStaging
        Names = $names
        Definition = [pscustomobject]@{ OutputPath = $fixtureRepository }
    }
}

function Use-ReleaseFixture {
    param([Parameter(Mandatory = $true)] $Fixture)

    $script:repositoryRoot = $Fixture.Repository
    $script:buildRoot = $Fixture.Build
}

function Assert-True {
    param(
        [Parameter(Mandatory = $true)][bool] $Condition,
        [Parameter(Mandatory = $true)][string] $Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

$script:manifestName = 'MishaWeb.release.json'
$script:checksumName = 'MishaWeb.sha256'
$script:forceCleanupFailure = $false
$script:forceValidationFailure = $false
$script:fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    'MishaWeb.PublishTransaction.' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $script:fixtureRoot | Out-Null

try {
    $cleanupFixture = New-ReleaseFixture -Name 'cleanup-failure'
    Use-ReleaseFixture -Fixture $cleanupFixture
    $script:forceCleanupFailure = $true
    $cleanupWarnings = @()
    Move-OneClickRelease `
        -StagingPath $cleanupFixture.Staging `
        -Definition $cleanupFixture.Definition `
        -WarningVariable +cleanupWarnings

    foreach ($name in $cleanupFixture.Names) {
        Assert-True `
            -Condition ((Get-Content -Raw -LiteralPath (Join-Path $cleanupFixture.Repository $name)) -eq "new:$name") `
            -Message "Post-commit cleanup failure replaced '$name' with the old payload."
    }
    Assert-True `
        -Condition (-not (Test-Path -LiteralPath (Join-Path $cleanupFixture.Repository 'MishaWeb.legacy'))) `
        -Message 'Post-commit cleanup failure restored a legacy payload.'
    $retainedBackups = @(Get-ChildItem -Directory -LiteralPath $cleanupFixture.Build -Filter '.one-click-backup-*')
    Assert-True `
        -Condition ($retainedBackups.Count -eq 1) `
        -Message 'Locked post-commit backup was not retained for safe later cleanup.'
    Assert-True `
        -Condition (($cleanupWarnings -join "`n") -match 'installed and validated') `
        -Message 'Post-commit cleanup failure did not produce a clear warning.'

    $rollbackFixture = New-ReleaseFixture -Name 'validation-failure'
    Use-ReleaseFixture -Fixture $rollbackFixture
    $script:forceCleanupFailure = $false
    $script:forceValidationFailure = $true
    $validationFailed = $false
    try {
        Move-OneClickRelease -StagingPath $rollbackFixture.Staging -Definition $rollbackFixture.Definition
    }
    catch {
        $validationFailed = $_.Exception.Message -match 'forced pre-commit validation failure'
    }

    Assert-True -Condition $validationFailed -Message 'Pre-commit validation failure did not propagate.'
    foreach ($name in $rollbackFixture.Names) {
        Assert-True `
            -Condition ((Get-Content -Raw -LiteralPath (Join-Path $rollbackFixture.Repository $name)) -eq "old:$name") `
            -Message "Pre-commit validation failure did not restore '$name'."
    }
    Assert-True `
        -Condition ((Get-Content -Raw -LiteralPath (Join-Path $rollbackFixture.Repository 'MishaWeb.legacy')) -eq 'old:legacy') `
        -Message 'Pre-commit validation failure did not restore the legacy payload.'
    Assert-True `
        -Condition (@(Get-ChildItem -Directory -LiteralPath $rollbackFixture.Build -Filter '.one-click-backup-*').Count -eq 0) `
        -Message 'Pre-commit rollback left a backup directory behind.'

    Write-Host 'PASS: One-click publish keeps validated releases on cleanup failure and rolls back pre-commit failures.'
}
finally {
    $safeFixtureRoot = [System.IO.Path]::GetFullPath($script:fixtureRoot).TrimEnd('\', '/')
    $safeTempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\', '/')
    $tempPrefix = $safeTempRoot + [System.IO.Path]::DirectorySeparatorChar
    if (-not $safeFixtureRoot.StartsWith($tempPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not (Split-Path -Leaf $safeFixtureRoot).StartsWith('MishaWeb.PublishTransaction.', [System.StringComparison]::Ordinal)) {
        throw "Refusing to remove unexpected fixture path '$safeFixtureRoot'."
    }
    if (Test-Path -LiteralPath $safeFixtureRoot) {
        Remove-Item -Force -Recurse -LiteralPath $safeFixtureRoot
    }
}
