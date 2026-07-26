[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sourceRoots = @(
    (Join-Path $repositoryRoot 'src\MyPlasm.Inspector.App'),
    (Join-Path $repositoryRoot 'src\MyPlasm.Inspector.Core'),
    (Join-Path $repositoryRoot 'src\MyPlasm.Inspector.Transport.D2xx')
)
$prohibitedSymbols = @(
    'FT_Write',
    'FT_EE_',
    'FT_SetBaudRate',
    'FT_SetBitMode',
    'FT_SetDataCharacteristics',
    'FT_SetFlowControl',
    'FT_SetLatencyTimer',
    'FT_ResetDevice',
    'FT_Purge',
    'FT_EraseEE',
    'FT_Program',
    'FT_Firmware'
)

$sourceFiles = $sourceRoots |
    ForEach-Object {
        Get-ChildItem -LiteralPath $_ -Recurse -File -Filter '*.cs'
    } |
    Where-Object {
        $_.FullName -notmatch '[\\/](bin|obj)[\\/]'
    }

$violations = foreach ($file in $sourceFiles) {
    foreach ($symbol in $prohibitedSymbols) {
        Select-String -LiteralPath $file.FullName -SimpleMatch -Pattern $symbol |
            ForEach-Object {
                "$($_.Path):$($_.LineNumber): prohibited native symbol $symbol"
            }
    }
}

if ($violations) {
    $violations | Write-Error
    throw 'Production native safety audit failed.'
}

Write-Host "Production native safety audit passed for $($sourceFiles.Count) source files."
