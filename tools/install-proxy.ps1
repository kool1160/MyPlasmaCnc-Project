[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InstallationDirectory,

    [Parameter(Mandatory)]
    [string]$ProxyDllPath,

    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedApplicationHash = '0ec9f20ca46fb882257c610b25790e79474fa8f882a97d6b524e1b7b7b1447a9',

    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedOriginalDllHash = '381117c743766e3a696609bb29ca075772aa603cff196e16c3854c06ee1ab254',

    [Parameter(DontShow)]
    [ValidateSet(
        '',
        'AfterOriginalMove',
        'AfterProxyCopy',
        'ProxyPostCopyHashMismatch',
        'ManifestWriteFailureAfterPlacement')]
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
    Write-Host 'Resulting installation state:'
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
        throw "Injected installation failure: $Point"
    }
}

if (Get-Process -Name 'MyPlasmCNC' -ErrorAction SilentlyContinue) {
    throw 'MyPlasmCNC.exe is running. Close it before installing the proxy.'
}

$installationItem = Get-Item -LiteralPath $InstallationDirectory -ErrorAction Stop
if (-not $installationItem.PSIsContainer) {
    throw 'InstallationDirectory must identify one existing directory.'
}
$installationPath = $installationItem.FullName
$proxySource = (Get-Item -LiteralPath $ProxyDllPath -ErrorAction Stop).FullName
if (-not (Test-Path -LiteralPath $proxySource -PathType Leaf)) {
    throw 'ProxyDllPath must identify one built proxy DLL.'
}

$applicationPath = Join-Path $installationPath 'MyPlasmCNC.exe'
$activeDllPath = Join-Path $installationPath 'ftd2xx.dll'
$realDllPath = Join-Path $installationPath 'ftd2xx_real.dll'
$pendingRestorePath = Join-Path $installationPath 'ftd2xx_proxy_restore_pending.dll'
$manifestPath = Join-Path $installationPath 'myplasm-proxy-install.json'

$applicationMatches = @(Get-ChildItem -LiteralPath $installationPath -File |
    Where-Object Name -eq 'MyPlasmCNC.exe')
if ($applicationMatches.Count -ne 1 -or
    -not (Test-Path -LiteralPath $applicationPath -PathType Leaf)) {
    throw 'The supplied directory is ambiguous or does not contain exactly one MyPlasmCNC.exe.'
}

$applicationHash = Get-Sha256 $applicationPath
$proxyHash = Get-Sha256 $proxySource
$expectedApplication = $ExpectedApplicationHash.ToLowerInvariant()
$expectedOriginal = $ExpectedOriginalDllHash.ToLowerInvariant()
Write-Host "MyPlasmCNC.exe SHA-256: $applicationHash"
Write-Host "Built proxy SHA-256:     $proxyHash"

if ($applicationHash -ne $expectedApplication) {
    throw 'MyPlasmCNC.exe does not match the expected evidence hash. No files were changed.'
}

if (Test-Path -LiteralPath $pendingRestorePath) {
    throw 'Unsafe partial state: ftd2xx_proxy_restore_pending.dll exists. No files were changed.'
}

