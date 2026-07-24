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
    }
    if (-not $threw) {
        throw "Expected refusal did not occur: $Description"
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

$safeInstall = Join-Path $root 'safe-install'
New-Item -ItemType Directory -Path $safeInstall | Out-Null
$applicationPath = Join-Path $safeInstall 'MyPlasmCNC.exe'
$activeDllPath = Join-Path $safeInstall 'ftd2xx.dll'
Set-Content -LiteralPath $applicationPath -Value 'synthetic application fixture' -NoNewline
Copy-Item -LiteralPath $MockDll -Destination $activeDllPath
$applicationHash = Get-Sha256 $applicationPath
$originalHash = Get-Sha256 $activeDllPath
$proxyHash = Get-Sha256 $ProxyDll

& $InstallScript `
    -InstallationDirectory $safeInstall `
    -ProxyDllPath $ProxyDll `
    -ExpectedApplicationHash $applicationHash `
    -ExpectedOriginalDllHash $originalHash
if ((Get-Sha256 $activeDllPath) -ne $proxyHash) {
    throw 'Installer did not activate the proxy.'
}
if ((Get-Sha256 (Join-Path $safeInstall 'ftd2xx_real.dll')) -ne $originalHash) {
    throw 'Installer did not preserve the original DLL.'
}

& $InstallScript `
    -InstallationDirectory $safeInstall `
    -ProxyDllPath $ProxyDll `
    -ExpectedApplicationHash $applicationHash `
    -ExpectedOriginalDllHash $originalHash

$capturePath = Join-Path $safeInstall 'captures\preserve-me.jsonl'
New-Item -ItemType Directory -Path (Split-Path -Parent $capturePath) | Out-Null
Set-Content -LiteralPath $capturePath -Value '{"evidence":true}' -NoNewline

& $RestoreScript `
    -InstallationDirectory $safeInstall `
    -ExpectedOriginalDllHash $originalHash
if ((Get-Sha256 $activeDllPath) -ne $originalHash) {
    throw 'Restoration did not reactivate the original DLL.'
}
if (Test-Path -LiteralPath (Join-Path $safeInstall 'ftd2xx_real.dll')) {
    throw 'Restoration left an unexpected preserved DLL.'
}
if (-not (Test-Path -LiteralPath $capturePath -PathType Leaf)) {
    throw 'Restoration removed protocol capture evidence.'
}

& $RestoreScript `
    -InstallationDirectory $safeInstall `
    -ExpectedOriginalDllHash $originalHash

$partialInstall = Join-Path $root 'partial-install'
New-Item -ItemType Directory -Path $partialInstall | Out-Null
Set-Content -LiteralPath (Join-Path $partialInstall 'MyPlasmCNC.exe') -Value 'fixture' -NoNewline
Copy-Item -LiteralPath $MockDll -Destination (Join-Path $partialInstall 'ftd2xx.dll')
Copy-Item -LiteralPath $MockDll -Destination (Join-Path $partialInstall 'ftd2xx_real.dll')
$partialAppHash = Get-Sha256 (Join-Path $partialInstall 'MyPlasmCNC.exe')
Assert-Throws {
    & $InstallScript `
        -InstallationDirectory $partialInstall `
        -ProxyDllPath $ProxyDll `
        -ExpectedApplicationHash $partialAppHash `
        -ExpectedOriginalDllHash $originalHash
} 'partial install state'

$mismatchInstall = Join-Path $root 'hash-mismatch'
New-Item -ItemType Directory -Path $mismatchInstall | Out-Null
Set-Content -LiteralPath (Join-Path $mismatchInstall 'MyPlasmCNC.exe') -Value 'fixture' -NoNewline
Copy-Item -LiteralPath $MockDll -Destination (Join-Path $mismatchInstall 'ftd2xx.dll')
$mismatchAppHash = Get-Sha256 (Join-Path $mismatchInstall 'MyPlasmCNC.exe')
Assert-Throws {
    & $InstallScript `
        -InstallationDirectory $mismatchInstall `
        -ProxyDllPath $ProxyDll `
        -ExpectedApplicationHash $mismatchAppHash `
        -ExpectedOriginalDllHash ('0' * 64)
} 'original DLL hash mismatch'

$restoreMismatch = Join-Path $root 'restore-hash-mismatch'
New-Item -ItemType Directory -Path $restoreMismatch | Out-Null
$restoreApplication = Join-Path $restoreMismatch 'MyPlasmCNC.exe'
$restoreActive = Join-Path $restoreMismatch 'ftd2xx.dll'
Set-Content -LiteralPath $restoreApplication -Value 'restore fixture' -NoNewline
Copy-Item -LiteralPath $MockDll -Destination $restoreActive
$restoreApplicationHash = Get-Sha256 $restoreApplication
& $InstallScript `
    -InstallationDirectory $restoreMismatch `
    -ProxyDllPath $ProxyDll `
    -ExpectedApplicationHash $restoreApplicationHash `
    -ExpectedOriginalDllHash $originalHash
Set-Content -LiteralPath $restoreActive -Value 'tampered active proxy' -NoNewline
Assert-Throws {
    & $RestoreScript `
        -InstallationDirectory $restoreMismatch `
        -ExpectedOriginalDllHash $originalHash
} 'active proxy hash mismatch during restoration'
if (-not (Test-Path -LiteralPath (Join-Path $restoreMismatch 'ftd2xx_real.dll') -PathType Leaf)) {
    throw 'Unsafe restoration removed the preserved original after a hash mismatch.'
}

Write-Host 'PASS: install and restore scripts are hash-aware, state-aware, idempotent, and preserve captures.'
