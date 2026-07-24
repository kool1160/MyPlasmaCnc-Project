[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InstallationDirectory,

    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedOriginalDllHash = '381117c743766e3a696609bb29ca075772aa603cff196e16c3854c06ee1ab254',

    [Parameter(DontShow)]
    [ValidateSet(
        '',
        'AfterProxyMovedAside',
        'RestoredOriginalPostMoveHashMismatch',
        'PendingProxyPostMoveHashMismatch')]
    [string]$TestFailurePoint = ''
)

$ErrorActionPreference = 'Stop'

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function New-UniquePath([string]$Path, [string]$Label) {
    $directory = Split-Path -Parent $Path
    $leaf = Split-Path -Leaf $Path
    $stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
    Join-Path $directory "$leaf.$Label.$stamp.$([Guid]::NewGuid().ToString('N'))"
}

function Move-ToQuarantine([string]$Path, [string]$Reason) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    $quarantinePath = New-UniquePath $Path "quarantine-$Reason"
    Move-Item -LiteralPath $Path -Destination $quarantinePath
    Write-Warning "Preserved '$Path' as quarantine '$quarantinePath'."
    $quarantinePath
}

function Get-HashOrMissing([string]$Path) {
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        return Get-Sha256 $Path
    }
    '<missing>'
}

function Test-RunnableState(
    [string]$ActivePath,
    [string]$RealPath,
    [string]$OriginalHash,
    [string]$ProxyHash) {
    $activeHash = Get-HashOrMissing $ActivePath
    $realHash = Get-HashOrMissing $RealPath
    ($activeHash -eq $OriginalHash) -or
        ($activeHash -eq $ProxyHash -and $realHash -eq $OriginalHash)
}

function Write-StateReport(
    [string]$ActivePath,
    [string]$RealPath,
    [string]$ManifestPath,
    [string]$OriginalHash,
    [string]$ProxyHash,
    [string[]]$QuarantinePaths) {
    Write-Host 'Resulting restoration state:'
    Write-Host "  ftd2xx.dll:             $(Get-HashOrMissing $ActivePath)"
    Write-Host "  ftd2xx_real.dll:        $(Get-HashOrMissing $RealPath)"
    Write-Host "  installation manifest:  $(Get-HashOrMissing $ManifestPath)"
    foreach ($path in @($QuarantinePaths | Where-Object { $_ })) {
        Write-Host "  quarantine $path : $(Get-HashOrMissing $path)"
    }
    $runnable = Test-RunnableState $ActivePath $RealPath $OriginalHash $ProxyHash
    Write-Host "  runnable verified state: $runnable"
    $runnable
}

function Invoke-TestFailure([string]$Point) {
    if ($TestFailurePoint -eq $Point) {
        throw "Injected restoration failure: $Point"
    }
}

if (Get-Process -Name 'MyPlasmCNC' -ErrorAction SilentlyContinue) {
    throw 'MyPlasmCNC.exe is running. Close it before restoring the original DLL.'
}

$installationItem = Get-Item -LiteralPath $InstallationDirectory -ErrorAction Stop
if (-not $installationItem.PSIsContainer) {
    throw 'InstallationDirectory must identify one existing directory.'
}
$installationPath = $installationItem.FullName
$applicationPath = Join-Path $installationPath 'MyPlasmCNC.exe'
$activeDllPath = Join-Path $installationPath 'ftd2xx.dll'
$realDllPath = Join-Path $installationPath 'ftd2xx_real.dll'
$pendingProxyPath = Join-Path $installationPath 'ftd2xx_proxy_restore_pending.dll'
$manifestPath = Join-Path $installationPath 'myplasm-proxy-install.json'
$expectedOriginal = $ExpectedOriginalDllHash.ToLowerInvariant()

if (-not (Test-Path -LiteralPath $applicationPath -PathType Leaf)) {
    throw 'The supplied directory does not contain MyPlasmCNC.exe.'
}

if (-not (Test-Path -LiteralPath $realDllPath)) {
    if (-not (Test-Path -LiteralPath $activeDllPath -PathType Leaf)) {
        throw 'Neither active ftd2xx.dll nor preserved ftd2xx_real.dll exists.'
    }

    $activeHash = Get-Sha256 $activeDllPath
    if ($activeHash -eq $expectedOriginal) {
        if (Test-Path -LiteralPath $pendingProxyPath) {
            if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
                throw 'Original DLL is active, but an unverified pending proxy and no manifest remain.'
            }
            $existingManifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
            if ((Get-Sha256 $pendingProxyPath) -ne $existingManifest.proxySha256) {
                throw 'Original DLL is active, but the pending proxy does not match the manifest.'
            }
            [void](Move-ToQuarantine $pendingProxyPath 'already-restored-proxy')
        }
        Write-Host 'Original FTDI DLL is already active. No controller-facing files were changed.'
        return
    }
    throw 'No preserved original DLL exists and the active DLL is not the known original. Refusing destructive action.'
}

