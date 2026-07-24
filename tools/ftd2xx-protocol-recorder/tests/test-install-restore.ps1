[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ProxyDll,

    [Parameter(Mandatory)]
    [string]$MockDll,

    [Parameter(Mandatory)]
    [string]$InstallScript,

    [Parameter(Mandatory)]
    [string]$RestoreScript,

    [Parameter(Mandatory)]
    [string]$WorkingRoot
)

$ErrorActionPreference = 'Stop'

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-Throws([scriptblock]$Action, [string]$Description) {
    $threw = $false
    try {
        & $Action
    }
    catch {
        $threw = $true
        Write-Host "Expected failure '$Description': $($_.Exception.Message)"
    }
    if (-not $threw) {
        throw "Expected refusal did not occur: $Description"
    }
}

function New-Fixture([string]$Name) {
    $directory = Join-Path $script:root $Name
    New-Item -ItemType Directory -Path $directory | Out-Null
    $application = Join-Path $directory 'MyPlasmCNC.exe'
    $active = Join-Path $directory 'ftd2xx.dll'
    Set-Content -LiteralPath $application -Value "synthetic application fixture: $Name" -NoNewline
    Copy-Item -LiteralPath $script:MockDll -Destination $active
    @{
        Directory = $directory
        Application = $application
        Active = $active
        Real = Join-Path $directory 'ftd2xx_real.dll'
        Pending = Join-Path $directory 'ftd2xx_proxy_restore_pending.dll'
        Manifest = Join-Path $directory 'myplasm-proxy-install.json'
        ApplicationHash = Get-Sha256 $application
        OriginalHash = Get-Sha256 $active
        ProxyHash = Get-Sha256 $script:ProxyDll
    }
}

function Invoke-Install([hashtable]$Fixture, [string]$FailurePoint = '') {
    $arguments = @{
        InstallationDirectory = $Fixture.Directory
        ProxyDllPath = $script:ProxyDll
        ExpectedApplicationHash = $Fixture.ApplicationHash
        ExpectedOriginalDllHash = $Fixture.OriginalHash
    }
    if ($FailurePoint) {
        $arguments.TestFailurePoint = $FailurePoint
    }
    & $script:InstallScript @arguments
}

function Invoke-Restore([hashtable]$Fixture, [string]$FailurePoint = '') {
    $arguments = @{
        InstallationDirectory = $Fixture.Directory
        ExpectedOriginalDllHash = $Fixture.OriginalHash
    }
    if ($FailurePoint) {
        $arguments.TestFailurePoint = $FailurePoint
    }
    & $script:RestoreScript @arguments
}

function Get-QuarantineFiles([hashtable]$Fixture) {
    @(
        Get-ChildItem -LiteralPath $Fixture.Directory -File |
            Where-Object Name -Like '*.quarantine-*' |
            Sort-Object Name
    )
}

function Assert-NoTransactionNames([hashtable]$Fixture) {
    $unexpected = @(
        Get-ChildItem -LiteralPath $Fixture.Directory -File |
            Where-Object {
                $_.Name -notlike '*.quarantine-*' -and
                ($_.Name -like '*.transaction-*' -or
                 $_.Name -eq 'ftd2xx_proxy_restore_pending.dll')
            })
    if ($unexpected.Count -ne 0) {
        throw "Unexpected live transaction files: $($unexpected.Name -join ', ')"
    }
}

function Assert-Manifest(
    [hashtable]$Fixture,
    [AllowNull()][string]$ExpectedState) {
    if ([string]::IsNullOrEmpty($ExpectedState)) {
        if (Test-Path -LiteralPath $Fixture.Manifest) {
            throw 'Manifest exists when the exact expected state is no manifest.'
        }
        return
    }

    if (-not (Test-Path -LiteralPath $Fixture.Manifest -PathType Leaf)) {
        throw "Manifest is missing; expected state '$ExpectedState'."
    }
    $manifest = Get-Content -Raw -LiteralPath $Fixture.Manifest | ConvertFrom-Json
    if ($manifest.state -ne $ExpectedState -or
        $manifest.applicationSha256 -ne $Fixture.ApplicationHash -or
        $manifest.originalDllSha256 -ne $Fixture.OriginalHash -or
        $manifest.proxySha256 -ne $Fixture.ProxyHash) {
        throw "Manifest contents do not match the exact '$ExpectedState' fixture state."
    }
}

