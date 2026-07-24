[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InstallationDirectory,

    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedOriginalDllHash = '381117c743766e3a696609bb29ca075772aa603cff196e16c3854c06ee1ab254'
)

$ErrorActionPreference = 'Stop'

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
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

if (-not (Test-Path -LiteralPath $applicationPath -PathType Leaf)) {
    throw 'The supplied directory does not contain MyPlasmCNC.exe.'
}
if (Test-Path -LiteralPath $pendingProxyPath) {
    throw 'Unsafe partial state: ftd2xx_proxy_restore_pending.dll exists. No files were changed.'
}

if (-not (Test-Path -LiteralPath $realDllPath)) {
    if (-not (Test-Path -LiteralPath $activeDllPath -PathType Leaf)) {
        throw 'Neither active ftd2xx.dll nor preserved ftd2xx_real.dll exists.'
    }

    $activeHash = Get-Sha256 $activeDllPath
    if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
        $existingManifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
        if ($activeHash -eq $existingManifest.originalDllSha256) {
            Write-Host 'Original FTDI DLL is already restored. No files were changed.'
            return
        }
    }
    if ($activeHash -eq $ExpectedOriginalDllHash.ToLowerInvariant()) {
        Write-Host 'Original FTDI DLL is already active. No files were changed.'
        return
    }
    throw 'No preserved original DLL exists and the active DLL is not the known original. Refusing destructive action.'
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
Write-Host "Active ftd2xx.dll SHA-256:       $activeHash"
Write-Host "Preserved ftd2xx_real.dll SHA-256: $realHash"
Write-Host "MyPlasmCNC.exe SHA-256:          $applicationHash"

if ($activeHash -ne $manifest.proxySha256) {
    throw 'Active ftd2xx.dll does not match the recorded proxy hash. No files were changed.'
}
if ($realHash -ne $manifest.originalDllSha256 -or
    $realHash -ne $ExpectedOriginalDllHash.ToLowerInvariant()) {
    throw 'Preserved ftd2xx_real.dll does not match the recorded expected original hash. No files were changed.'
}
if ($applicationHash -ne $manifest.applicationSha256) {
    throw 'MyPlasmCNC.exe changed after proxy installation. No files were changed.'
}

Move-Item -LiteralPath $activeDllPath -Destination $pendingProxyPath
try {
    Move-Item -LiteralPath $realDllPath -Destination $activeDllPath
    if ((Get-Sha256 $activeDllPath) -ne $realHash) {
        throw 'Restored DLL hash verification failed.'
    }
    if ((Get-Sha256 $pendingProxyPath) -ne $activeHash) {
        throw 'Pending proxy hash changed unexpectedly.'
    }
    Remove-Item -LiteralPath $pendingProxyPath -Force
}
catch {
    if (-not (Test-Path -LiteralPath $activeDllPath) -and
        (Test-Path -LiteralPath $pendingProxyPath -PathType Leaf)) {
        Move-Item -LiteralPath $pendingProxyPath -Destination $activeDllPath
    }
    throw 'Restoration failed safely. The verified proxy was retained or restored, and the original was not guessed.'
}

$manifest.state = 'restored'
$manifest | Add-Member -NotePropertyName restoredUtc -NotePropertyValue ([DateTimeOffset]::UtcNow.ToString('O')) -Force
$manifest | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding UTF8

Write-Host 'Original FTDI DLL restored successfully.'
Write-Host 'Protocol capture logs were not modified.'
