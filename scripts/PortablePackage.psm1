Set-StrictMode -Version Latest

function Get-TrustedD2xxEvidence {
    [CmdletBinding()]
    param()

    [pscustomobject]@{
        Architecture = 'x86'
        FileVersion = '3.01.19'
        SizeBytes = [int64]206144
        Sha256 = '381117C743766E3A696609BB29CA075772AA603CFF196E16C3854C06EE1AB254'
    }
}

function Get-FileEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $item = Get-Item -LiteralPath $Path -ErrorAction Stop
    if ($item.PSIsContainer) {
        throw "Expected a file but found a directory: $Path"
    }

    [pscustomobject]@{
        Path = $item.FullName
        FileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($item.FullName).FileVersion
        SizeBytes = [int64]$item.Length
        Sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
    }
}

function Assert-FileMatchesEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [psobject]$ExpectedEvidence,

        [switch]$VerifyFileVersion
    )

    $actual = Get-FileEvidence -Path $Path
    if ($actual.SizeBytes -ne [int64]$ExpectedEvidence.SizeBytes) {
        throw "File size mismatch for '$Path'. Expected $($ExpectedEvidence.SizeBytes), found $($actual.SizeBytes)."
    }

    if ($actual.Sha256 -ne ([string]$ExpectedEvidence.Sha256).ToUpperInvariant()) {
        throw "SHA-256 mismatch for '$Path'. Expected $($ExpectedEvidence.Sha256), found $($actual.Sha256)."
    }

    if ($VerifyFileVersion -and
        [string]$actual.FileVersion -ne [string]$ExpectedEvidence.FileVersion) {
        throw "File version mismatch for '$Path'. Expected '$($ExpectedEvidence.FileVersion)', found '$($actual.FileVersion)'."
    }

    $actual
}

function Assert-TrustedD2xxFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [psobject]$ExpectedEvidence = (Get-TrustedD2xxEvidence)
    )

    Assert-FileMatchesEvidence -Path $Path -ExpectedEvidence $ExpectedEvidence -VerifyFileVersion
}

function Assert-WinX86Executable {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $stream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)
    $reader = [System.IO.BinaryReader]::new($stream)

    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) {
            throw "The packaged application does not have a valid DOS/PE signature: $Path"
        }

        $stream.Position = 0x3C
        $peHeaderOffset = $reader.ReadInt32()
        if ($peHeaderOffset -lt 0x40 -or $peHeaderOffset -gt ($stream.Length - 26)) {
            throw "The packaged application has an invalid PE header offset: $Path"
        }

        $stream.Position = $peHeaderOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw "The packaged application does not have a valid PE signature: $Path"
        }

        $machine = $reader.ReadUInt16()
        $stream.Position = $peHeaderOffset + 22
        $characteristics = $reader.ReadUInt16()
        $stream.Position = $peHeaderOffset + 24
        $optionalHeaderMagic = $reader.ReadUInt16()

        $isExecutable = ($characteristics -band 0x0002) -ne 0
        $isDll = ($characteristics -band 0x2000) -ne 0
        if ($machine -ne 0x014C -or
            $optionalHeaderMagic -ne 0x010B -or
            -not $isExecutable -or
            $isDll) {
            throw "The packaged application is not an x86 PE32 executable: $Path"
        }
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }

    $evidence = Get-FileEvidence -Path $Path
    [pscustomobject]@{
        Path = $evidence.Path
        Architecture = 'x86'
        PeFormat = 'PE32 executable'
        FileVersion = $evidence.FileVersion
        SizeBytes = $evidence.SizeBytes
        Sha256 = $evidence.Sha256
    }
}

