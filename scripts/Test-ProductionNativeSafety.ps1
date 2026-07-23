[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $repositoryRoot 'src'
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

$sourceFiles = Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -Filter '*.cs' |
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
