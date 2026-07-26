[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$DllPath
)

$ErrorActionPreference = 'Stop'
$requiredExports = @(
    'FT_ListDevices',
    'FT_OpenEx',
    'FT_Close',
    'FT_Read',
    'FT_Write',
    'FT_SetBaudRate',
    'FT_SetDataCharacteristics',
    'FT_SetFlowControl',
    'FT_GetQueueStatus',
    'FT_SetLatencyTimer',
    'FT_SetBitMode'
) | Sort-Object

$dumpbin = Get-Command dumpbin.exe -ErrorAction SilentlyContinue
if (-not $dumpbin) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) {
        throw 'dumpbin.exe and vswhere.exe are unavailable.'
    }
    $installations = @(& $vswhere -all -products * -property installationPath)
    $candidate = foreach ($installation in $installations) {
        $toolsRoot = Join-Path $installation 'VC\Tools\MSVC'
        if (Test-Path -LiteralPath $toolsRoot) {
            Get-ChildItem -LiteralPath $toolsRoot -Directory |
                Sort-Object Name -Descending |
                ForEach-Object {
                    Join-Path $_.FullName 'bin\Hostx64\x86\dumpbin.exe'
                } |
                Where-Object { Test-Path -LiteralPath $_ }
        }
    }
    $candidate = $candidate | Select-Object -First 1
    if (-not $candidate) {
        throw 'Could not locate the x86 dumpbin.exe tool.'
    }
    $dumpbinPath = $candidate
}
else {
    $dumpbinPath = $dumpbin.Source
}

$headers = & $dumpbinPath /headers $DllPath
$headerText = $headers -join "`n"
if ($LASTEXITCODE -ne 0 -or $headerText -notmatch '14C machine \(x86\)') {
    throw 'The proxy DLL is not PE32/x86.'
}

$exportOutput = & $dumpbinPath /exports $DllPath
if ($LASTEXITCODE -ne 0) {
    throw 'dumpbin export inspection failed.'
}

$actualExports = @(
    $exportOutput |
        ForEach-Object {
            if ($_ -match '^\s+\d+\s+[0-9A-Fa-f]+\s+[0-9A-Fa-f]+\s+(\S+)\s*$') {
                $Matches[1]
            }
        } |
        Sort-Object
)

if (($actualExports -join ',') -ne ($requiredExports -join ',')) {
    throw @"
Proxy exports do not exactly match the required undecorated names.
Expected: $($requiredExports -join ', ')
Actual:   $($actualExports -join ', ')
"@
}

Write-Host 'PASS: proxy is PE32/x86 and exports exactly the 11 required undecorated names.'