function Assert-OriginalRunnable(
    [hashtable]$Fixture,
    [AllowNull()][string]$ManifestState) {
    if ((Get-Sha256 $Fixture.Active) -ne $Fixture.OriginalHash) {
        throw 'The exact active DLL is not the verified original.'
    }
    if (Test-Path -LiteralPath $Fixture.Real) {
        throw 'A preserved real DLL unexpectedly remains beside the active original.'
    }
    Assert-Manifest $Fixture $ManifestState
    Assert-NoTransactionNames $Fixture
}

function Assert-ProxyRunnable([hashtable]$Fixture) {
    if ((Get-Sha256 $Fixture.Active) -ne $Fixture.ProxyHash -or
        (Get-Sha256 $Fixture.Real) -ne $Fixture.OriginalHash) {
        throw 'The exact active proxy/preserved-original pair is not runnable.'
    }
    Assert-Manifest $Fixture 'installed'
    Assert-NoTransactionNames $Fixture
}

function Assert-QuarantineManifest([hashtable]$Fixture) {
    $matches = @(
        Get-QuarantineFiles $Fixture |
            Where-Object Name -Like 'myplasm-proxy-install.json*')
    if ($matches.Count -ne 1) {
        throw "Expected exactly one quarantined transaction manifest, found $($matches.Count)."
    }
    $manifest = Get-Content -Raw -LiteralPath $matches[0].FullName | ConvertFrom-Json
    if ($manifest.state -notin @('installed', 'restored') -or
        $manifest.originalDllSha256 -ne $Fixture.OriginalHash -or
        $manifest.proxySha256 -ne $Fixture.ProxyHash) {
        throw 'Quarantined transaction manifest did not preserve exact hashes and state.'
    }
}