if (Test-Path -LiteralPath $pendingProxyPath) {
    throw 'Unsafe partial state: ftd2xx_proxy_restore_pending.dll exists. No files were changed.'
}
if (-not (Test-Path -LiteralPath $activeDllPath -PathType Leaf)) {
    throw 'Unsafe partial state: ftd2xx_real.dll exists but active ftd2xx.dll is missing.'
}
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw 'Installation record is missing. The active DLL cannot be proven to be this proxy build.'
}

$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$activeHash = Get-Sha256 $activeDllPath
$realHash = Get-Sha256 $realDllPath
$applicationHash = Get-Sha256 $applicationPath
Write-Host "Active ftd2xx.dll SHA-256:          $activeHash"
Write-Host "Preserved ftd2xx_real.dll SHA-256: $realHash"
Write-Host "MyPlasmCNC.exe SHA-256:             $applicationHash"

if ($manifest.state -ne 'installed') {
    throw "Installation manifest state is '$($manifest.state)', not 'installed'."
}
if ($activeHash -ne $manifest.proxySha256) {
    throw 'Active ftd2xx.dll does not match the recorded proxy hash. No files were changed.'
}
if ($realHash -ne $manifest.originalDllSha256 -or
    $realHash -ne $expectedOriginal) {
    throw 'Preserved ftd2xx_real.dll does not match the recorded expected original hash. No files were changed.'
}
if ($applicationHash -ne $manifest.applicationSha256) {
    throw 'MyPlasmCNC.exe changed after proxy installation. No files were changed.'
}

$proxyBackup = New-UniquePath $activeDllPath 'transaction-proxy'
$originalBackup = New-UniquePath $realDllPath 'transaction-original'
$restoredManifest = New-UniquePath $manifestPath 'transaction-restored'
$manifestBackup = New-UniquePath $manifestPath 'transaction-backup'
$quarantinePaths = [Collections.Generic.List[string]]::new()
$manifestUpdated = $false
$proxyMoved = $false
$originalMoved = $false