if (Test-Path -LiteralPath $realDllPath) {
    if ((Test-Path -LiteralPath $activeDllPath -PathType Leaf) -and
        (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        $existingManifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
        $activeHash = Get-Sha256 $activeDllPath
        $realHash = Get-Sha256 $realDllPath
        if ($existingManifest.state -eq 'installed' -and
            $activeHash -eq $existingManifest.proxySha256 -and
            $realHash -eq $existingManifest.originalDllSha256) {
            Write-Host 'Proxy is already installed and both DLL hashes match the installation record.'
            [void](Write-StateReport `
                $activeDllPath `
                $realDllPath `
                $manifestPath `
                $realHash `
                $activeHash `
                @())
            return
        }
    }
    throw 'Unsafe or partial state: ftd2xx_real.dll already exists. No files were changed.'
}

if (Test-Path -LiteralPath $manifestPath) {
    throw 'Unsafe partial state: an installation manifest exists without ftd2xx_real.dll.'
}
if (-not (Test-Path -LiteralPath $activeDllPath -PathType Leaf)) {
    throw 'The supplied directory does not contain ftd2xx.dll.'
}

$originalHash = Get-Sha256 $activeDllPath
Write-Host "Original ftd2xx.dll SHA-256: $originalHash"
if ($originalHash -ne $expectedOriginal) {
    throw 'Active ftd2xx.dll does not match the expected original DLL hash. No files were changed.'
}

$writeProbe = New-UniquePath (Join-Path $installationPath '.myplasm-proxy-write-test.tmp') 'probe'
try {
    New-Item -ItemType File -Path $writeProbe -ErrorAction Stop | Out-Null
}
finally {
    if (Test-Path -LiteralPath $writeProbe) {
        Remove-Item -LiteralPath $writeProbe -Force
    }
}

$manifest = [ordered]@{
    schemaVersion = 2
    state = 'installed'
    installedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    installationDirectory = $installationPath
    applicationSha256 = $applicationHash
    originalDllSha256 = $originalHash
    proxySha256 = $proxyHash
    originalDllName = 'ftd2xx_real.dll'
    proxyDllName = 'ftd2xx.dll'
}
$temporaryManifest = New-UniquePath $manifestPath 'transaction'
$originalBackup = New-UniquePath $activeDllPath 'transaction-original'
$quarantinePaths = [Collections.Generic.List[string]]::new()

$manifest | ConvertTo-Json | Set-Content -LiteralPath $temporaryManifest -Encoding UTF8

try {
    Copy-Item -LiteralPath $activeDllPath -Destination $originalBackup
    if ((Get-Sha256 $originalBackup) -ne $originalHash) {
        throw 'Transaction backup of the original DLL failed hash verification.'
    }

    Move-Item -LiteralPath $activeDllPath -Destination $realDllPath
    Invoke-TestFailure 'AfterOriginalMove'

    Copy-Item -LiteralPath $proxySource -Destination $activeDllPath
    Invoke-TestFailure 'AfterProxyCopy'
    if ($TestFailurePoint -eq 'ProxyPostCopyHashMismatch') {
        Add-Content -LiteralPath $activeDllPath -Value 'injected-corruption' -NoNewline
    }
    if ((Get-Sha256 $activeDllPath) -ne $proxyHash) {
        throw 'Copied proxy hash does not match its source hash.'
    }
    if ((Get-Sha256 $realDllPath) -ne $originalHash) {
        throw 'Renamed original DLL hash changed unexpectedly.'
    }

    Invoke-TestFailure 'ManifestWriteFailureAfterPlacement'
    Move-Item -LiteralPath $temporaryManifest -Destination $manifestPath
}
catch {
    $failure = $_
    try {
        if (Test-Path -LiteralPath $activeDllPath -PathType Leaf) {
            $activeHash = Get-Sha256 $activeDllPath
            if ($activeHash -ne $originalHash) {
                $quarantinePaths.Add((Move-ToQuarantine $activeDllPath 'install-active'))
            }
        }

        if (Test-Path -LiteralPath $realDllPath -PathType Leaf) {
            $realHash = Get-Sha256 $realDllPath
            if ($realHash -eq $originalHash -and
                -not (Test-Path -LiteralPath $activeDllPath)) {
                Move-Item -LiteralPath $realDllPath -Destination $activeDllPath
            }
            elseif ($realHash -ne $originalHash) {
                $quarantinePaths.Add((Move-ToQuarantine $realDllPath 'install-real'))
            }
        }

        if (-not (Test-Path -LiteralPath $activeDllPath) -and
            (Test-Path -LiteralPath $originalBackup -PathType Leaf) -and
            (Get-Sha256 $originalBackup) -eq $originalHash) {
            Move-Item -LiteralPath $originalBackup -Destination $activeDllPath
        }

        if ((Test-Path -LiteralPath $activeDllPath -PathType Leaf) -and
            (Get-Sha256 $activeDllPath) -ne $originalHash) {
            $quarantinePaths.Add((Move-ToQuarantine $activeDllPath 'install-unverified-active'))
        }
        if (Test-Path -LiteralPath $temporaryManifest) {
            $quarantinePaths.Add((Move-ToQuarantine $temporaryManifest 'install-manifest'))
        }
        if (Test-Path -LiteralPath $originalBackup) {
            $quarantinePaths.Add((Move-ToQuarantine $originalBackup 'install-backup'))
        }
    }
    catch {
        Write-Warning "Rollback encountered an additional error: $($_.Exception.Message)"
    }

    $runnable = Write-StateReport `
        $activeDllPath `
        $realDllPath `
        $manifestPath `
        $originalHash `
        $proxyHash `
        $quarantinePaths.ToArray()
    throw "Proxy installation failed transactionally: $($failure.Exception.Message) Runnable verified state: $runnable"
}

if (Test-Path -LiteralPath $originalBackup) {
    try {
        Remove-Item -LiteralPath $originalBackup -Force
    }
    catch {
        Write-Warning "Could not remove the verified redundant transaction backup: $($_.Exception.Message)"
        try {
            $quarantinePaths.Add((Move-ToQuarantine $originalBackup 'install-success-backup'))
        }
        catch {
            Write-Warning "The redundant backup remains at '$originalBackup': $($_.Exception.Message)"
            $quarantinePaths.Add($originalBackup)
        }
    }
}

Write-Host 'Proxy installation completed safely.'
Write-Host "Active proxy: $activeDllPath"
Write-Host "Preserved original: $realDllPath"
Write-Host "Installation record: $manifestPath"
[void](Write-StateReport `
    $activeDllPath `
    $realDllPath `
    $manifestPath `
    $originalHash `
    $proxyHash `
    $quarantinePaths.ToArray())