function New-PortablePackageManifestJson {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$SourceCommit,

        [Parameter(Mandatory)]
        [psobject]$ApplicationEvidence,

        [Parameter(Mandatory)]
        [psobject]$D2xxEvidence
    )

    if ($SourceCommit -notmatch '^[0-9a-fA-F]{40}$') {
        throw "Source commit must be one exact 40-character Git SHA: '$SourceCommit'"
    }

    $manifest = [ordered]@{
        schemaVersion = 1
        product = 'MyPlasm Inspector'
        package = 'MyPlasmInspector-win-x86-diagnostic'
        sourceCommit = $SourceCommit.ToLowerInvariant()
        buildConfiguration = 'Release'
        runtimeIdentifier = 'win-x86'
        selfContained = $true
        safetyScope = 'Device enumeration only. No controller commands.'
        application = [ordered]@{
            path = 'MyPlasm Inspector.exe'
            architecture = 'x86'
            peFormat = 'PE32 executable'
            sizeBytes = [int64]$ApplicationEvidence.SizeBytes
            sha256 = ([string]$ApplicationEvidence.Sha256).ToUpperInvariant()
        }
        d2xx = [ordered]@{
            path = 'native/ftd2xx.dll'
            architecture = 'x86'
            fileVersion = [string]$D2xxEvidence.FileVersion
            sizeBytes = [int64]$D2xxEvidence.SizeBytes
            sha256 = ([string]$D2xxEvidence.Sha256).ToUpperInvariant()
        }
    }

    $manifest | ConvertTo-Json -Depth 6
}

function Read-PackageManifest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Json,

        [Parameter(Mandatory)]
        [string]$SourceDescription
    )

    try {
        $manifest = $Json | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "Package manifest is not valid JSON in $SourceDescription. $($_.Exception.Message)"
    }

    if ($manifest.schemaVersion -ne 1 -or
        $manifest.product -ne 'MyPlasm Inspector' -or
        $manifest.package -ne 'MyPlasmInspector-win-x86-diagnostic' -or
        $manifest.buildConfiguration -ne 'Release' -or
        $manifest.runtimeIdentifier -ne 'win-x86' -or
        $manifest.selfContained -ne $true -or
        $manifest.sourceCommit -notmatch '^[0-9a-fA-F]{40}$') {
        throw "Package manifest identity is invalid in $SourceDescription."
    }

    if ($manifest.application.path -ne 'MyPlasm Inspector.exe' -or
        $manifest.application.architecture -ne 'x86' -or
        $manifest.application.peFormat -ne 'PE32 executable' -or
        $manifest.d2xx.path -ne 'native/ftd2xx.dll' -or
        $manifest.d2xx.architecture -ne 'x86') {
        throw "Package manifest file identity is invalid in $SourceDescription."
    }

    $manifest
}

function Assert-ManifestEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [psobject]$Manifest,

        [Parameter(Mandatory)]
        [psobject]$ApplicationEvidence,

        [Parameter(Mandatory)]
        [psobject]$D2xxEvidence,

        [string]$ExpectedSourceCommit
    )

    if ($ExpectedSourceCommit -and
        $Manifest.sourceCommit -ne $ExpectedSourceCommit.ToLowerInvariant()) {
        throw "Package source commit mismatch. Expected $ExpectedSourceCommit, found $($Manifest.sourceCommit)."
    }

    if ([int64]$Manifest.application.sizeBytes -ne [int64]$ApplicationEvidence.SizeBytes -or
        ([string]$Manifest.application.sha256).ToUpperInvariant() -ne
            ([string]$ApplicationEvidence.Sha256).ToUpperInvariant()) {
        throw 'Package application evidence does not match its manifest.'
    }

    if ([int64]$Manifest.d2xx.sizeBytes -ne [int64]$D2xxEvidence.SizeBytes -or
        ([string]$Manifest.d2xx.sha256).ToUpperInvariant() -ne
            ([string]$D2xxEvidence.Sha256).ToUpperInvariant() -or
        [string]$Manifest.d2xx.fileVersion -ne [string]$D2xxEvidence.FileVersion) {
        throw 'Package D2XX evidence does not match its manifest.'
    }
}

function Assert-PortablePackageDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$PackageDirectory,

        [psobject]$ExpectedD2xxEvidence = (Get-TrustedD2xxEvidence),

        [string]$ExpectedSourceCommit,

        [switch]$RequireManifest
    )

    $requiredRelativePaths = @(
        'MyPlasm Inspector.exe',
        'native\ftd2xx.dll',
        'Launch MyPlasm Inspector.bat',
        'Launch MyPlasm Inspector Diagnostic.bat',
        'README-FIRST.txt'
    )
    foreach ($relativePath in $requiredRelativePaths) {
        $requiredPath = Join-Path $PackageDirectory $relativePath
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Portable package is missing required file: $requiredPath"
        }
    }

    $applicationPath = Join-Path $PackageDirectory 'MyPlasm Inspector.exe'
    $dllPath = Join-Path $PackageDirectory 'native\ftd2xx.dll'
    $applicationEvidence = Assert-WinX86Executable -Path $applicationPath
    $d2xxEvidence = Assert-TrustedD2xxFile -Path $dllPath -ExpectedEvidence $ExpectedD2xxEvidence
    $manifestPath = Join-Path $PackageDirectory 'package-manifest.json'
    $manifest = $null

    if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
        $manifest = Read-PackageManifest `
            -Json ([System.IO.File]::ReadAllText($manifestPath)) `
            -SourceDescription $manifestPath
        Assert-ManifestEvidence `
            -Manifest $manifest `
            -ApplicationEvidence $applicationEvidence `
            -D2xxEvidence $d2xxEvidence `
            -ExpectedSourceCommit $ExpectedSourceCommit
    }
    elseif ($RequireManifest) {
        throw "Portable package is missing required evidence manifest: $manifestPath"
    }

    [pscustomobject]@{
        Application = $applicationEvidence
        D2xx = $d2xxEvidence
        Manifest = $manifest
    }
}

