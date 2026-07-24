[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet(
        'normal',
        'default_location',
        'missing_real',
        'missing_export',
        'failed_initialization',
        'reentrant',
        'concurrent',
        'logging_failure',
        'mock_error',
        'empty_read',
        'zero_write',
        'large_payload')]
    [string]$CaseName,

    [Parameter(Mandatory)]
    [string]$ProxyDll,

    [Parameter(Mandatory)]
    [string]$MockDll,

    [Parameter(Mandatory)]
    [string]$MissingExportMockDll,

    [Parameter(Mandatory)]
    [string]$TestHost,

    [Parameter(Mandatory)]
    [string]$WorkingRoot
)

$ErrorActionPreference = 'Stop'

$resolvedWorkingRoot = [IO.Path]::GetFullPath($WorkingRoot)
if ([IO.Path]::GetFileName($resolvedWorkingRoot.TrimEnd('\')) -ne 'test-runs') {
    throw 'WorkingRoot must end in the dedicated test-runs directory.'
}
$caseDirectory = [IO.Path]::GetFullPath((Join-Path $resolvedWorkingRoot $CaseName))
if (-not $caseDirectory.StartsWith(
        $resolvedWorkingRoot.TrimEnd('\') + '\',
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The test case directory escaped the configured working root.'
}

if (Test-Path -LiteralPath $caseDirectory) {
    Remove-Item -LiteralPath $caseDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $caseDirectory -Force | Out-Null

$caseProxy = Join-Path $caseDirectory 'ftd2xx.dll'
Copy-Item -LiteralPath $ProxyDll -Destination $caseProxy

if ($CaseName -eq 'missing_export') {
    Copy-Item -LiteralPath $MissingExportMockDll -Destination (Join-Path $caseDirectory 'ftd2xx_real.dll')
}
elseif ($CaseName -eq 'failed_initialization') {
    Set-Content -LiteralPath (Join-Path $caseDirectory 'ftd2xx_real.dll') -Value 'not a PE file' -NoNewline
}
elseif ($CaseName -ne 'missing_real') {
    Copy-Item -LiteralPath $MockDll -Destination (Join-Path $caseDirectory 'ftd2xx_real.dll')
}

$captureDirectory = Join-Path $caseDirectory 'capture'
$previousLogDirectory = $env:MYPLASM_PROXY_LOG_DIR
$previousLocalAppData = $env:LOCALAPPDATA
$previousMockError = $env:MOCK_FTDI_ERROR
$previousEmptyRead = $env:MOCK_EMPTY_READ
$previousReentry = $env:MOCK_REENTER
try {
    if ($CaseName -eq 'default_location') {
        $env:MYPLASM_PROXY_LOG_DIR = $null
        $env:LOCALAPPDATA = Join-Path $caseDirectory 'localappdata'
    }
    elseif ($CaseName -eq 'logging_failure') {
        $blockedPath = Join-Path $caseDirectory 'not-a-directory'
        Set-Content -LiteralPath $blockedPath -Value 'blocks directory creation' -NoNewline
        $env:MYPLASM_PROXY_LOG_DIR = $blockedPath
    }
    else {
        $env:MYPLASM_PROXY_LOG_DIR = $captureDirectory
    }

    $env:MOCK_FTDI_ERROR = if ($CaseName -eq 'mock_error') { '1' } else { $null }
    $env:MOCK_EMPTY_READ = if ($CaseName -eq 'empty_read') { '1' } else { $null }
    $env:MOCK_REENTER = if ($CaseName -eq 'reentrant') { '1' } else { $null }

    & $TestHost $CaseName $caseProxy
    if ($LASTEXITCODE -ne 0) {
        throw "Test host failed for case '$CaseName' with exit code $LASTEXITCODE."
    }
}
finally {
    $env:MYPLASM_PROXY_LOG_DIR = $previousLogDirectory
    $env:LOCALAPPDATA = $previousLocalAppData
    $env:MOCK_FTDI_ERROR = $previousMockError
    $env:MOCK_EMPTY_READ = $previousEmptyRead
    $env:MOCK_REENTER = $previousReentry
}

if ($CaseName -eq 'logging_failure') {
    if (Test-Path -LiteralPath (Join-Path $captureDirectory 'traffic.jsonl')) {
        throw 'Logging failure test unexpectedly created a traffic log.'
    }
    Write-Host "PASS: $CaseName forwarding survived an unavailable log directory."
    exit 0
}

$logPath = if ($CaseName -eq 'default_location') {
    $defaultCaptureRoot =
        Join-Path $caseDirectory 'localappdata\MyPlasmProtocolRecorder\captures'
    $matches = @(
        Get-ChildItem `
            -LiteralPath $defaultCaptureRoot `
            -Recurse `
            -File `
            -Filter 'traffic.jsonl')
    if ($matches.Count -ne 1) {
        throw "Expected one default-location traffic log, found $($matches.Count)."
    }
    $matches[0].FullName
}
else {
    Join-Path $captureDirectory 'traffic.jsonl'
}
if (-not (Test-Path -LiteralPath $logPath -PathType Leaf)) {
    throw "Expected JSONL log was not created: $logPath"
}

$lines = @(Get-Content -LiteralPath $logPath | Where-Object { $_.Length -gt 0 })
if ($lines.Count -eq 0) {
    throw 'The JSONL log is empty.'
}

$records = foreach ($line in $lines) {
    try {
        $record = $line | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "Invalid JSONL record: $line"
    }

    foreach ($required in @(
            'schema_version',
            'session_id',
            'utc_timestamp',
            'elapsed_us',
            'process_id',
            'thread_id',
            'function',
            'sequence',
            'handle_id',
            'status')) {
        if ($record.PSObject.Properties.Name -notcontains $required) {
            throw "Record for $($record.function) is missing '$required'."
        }
    }
    if ($record.schema_version -ne 1) {
        throw 'Unexpected log schema version.'
    }
    $parsedTimestamp = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse(
            $record.utc_timestamp,
            [ref]$parsedTimestamp)) {
        throw "Invalid UTC timestamp: $($record.utc_timestamp)"
    }
    if ($parsedTimestamp.Offset -ne [TimeSpan]::Zero) {
        throw "Timestamp is not UTC: $($record.utc_timestamp)"
    }
    $record
}

$sequences = @($records | ForEach-Object { [UInt64]$_.sequence })
if (($sequences | Sort-Object -Unique).Count -ne $sequences.Count) {
    throw 'Call sequence numbers are not unique.'
}
for ($index = 0; $index -lt $sequences.Count; $index++) {
    if ($sequences[$index] -ne ($index + 1)) {
        throw 'JSONL file ordering does not match monotonically increasing sequence numbers.'
    }
}

$sessionIds = @($records.session_id | Sort-Object -Unique)
if ($sessionIds.Count -ne 1 -or [string]::IsNullOrWhiteSpace($sessionIds[0])) {
    throw 'Session ID was not stable across the capture.'
}

if ($CaseName -in @('normal', 'default_location')) {
    $expectedFunctions = @(
        'FT_ListDevices',
        'FT_OpenEx',
        'FT_SetBaudRate',
        'FT_SetDataCharacteristics',
        'FT_SetFlowControl',
        'FT_SetLatencyTimer',
        'FT_SetBitMode',
        'FT_GetQueueStatus',
        'FT_Write',
        'FT_Read',
        'FT_Close')
    if (($records.function -join ',') -ne ($expectedFunctions -join ',')) {
        throw "Unexpected function order: $($records.function -join ',')"
    }

    $write = $records | Where-Object function -eq 'FT_Write'
    $read = $records | Where-Object function -eq 'FT_Read'
    $queue = $records | Where-Object function -eq 'FT_GetQueueStatus'
    if ($write.requested_count -ne 5 -or
        $write.actual_count -ne 5 -or
        $write.write_hex -ne '00107F80FF') {
        throw 'FT_Write logging did not preserve the complete known payload.'
    }
    if ($read.actual_count -ne 4 -or $read.read_hex -ne 'DEADBEEF') {
        throw 'FT_Read logging did not preserve the complete returned payload.'
    }
    if ($queue.queue_count -ne 4) {
        throw 'Queue count was not logged.'
    }
    if (($records | Where-Object function -eq 'FT_SetBaudRate').baud_rate -ne 115200) {
        throw 'Baud rate was not logged.'
    }
    if (($records | Where-Object function -eq 'FT_SetLatencyTimer').latency_timer -ne 2) {
        throw 'Latency timer was not logged.'
    }
    if (($records | Where-Object function -eq 'FT_SetBitMode').bit_mode.mode -ne 1) {
        throw 'Bit mode was not logged.'
    }
    if (($records | Where-Object function -eq 'FT_SetFlowControl').flow_control.mode -ne 256) {
        throw 'Flow-control settings were not logged.'
    }
    if (($records | Where-Object function -eq 'FT_SetDataCharacteristics').data_characteristics.word_length -ne 8) {
        throw 'Data-characteristic settings were not logged.'
    }

    $handleIds = @(
        $records |
            Where-Object { $_.function -notin @('FT_ListDevices') } |
            Select-Object -ExpandProperty handle_id -Unique)
    if ($handleIds.Count -ne 1 -or $handleIds[0] -ne 1) {
        throw 'The stable per-session handle identifier changed.'
    }
}
elseif ($CaseName -eq 'concurrent') {
    $queueRecords = @($records | Where-Object function -eq 'FT_GetQueueStatus')
    if ($queueRecords.Count -ne 800) {
        throw "Expected 800 concurrent queue records, found $($queueRecords.Count)."
    }
}
elseif ($CaseName -eq 'reentrant') {
    if (($records.function -join ',') -ne 'FT_OpenEx,FT_GetQueueStatus,FT_Read,FT_Close') {
        throw "Unexpected re-entrant record order: $($records.function -join ',')"
    }
}
elseif ($CaseName -eq 'mock_error') {
    $write = $records | Where-Object function -eq 'FT_Write'
    if ($write.status -ne 4 -or $write.write_hex -ne '919293') {
        throw 'Mock error status or payload was not preserved in the log.'
    }
}
elseif ($CaseName -eq 'empty_read') {
    $read = $records | Where-Object function -eq 'FT_Read'
    if ($read.actual_count -ne 0 -or $read.read_hex -ne '') {
        throw 'Empty read was not logged accurately.'
    }
}
elseif ($CaseName -eq 'zero_write') {
    $write = $records | Where-Object function -eq 'FT_Write'
    if ($write.requested_count -ne 0 -or
        $write.actual_count -ne 0 -or
        $write.write_hex -ne '') {
        throw 'Zero-byte write was not logged accurately.'
    }
}
elseif ($CaseName -eq 'large_payload') {
    $write = $records | Where-Object function -eq 'FT_Write'
    if ($write.requested_count -ne 1048576 -or
        $write.actual_count -ne 1048576 -or
        $write.write_hex.Length -ne 2097152) {
        throw 'Large payload was truncated in the log.'
    }
}
elseif ($CaseName -in @('missing_real', 'missing_export', 'failed_initialization')) {
    $record = $records | Select-Object -First 1
    if ($record.status -ne 18 -or $record.function -ne 'FT_ListDevices') {
        throw 'Initialization failure did not produce an unambiguous safe-failure record.'
    }
}

Write-Host "PASS: $CaseName produced $($records.Count) valid JSONL record(s)."