$root = [IO.Path]::GetFullPath($WorkingRoot)
if ([IO.Path]::GetFileName($root.TrimEnd('\')) -ne 'install-restore-tests') {
    throw 'WorkingRoot must end in the dedicated install-restore-tests directory.'
}
if (Test-Path -LiteralPath $root) {
    Remove-Item -LiteralPath $root -Recurse -Force
}
New-Item -ItemType Directory -Path $root | Out-Null

# Normal installation, idempotence, restoration, capture preservation, and
# restoration idempotence.
$safe = New-Fixture 'safe'
Invoke-Install $safe
Assert-ProxyRunnable $safe
Invoke-Install $safe
Assert-ProxyRunnable $safe

$capturePath = Join-Path $safe.Directory 'captures\preserve-me.jsonl'
New-Item -ItemType Directory -Path (Split-Path -Parent $capturePath) | Out-Null
Set-Content -LiteralPath $capturePath -Value '{"evidence":true}' -NoNewline
Invoke-Restore $safe
Assert-OriginalRunnable $safe 'restored'
if (-not (Test-Path -LiteralPath $capturePath -PathType Leaf)) {
    throw 'Restoration removed protocol capture evidence.'
}
Invoke-Restore $safe
Assert-OriginalRunnable $safe 'restored'

# Installation rollback failure injection. Each failed transaction must return
# to the verified original, leave no standard partial state, preserve evidence
# in uniquely named quarantine files, and leave no manifest claiming install.
$installFailurePoints = @(
    'AfterOriginalMove',
    'AfterProxyCopy',
    'ProxyPostCopyHashMismatch',
    'ManifestWriteFailureAfterPlacement')
foreach ($point in $installFailurePoints) {
    $fixture = New-Fixture "install-$point"
    Assert-Throws { Invoke-Install $fixture $point } $point
    Assert-OriginalRunnable $fixture $null
    $quarantine = @(Get-QuarantineFiles $fixture)
    $expectedCount = if ($point -eq 'AfterOriginalMove') { 2 } else { 3 }
    if ($quarantine.Count -ne $expectedCount) {
        throw "$point preserved $($quarantine.Count) quarantine files; expected exactly $expectedCount."
    }
    if (@($quarantine | Where-Object { (Get-Sha256 $_.FullName) -eq $fixture.OriginalHash }).Count -ne 1) {
        throw "$point did not preserve exactly one verified original transaction backup."
    }
    if ($point -in @('AfterProxyCopy', 'ManifestWriteFailureAfterPlacement') -and
        @($quarantine | Where-Object { (Get-Sha256 $_.FullName) -eq $fixture.ProxyHash }).Count -ne 1) {
        throw "$point did not quarantine the exact copied proxy."
    }
    if ($point -eq 'ProxyPostCopyHashMismatch') {
        $activeQuarantine = @($quarantine | Where-Object Name -Like 'ftd2xx.dll.quarantine-install-active*')
        if ($activeQuarantine.Count -ne 1 -or
            (Get-Sha256 $activeQuarantine[0].FullName) -in @($fixture.OriginalHash, $fixture.ProxyHash)) {
            throw 'Proxy hash-mismatch test did not preserve the unexpected copied DLL exactly once.'
        }
    }
    Assert-QuarantineManifest $fixture
}

# A failure-recovered installation is safely repeatable.
$retryInstall = New-Fixture 'install-retry'
Assert-Throws {
    Invoke-Install $retryInstall 'ManifestWriteFailureAfterPlacement'
} 'manifest write retry setup'
Assert-OriginalRunnable $retryInstall $null
Invoke-Install $retryInstall
Assert-ProxyRunnable $retryInstall

# Restoration rollback failure injection. Every case must reconstruct the exact
# verified proxy + original pair and retain the installed manifest.
$restoreFailurePoints = @(
    'AfterProxyMovedAside',
    'RestoredOriginalPostMoveHashMismatch',
    'PendingProxyPostMoveHashMismatch')
foreach ($point in $restoreFailurePoints) {
    $fixture = New-Fixture "restore-$point"
    Invoke-Install $fixture
    Assert-Throws { Invoke-Restore $fixture $point } $point
    Assert-ProxyRunnable $fixture
    $quarantine = @(Get-QuarantineFiles $fixture)
    if ($quarantine.Count -ne 3) {
        throw "$point preserved $($quarantine.Count) quarantine files; expected exactly 3."
    }
    Assert-QuarantineManifest $fixture

    if ($point -eq 'PendingProxyPostMoveHashMismatch') {
        $pending = @($quarantine | Where-Object Name -Like 'ftd2xx_proxy_restore_pending.dll*')
        if ($pending.Count -ne 1 -or
            (Get-Sha256 $pending[0].FullName) -in @($fixture.ProxyHash, $fixture.OriginalHash)) {
            throw 'Pending-proxy mismatch was not preserved as one unknown quarantine file.'
        }
    }
    if ($point -eq 'RestoredOriginalPostMoveHashMismatch') {
        $restored = @($quarantine | Where-Object Name -Like 'ftd2xx.dll.quarantine-restore-active*')
        if ($restored.Count -ne 1 -or
            (Get-Sha256 $restored[0].FullName) -in @($fixture.ProxyHash, $fixture.OriginalHash)) {
            throw 'Restored-original mismatch was not preserved as one unknown quarantine file.'
        }
    }

    # Repeating after rollback must remain safe and complete restoration.
    Invoke-Restore $fixture
    Assert-OriginalRunnable $fixture 'restored'
}

# Existing fail-closed preflight checks remain deterministic.
$partial = New-Fixture 'partial-install'
Copy-Item -LiteralPath $MockDll -Destination $partial.Real
Assert-Throws { Invoke-Install $partial } 'partial install state'

$mismatch = New-Fixture 'original-hash-mismatch'
$mismatch.OriginalHash = '0' * 64
Assert-Throws { Invoke-Install $mismatch } 'original DLL hash mismatch'

$tampered = New-Fixture 'tampered-proxy'
Invoke-Install $tampered
Set-Content -LiteralPath $tampered.Active -Value 'tampered active proxy' -NoNewline
Assert-Throws { Invoke-Restore $tampered } 'active proxy hash mismatch during restoration'
if ((Get-Sha256 $tampered.Real) -ne $tampered.OriginalHash) {
    throw 'Preflight refusal changed the preserved original.'
}

Write-Host 'PASS: install/restore transactions, rollback injection, quarantine, hashes, manifests, runnability, and idempotence are verified.'