function Get-ZipEntryHash {
    param(
        [Parameter(Mandatory)]
        [System.IO.Compression.ZipArchiveEntry]$Entry
    )

    $stream = $Entry.Open()
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '')
    }
    finally {
        $algorithm.Dispose()
        $stream.Dispose()
    }
}

function Read-ZipEntryText {
    param(
        [Parameter(Mandatory)]
        [System.IO.Compression.ZipArchiveEntry]$Entry
    )

    $stream = $Entry.Open()
    $reader = [System.IO.StreamReader]::new(
        $stream,
        [System.Text.Encoding]::UTF8,
        $true,
        4096,
        $false)
    try {
        $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Assert-PortablePackageZip {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ZipPath,

        [Parameter(Mandatory)]
        [psobject]$ExpectedApplicationEvidence,

        [psobject]$ExpectedD2xxEvidence = (Get-TrustedD2xxEvidence),

        [string]$ExpectedSourceCommit,

        [switch]$RequireManifest
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zipItem = Get-Item -LiteralPath $ZipPath -ErrorAction Stop
    if ($zipItem.Length -le 0) {
        throw "Portable ZIP is empty: $ZipPath"
    }

    $archive = [System.IO.Compression.ZipFile]::OpenRead($zipItem.FullName)
    try {
        $entriesByName = @{}
        foreach ($entry in $archive.Entries) {
            $normalizedName = $entry.FullName.Replace([char]92, [char]47)
            if ($normalizedName.StartsWith('/', [StringComparison]::Ordinal) -or
                $normalizedName.Contains(':') -or
                $normalizedName.Split('/') -contains '..') {
                throw "Portable ZIP contains an unsafe entry path: $normalizedName"
            }

            if ([string]::IsNullOrWhiteSpace($entry.Name)) {
                continue
            }

            $key = $normalizedName.ToUpperInvariant()
            if ($entriesByName.ContainsKey($key)) {
                throw "Portable ZIP contains a duplicate entry: $normalizedName"
            }

            $entriesByName[$key] = $entry
        }

        $requiredNames = @(
            'MYPLASM INSPECTOR.EXE',
            'NATIVE/FTD2XX.DLL',
            'LAUNCH MYPLASM INSPECTOR.BAT',
            'LAUNCH MYPLASM INSPECTOR DIAGNOSTIC.BAT',
            'README-FIRST.TXT'
        )
        foreach ($requiredName in $requiredNames) {
            if (-not $entriesByName.ContainsKey($requiredName)) {
                throw "Portable ZIP is missing required entry: $requiredName"
            }
        }

        $applicationEntry = $entriesByName['MYPLASM INSPECTOR.EXE']
        $dllEntry = $entriesByName['NATIVE/FTD2XX.DLL']
        $applicationEvidence = [pscustomobject]@{
            SizeBytes = [int64]$applicationEntry.Length
            Sha256 = Get-ZipEntryHash -Entry $applicationEntry
        }
        $dllEvidence = [pscustomobject]@{
            FileVersion = [string]$ExpectedD2xxEvidence.FileVersion
            SizeBytes = [int64]$dllEntry.Length
            Sha256 = Get-ZipEntryHash -Entry $dllEntry
        }

        if ($applicationEvidence.SizeBytes -ne [int64]$ExpectedApplicationEvidence.SizeBytes -or
            $applicationEvidence.Sha256 -ne
                ([string]$ExpectedApplicationEvidence.Sha256).ToUpperInvariant()) {
            throw 'Portable ZIP application evidence does not match the validated package directory.'
        }

        if ($dllEvidence.SizeBytes -ne [int64]$ExpectedD2xxEvidence.SizeBytes -or
            $dllEvidence.Sha256 -ne ([string]$ExpectedD2xxEvidence.Sha256).ToUpperInvariant()) {
            throw 'Portable ZIP D2XX evidence does not match the trusted DLL identity.'
        }

        $manifest = $null
        if ($entriesByName.ContainsKey('PACKAGE-MANIFEST.JSON')) {
            $manifest = Read-PackageManifest `
                -Json (Read-ZipEntryText -Entry $entriesByName['PACKAGE-MANIFEST.JSON']) `
                -SourceDescription $ZipPath
            Assert-ManifestEvidence `
                -Manifest $manifest `
                -ApplicationEvidence $applicationEvidence `
                -D2xxEvidence $dllEvidence `
                -ExpectedSourceCommit $ExpectedSourceCommit
        }
        elseif ($RequireManifest) {
            throw 'Portable ZIP is missing required evidence manifest: package-manifest.json'
        }

        [pscustomobject]@{
            EntryCount = @($archive.Entries | Where-Object { -not [string]::IsNullOrWhiteSpace($_.Name) }).Count
            Application = $applicationEvidence
            D2xx = $dllEvidence
            Manifest = $manifest
        }
    }
    finally {
        $archive.Dispose()
    }
}

function New-PortablePackageZip {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$PackageDirectory,

        [Parameter(Mandatory)]
        [string]$ZipPath
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path -LiteralPath $ZipPath) {
        throw "Staged ZIP path already exists: $ZipPath"
    }

    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $PackageDirectory,
        $ZipPath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)
}

