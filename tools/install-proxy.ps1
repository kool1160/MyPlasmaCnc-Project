[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InstallationDirectory,

    [Parameter(Mandatory)]
    [string]$ProxyDllPath,

    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedApplicationHash = '0ec9f20ca46fb882257c610b25790e79474fa8f882a97d6b524e1b7b7b1447a9',

    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedOriginalDllHash = '381117c743766e3a696609bb29ca075772aa603cff196e16c3854c06ee1ab254'
)

$ErrorActionPreference = 'Stop'

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
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
Write-Host "MyPlasmCNC.exe SHA-256: $applicationHash"
Write-Host "Built proxy SHA-256:     $proxyHash"

if ($applicationHash -ne $ExpectedApplicationHash.ToLowerInvariant()) {
    throw 'MyPlasmCNC.exe does not match the expected evidence hash. No files were changed.'
}

if (Test-Path -LiteralPath $pendingRestorePath) {
    throw 'Unsafe partial state: ftd2xx_proxy_restore_pending.dll exists. No files were changed.'
}

if (Test-Path -LiteralPath $realDllPath) {
    if ((Test-Path -LiteralPath $activeDllPath -PathType Leaf) -and
        (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
        $activeHash = Get-Sha256 $activeDllPath
        $realHash = Get-Sha256 $realDllPath
        if ($activeHash -eq $manifest.proxySha256 -and
            $realHash -eq $manifest.originalDllSha256) {
            Write-Host 'Proxy is already installed and both DLL hashes match the installation record.'
            return
        }
    }
    throw 'Unsafe or partial state: ftd2xx_real.dll already exists. No files were changed.'
}

if (-not (Test-Path -LiteralPath $activeDllPath -PathType Leaf)) {
    throw 'The supplied directory does not contain ftd2xx.dll.'
}

$originalHash = Get-Sha256 $activeDllPath
Write-Host "Original ftd2xx.dll SHA-256: $originalHash"
if ($originalHash -ne $ExpectedOriginalDllHash.ToLowerInvariant()) {
    throw 'Active ftd2xx.dll does not match the expected original DLL hash. No files were changed.'
}

$writeProbe = Join-Path $installationPath ".myplasm-proxy-write-test-$([Guid]::NewGuid().ToString('N')).tmp"
try {
    New-Item -ItemType File -Path $writeProbe -ErrorAction Stop | Out-Null
}
finally {
    if (Test-Path -LiteralPath $writeProbe) {
        Remove-Item -LiteralPath $writeProbe -Force
    }
}

$manifest = [ordered]@{
    schemaVersion = 1
    state = 'installed'
    installedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    installationDirectory = $installationPath
    applicationSha256 = $applicationHash
    originalDllSha256 = $originalHash
    proxySha256 = $proxyHash
    originalDllName = 'ftd2xx_real.dll'
    proxyDllName = 'ftd2xx.dll'
}
$temporaryManifest = Join-Path $installationPath ".myplasm-proxy-install-$([Guid]::NewGuid().ToString('N')).tmp"
$manifest | ConvertTo-Json | Set-Content -LiteralPath $temporaryManifest -Encoding UTF8

$originalMoved = $false
$proxyCopied = $false
try {
    Move-Item -LiteralPath $activeDllPath -Destination $realDllPath
    $originalMoved = $true
    Copy-Item -LiteralPath $proxySource -Destination $activeDllPath
    $proxyCopied = $true
    if ((Get-Sha256 $activeDllPath) -ne $proxyHash) {
        throw 'Copied proxy hash does not match its source hash.'
    }
    if ((Get-Sha256 $realDllPath) -ne $originalHash) {
        throw 'Renamed original DLL hash changed unexpectedly.'
    }
    Move-Item -LiteralPath $temporaryManifest -Destination $manifestPath -Force
}
catch {
    if ($proxyCopied -and
        (Test-Path -LiteralPath $activeDllPath -PathType Leaf) -and
        (Get-Sha256 $activeDllPath) -eq $proxyHash) {
        Remove-Item -LiteralPath $activeDllPath -Force
    }
    if ($originalMoved -and
        (Test-Path -LiteralPath $realDllPath -PathType Leaf) -and
        -not (Test-Path -LiteralPath $activeDllPath)) {
        Move-Item -LiteralPath $realDllPath -Destination $activeDllPath
    }
    if (Test-Path -LiteralPath $temporaryManifest) {
        Remove-Item -LiteralPath $temporaryManifest -Force
    }
    throw
}

Write-Host 'Proxy installation completed safely.'
Write-Host "Active proxy: $activeDllPath"
Write-Host "Preserved original: $realDllPath"
Write-Host "Installation record: $manifestPath"