try {
    Copy-Item -LiteralPath $activeDllPath -Destination $proxyBackup
    Copy-Item -LiteralPath $realDllPath -Destination $originalBackup
    if ((Get-Sha256 $proxyBackup) -ne $activeHash -or
        (Get-Sha256 $originalBackup) -ne $realHash) {
        throw 'Restoration transaction backup verification failed before any active file move.'
    }

    $manifest.state = 'restored'
    $manifest | Add-Member `
        -NotePropertyName restoredUtc `
        -NotePropertyValue ([DateTimeOffset]::UtcNow.ToString('O')) `
        -Force
    $manifest | ConvertTo-Json | Set-Content -LiteralPath $restoredManifest -Encoding UTF8

    Move-Item -LiteralPath $activeDllPath -Destination $pendingProxyPath
    $proxyMoved = $true
    Invoke-TestFailure 'AfterProxyMovedAside'
    if ($TestFailurePoint -eq 'PendingProxyPostMoveHashMismatch') {
        Add-Content -LiteralPath $pendingProxyPath -Value 'injected-corruption' -NoNewline
    }
    if ((Get-Sha256 $pendingProxyPath) -ne $activeHash) {
        throw 'Pending proxy hash verification failed after the move.'
    }

    Move-Item -LiteralPath $realDllPath -Destination $activeDllPath
    $originalMoved = $true
    if ($TestFailurePoint -eq 'RestoredOriginalPostMoveHashMismatch') {
        Add-Content -LiteralPath $activeDllPath -Value 'injected-corruption' -NoNewline
    }
    if ((Get-Sha256 $activeDllPath) -ne $realHash) {
        throw 'Restored-original hash verification failed after the move.'
    }
    if ((Get-Sha256 $pendingProxyPath) -ne $activeHash) {
        throw 'Pending-proxy hash verification failed after the original move.'
    }

    [IO.File]::Replace($restoredManifest, $manifestPath, $manifestBackup, $true)
    $manifestUpdated = $true
}
catch {
    $failure = $_
    try {
        if ($originalMoved -and
            (Test-Path -LiteralPath $activeDllPath -PathType Leaf)) {
            $currentActiveHash = Get-Sha256 $activeDllPath
            if ($currentActiveHash -eq $realHash -and
                -not (Test-Path -LiteralPath $realDllPath)) {
                Move-Item -LiteralPath $activeDllPath -Destination $realDllPath
            }
            else {
                $quarantinePaths.Add((Move-ToQuarantine $activeDllPath 'restore-active'))
            }
        }

        if (-not (Test-Path -LiteralPath $realDllPath) -and
            (Test-Path -LiteralPath $originalBackup -PathType Leaf) -and
            (Get-Sha256 $originalBackup) -eq $realHash) {
            Move-Item -LiteralPath $originalBackup -Destination $realDllPath
        }
        elseif ((Test-Path -LiteralPath $realDllPath -PathType Leaf) -and
            (Get-Sha256 $realDllPath) -ne $realHash) {
            $quarantinePaths.Add((Move-ToQuarantine $realDllPath 'restore-real'))
            if ((Test-Path -LiteralPath $originalBackup -PathType Leaf) -and
                (Get-Sha256 $originalBackup) -eq $realHash) {
                Move-Item -LiteralPath $originalBackup -Destination $realDllPath
            }
        }

        if ($proxyMoved -and
            (Test-Path -LiteralPath $pendingProxyPath -PathType Leaf)) {
            if ((Get-Sha256 $pendingProxyPath) -eq $activeHash -and
                -not (Test-Path -LiteralPath $activeDllPath) -and
                (Get-HashOrMissing $realDllPath) -eq $realHash) {
                Move-Item -LiteralPath $pendingProxyPath -Destination $activeDllPath
            }
            else {
                $quarantinePaths.Add((Move-ToQuarantine $pendingProxyPath 'restore-pending'))
            }
        }

        if (-not (Test-Path -LiteralPath $activeDllPath) -and
            (Test-Path -LiteralPath $proxyBackup -PathType Leaf) -and
            (Get-Sha256 $proxyBackup) -eq $activeHash -and
            (Get-HashOrMissing $realDllPath) -eq $realHash) {
            Move-Item -LiteralPath $proxyBackup -Destination $activeDllPath
        }

        if ($manifestUpdated -and
            (Test-Path -LiteralPath $manifestBackup -PathType Leaf)) {
            if (Test-Path -LiteralPath $manifestPath) {
                $quarantinePaths.Add((Move-ToQuarantine $manifestPath 'restore-manifest'))
            }
            Move-Item -LiteralPath $manifestBackup -Destination $manifestPath
        }

        foreach ($path in @(
                $restoredManifest,
                $manifestBackup,
                $proxyBackup,
                $originalBackup)) {
            if (Test-Path -LiteralPath $path) {
                $quarantinePaths.Add((Move-ToQuarantine $path 'restore-transaction'))
            }
        }

        if (Test-Path -LiteralPath $activeDllPath -PathType Leaf) {
            $finalActiveHash = Get-Sha256 $activeDllPath
            $finalRealHash = Get-HashOrMissing $realDllPath
            if ($finalActiveHash -ne $realHash -and
                -not ($finalActiveHash -eq $activeHash -and $finalRealHash -eq $realHash)) {
                $quarantinePaths.Add((Move-ToQuarantine $activeDllPath 'restore-unverified-active'))
            }
        }
    }
    catch {
        Write-Warning "Rollback encountered an additional error: $($_.Exception.Message)"
    }

    $runnable = Write-StateReport `
        $activeDllPath `
        $realDllPath `
        $manifestPath `
        $realHash `
        $activeHash `
        $quarantinePaths.ToArray()
    throw "Original restoration failed transactionally: $($failure.Exception.Message) Runnable verified state: $runnable"
}

if ((Get-Sha256 $activeDllPath) -ne $realHash) {
    throw 'Restoration reached an unsafe state: the active DLL is not the verified original.'
}

foreach ($path in @(
        $pendingProxyPath,
        $proxyBackup,
        $originalBackup,
        $manifestBackup,
        $restoredManifest)) {
    if (Test-Path -LiteralPath $path) {
        try {
            Remove-Item -LiteralPath $path -Force
        }
        catch {
            Write-Warning "Could not remove verified redundant file '$path': $($_.Exception.Message)"
            try {
                $quarantinePaths.Add((Move-ToQuarantine $path 'restore-success-cleanup'))
            }
            catch {
                Write-Warning "The redundant file remains at '$path': $($_.Exception.Message)"
                $quarantinePaths.Add($path)
            }
        }
    }
}

Write-Host 'Original FTDI DLL restored successfully.'
Write-Host 'Protocol capture logs were not modified.'
[void](Write-StateReport `
    $activeDllPath `
    $realDllPath `
    $manifestPath `
    $realHash `
    $activeHash `
    $quarantinePaths.ToArray())