function Assert-PathWithinRoot {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Root
    )

    $fullRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $rootPrefix = $fullRoot + [System.IO.Path]::DirectorySeparatorChar
    if ($fullPath -eq $fullRoot -or
        -not $fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Transaction path is outside the artifacts directory: $fullPath"
    }

    $fullPath
}

function Get-UniqueTransactionPath {
    param(
        [Parameter(Mandatory)]
        [string]$ArtifactsDirectory,

        [Parameter(Mandatory)]
        [string]$Label
    )

    Join-Path $ArtifactsDirectory ".$Label-$([Guid]::NewGuid().ToString('N'))"
}

function Publish-PortablePackageTransaction {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ArtifactsDirectory,

        [Parameter(Mandatory)]
        [string]$StagedPackageDirectory,

        [Parameter(Mandatory)]
        [string]$StagedZipPath,

        [Parameter(Mandatory)]
        [string]$FinalPackageDirectory,

        [Parameter(Mandatory)]
        [string]$FinalZipPath,

        [Parameter(Mandatory)]
        [scriptblock]$ValidatePublishedPackage,

        [ValidateSet(
            'None',
            'BeforePublish',
            'AfterZipPublish',
            'AfterDirectoryBackup',
            'AfterDirectoryPublish',
            'AfterValidation')]
        [string]$FailureInjectionStage = 'None'
    )

    $artifacts = [System.IO.Path]::GetFullPath($ArtifactsDirectory)
    $stagedDirectory = Assert-PathWithinRoot -Path $StagedPackageDirectory -Root $artifacts
    $stagedZip = Assert-PathWithinRoot -Path $StagedZipPath -Root $artifacts
    $finalDirectory = Assert-PathWithinRoot -Path $FinalPackageDirectory -Root $artifacts
    $finalZip = Assert-PathWithinRoot -Path $FinalZipPath -Root $artifacts

    if (-not (Test-Path -LiteralPath $stagedDirectory -PathType Container) -or
        -not (Test-Path -LiteralPath $stagedZip -PathType Leaf)) {
        throw 'The validated staged package directory and ZIP must both exist before publication.'
    }

    $priorDirectoryExists = Test-Path -LiteralPath $finalDirectory -PathType Container
    $priorZipExists = Test-Path -LiteralPath $finalZip -PathType Leaf
    if ($priorDirectoryExists -ne $priorZipExists) {
        throw 'Existing portable-package state is ambiguous; the directory and ZIP must either both exist or both be absent.'
    }

    $backupDirectory = if ($priorDirectoryExists) {
        Get-UniqueTransactionPath -ArtifactsDirectory $artifacts -Label 'portable-previous-directory'
    }
    else {
        $null
    }
    $backupZip = if ($priorZipExists) {
        Get-UniqueTransactionPath -ArtifactsDirectory $artifacts -Label 'portable-previous.zip'
    }
    else {
        $null
    }
    $zipPublished = $false
    $directoryBackedUp = $false
    $directoryPublished = $false

    try {
        if ($FailureInjectionStage -eq 'BeforePublish') {
            throw 'Injected portable-package failure before publication.'
        }

        if ($priorZipExists) {
            [System.IO.File]::Replace($stagedZip, $finalZip, $backupZip, $true)
        }
        else {
            [System.IO.File]::Move($stagedZip, $finalZip)
        }
        $zipPublished = $true

        if ($FailureInjectionStage -eq 'AfterZipPublish') {
            throw 'Injected portable-package failure after ZIP publication.'
        }

        if ($priorDirectoryExists) {
            [System.IO.Directory]::Move($finalDirectory, $backupDirectory)
            $directoryBackedUp = $true
        }

        if ($FailureInjectionStage -eq 'AfterDirectoryBackup') {
            throw 'Injected portable-package failure after directory backup.'
        }

        [System.IO.Directory]::Move($stagedDirectory, $finalDirectory)
        $directoryPublished = $true

        if ($FailureInjectionStage -eq 'AfterDirectoryPublish') {
            throw 'Injected portable-package failure after directory publication.'
        }

        & $ValidatePublishedPackage $finalDirectory $finalZip

        if ($FailureInjectionStage -eq 'AfterValidation') {
            throw 'Injected portable-package failure after final validation.'
        }

        try {
            if ($backupDirectory -and (Test-Path -LiteralPath $backupDirectory -PathType Container)) {
                Remove-Item -LiteralPath $backupDirectory -Recurse -Force
            }
            if ($backupZip -and (Test-Path -LiteralPath $backupZip -PathType Leaf)) {
                Remove-Item -LiteralPath $backupZip -Force
            }
        }
        catch {
            Write-Warning "The new package is validated, but a prior-package backup could not be removed: $($_.Exception.Message)"
        }
    }
    catch {
        $publicationFailure = $_
        $rollbackErrors = [System.Collections.Generic.List[string]]::new()

        try {
            if ($directoryPublished -and (Test-Path -LiteralPath $finalDirectory -PathType Container)) {
                $failedDirectory = Get-UniqueTransactionPath `
                    -ArtifactsDirectory $artifacts `
                    -Label 'portable-failed-directory.quarantine'
                [System.IO.Directory]::Move($finalDirectory, $failedDirectory)
            }
            if ($directoryBackedUp -and
                $backupDirectory -and
                (Test-Path -LiteralPath $backupDirectory -PathType Container)) {
                [System.IO.Directory]::Move($backupDirectory, $finalDirectory)
            }
        }
        catch {
            $rollbackErrors.Add("Directory rollback: $($_.Exception.Message)")
        }

        try {
            if ($zipPublished -and (Test-Path -LiteralPath $finalZip -PathType Leaf)) {
                $failedZip = Get-UniqueTransactionPath `
                    -ArtifactsDirectory $artifacts `
                    -Label 'portable-failed.zip.quarantine'
                [System.IO.File]::Move($finalZip, $failedZip)
            }
            if ($backupZip -and (Test-Path -LiteralPath $backupZip -PathType Leaf)) {
                [System.IO.File]::Move($backupZip, $finalZip)
            }
        }
        catch {
            $rollbackErrors.Add("ZIP rollback: $($_.Exception.Message)")
        }

        $rollbackSummary = if ($rollbackErrors.Count -eq 0) {
            'The previous package state was restored; failed replacement files were preserved in quarantine.'
        }
        else {
            "Rollback errors: $($rollbackErrors -join '; ')"
        }
        throw "Portable package publication failed. $rollbackSummary Original error: $($publicationFailure.Exception.Message)"
    }
}

Export-ModuleMember -Function @(
    'Get-TrustedD2xxEvidence',
    'Get-FileEvidence',
    'Assert-FileMatchesEvidence',
    'Assert-TrustedD2xxFile',
    'Assert-WinX86Executable',
    'New-PortablePackageManifestJson',
    'Assert-PortablePackageDirectory',
    'Assert-PortablePackageZip',
    'New-PortablePackageZip',
    'Publish-PortablePackageTransaction'
)
