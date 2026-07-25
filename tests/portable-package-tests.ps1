[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $repositoryRoot 'scripts\PortablePackage.psm1') -Force

$testRoot = Join-Path `
    ([System.IO.Path]::GetTempPath()) `
    "MyPlasmPortablePackageTests-$([Guid]::NewGuid().ToString('N'))"
$script:passedTests = 0

function Assert-True {
    param(
        [Parameter(Mandatory)]
        [bool]$Condition,

        [Parameter(Mandatory)]
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-Equal {
    param(
        $Expected,
        $Actual,
        [Parameter(Mandatory)]
        [string]$Message
    )

    if ($Expected -ne $Actual) {
        throw "$Message Expected '$Expected', found '$Actual'."
    }
}

function Assert-Throws {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Action,

        [Parameter(Mandatory)]
        [string]$MessagePattern
    )

    $caught = $null
    try {
        & $Action
    }
    catch {
        $caught = $_
    }

    if ($null -eq $caught) {
        throw "Expected an exception matching '$MessagePattern', but no exception was thrown."
    }
    if ($caught.Exception.Message -notlike "*$MessagePattern*") {
        throw "Expected exception matching '$MessagePattern', found '$($caught.Exception.Message)'."
    }
}

function Invoke-Test {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [scriptblock]$Body
    )

    & $Body
    $script:passedTests++
    Write-Host "PASS: $Name"
}

function New-MinimalX86Executable {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [uint16]$Machine = 0x014C
    )

    $bytes = [byte[]]::new(512)
    $stream = [System.IO.MemoryStream]::new($bytes, $true)
    $writer = [System.IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0x5A4D)
        $stream.Position = 0x3C
        $writer.Write([int32]0x80)
        $stream.Position = 0x80
        $writer.Write([uint32]0x00004550)
        $writer.Write($Machine)
        $stream.Position = 0x80 + 22
        $writer.Write([uint16]0x0002)
        $stream.Position = 0x80 + 24
        $writer.Write([uint16]0x010B)
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }

    [System.IO.File]::WriteAllBytes($Path, $bytes)
}

function New-TransactionFixture {
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    $artifacts = Join-Path $testRoot $Name
    $finalDirectory = Join-Path $artifacts 'MyPlasmInspector-win-x86-diagnostic'
    $finalZip = Join-Path $artifacts 'MyPlasmInspector-win-x86-diagnostic.zip'
    $stagingRoot = Join-Path $artifacts '.staging'
    $stagedDirectory = Join-Path $stagingRoot 'MyPlasmInspector-win-x86-diagnostic'
    $stagedZip = Join-Path $stagingRoot 'MyPlasmInspector-win-x86-diagnostic.zip'
    [System.IO.Directory]::CreateDirectory($finalDirectory) | Out-Null
    [System.IO.Directory]::CreateDirectory($stagedDirectory) | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $finalDirectory 'state.txt'), 'OLD-DIRECTORY')
    [System.IO.File]::WriteAllText($finalZip, 'OLD-ZIP')
    [System.IO.File]::WriteAllText((Join-Path $stagedDirectory 'state.txt'), 'NEW-DIRECTORY')
    [System.IO.File]::WriteAllText($stagedZip, 'NEW-ZIP')

    [pscustomobject]@{
        Artifacts = $artifacts
        FinalDirectory = $finalDirectory
        FinalZip = $finalZip
        StagedDirectory = $stagedDirectory
        StagedZip = $stagedZip
        OriginalZipHash = (Get-FileHash -LiteralPath $finalZip -Algorithm SHA256).Hash
    }
}

function Assert-OriginalFixtureRestored {
    param(
        [Parameter(Mandatory)]
        [psobject]$Fixture
    )

    Assert-True `
        -Condition (Test-Path -LiteralPath $Fixture.FinalDirectory -PathType Container) `
        -Message 'The original package directory was not restored.'
    Assert-Equal `
        -Expected 'OLD-DIRECTORY' `
        -Actual ([System.IO.File]::ReadAllText((Join-Path $Fixture.FinalDirectory 'state.txt'))) `
        -Message 'The original package directory content changed.'
    Assert-Equal `
        -Expected 1 `
        -Actual @(Get-ChildItem -LiteralPath $Fixture.FinalDirectory -File).Count `
        -Message 'The restored original package directory contains unexpected files.'
    Assert-True `
        -Condition (Test-Path -LiteralPath $Fixture.FinalZip -PathType Leaf) `
        -Message 'The original package ZIP was not restored.'
    Assert-Equal `
        -Expected $Fixture.OriginalZipHash `
        -Actual (Get-FileHash -LiteralPath $Fixture.FinalZip -Algorithm SHA256).Hash `
        -Message 'The original package ZIP changed.'
}

function Get-PassingValidator {
    {
        param($directory, $zip)

        if ([System.IO.File]::ReadAllText((Join-Path $directory 'state.txt')) -ne 'NEW-DIRECTORY' -or
            [System.IO.File]::ReadAllText($zip) -ne 'NEW-ZIP') {
            throw 'Published package validation failed.'
        }
    }
}

try {
    [System.IO.Directory]::CreateDirectory($testRoot) | Out-Null

    Invoke-Test 'trusted file identity accepts an exact match and rejects hash, size, and version mismatches' {
        $evidencePath = Join-Path $testRoot 'identity-source.bin'
        [System.IO.File]::WriteAllText($evidencePath, 'synthetic evidence')
        $actual = Get-FileEvidence -Path $evidencePath
        $null = Assert-TrustedD2xxFile -Path $evidencePath -ExpectedEvidence $actual

        $wrongHash = [pscustomobject]@{
            FileVersion = $actual.FileVersion
            SizeBytes = $actual.SizeBytes
            Sha256 = ('0' * 64)
        }
        Assert-Throws `
            -Action { $null = Assert-TrustedD2xxFile -Path $evidencePath -ExpectedEvidence $wrongHash } `
            -MessagePattern 'SHA-256 mismatch'

        $wrongSize = [pscustomobject]@{
            FileVersion = $actual.FileVersion
            SizeBytes = $actual.SizeBytes + 1
            Sha256 = $actual.Sha256
        }
        Assert-Throws `
            -Action { $null = Assert-TrustedD2xxFile -Path $evidencePath -ExpectedEvidence $wrongSize } `
            -MessagePattern 'File size mismatch'

        $wrongVersion = [pscustomobject]@{
            FileVersion = 'synthetic-wrong-version'
            SizeBytes = $actual.SizeBytes
            Sha256 = $actual.Sha256
        }
        Assert-Throws `
            -Action { $null = Assert-TrustedD2xxFile -Path $evidencePath -ExpectedEvidence $wrongVersion } `
            -MessagePattern 'File version mismatch'
    }

    Invoke-Test 'x86 executable inspection rejects a different machine architecture' {
        $x86Path = Join-Path $testRoot 'synthetic-x86.exe'
        New-MinimalX86Executable -Path $x86Path
        $evidence = Assert-WinX86Executable -Path $x86Path
        Assert-Equal -Expected 'x86' -Actual $evidence.Architecture -Message 'x86 architecture was not reported.'

        $x64Path = Join-Path $testRoot 'synthetic-x64.exe'
        New-MinimalX86Executable -Path $x64Path -Machine 0x8664
        Assert-Throws `
            -Action { $null = Assert-WinX86Executable -Path $x64Path } `
            -MessagePattern 'not an x86 PE32 executable'
    }

    Invoke-Test 'ZIP reopen validation locks application, DLL, manifest, and entry paths' {
        $packageDirectory = Join-Path $testRoot 'validated-package'
        $nativeDirectory = Join-Path $packageDirectory 'native'
        [System.IO.Directory]::CreateDirectory($nativeDirectory) | Out-Null
        $applicationPath = Join-Path $packageDirectory 'MyPlasm Inspector.exe'
        New-MinimalX86Executable -Path $applicationPath
        $applicationEvidence = Assert-WinX86Executable -Path $applicationPath
        $dllPath = Join-Path $nativeDirectory 'ftd2xx.dll'
        [System.IO.File]::WriteAllText($dllPath, 'synthetic DLL evidence')
        $dllEvidence = Get-FileEvidence -Path $dllPath
        foreach ($name in @(
            'Launch MyPlasm Inspector.bat',
            'Launch MyPlasm Inspector Diagnostic.bat',
            'README-FIRST.txt')) {
            [System.IO.File]::WriteAllText((Join-Path $packageDirectory $name), $name)
        }
        $manifest = New-PortablePackageManifestJson `
            -SourceCommit ('a' * 40) `
            -ApplicationEvidence $applicationEvidence `
            -D2xxEvidence $dllEvidence
        [System.IO.File]::WriteAllText(
            (Join-Path $packageDirectory 'package-manifest.json'),
            $manifest,
            [System.Text.UTF8Encoding]::new($false))

        $validatedDirectory = Assert-PortablePackageDirectory `
            -PackageDirectory $packageDirectory `
            -ExpectedD2xxEvidence $dllEvidence `
            -ExpectedSourceCommit ('a' * 40) `
            -RequireManifest
        $zipPath = Join-Path $testRoot 'validated-package.zip'
        New-PortablePackageZip -PackageDirectory $packageDirectory -ZipPath $zipPath
        $validatedZip = Assert-PortablePackageZip `
            -ZipPath $zipPath `
            -ExpectedApplicationEvidence $validatedDirectory.Application `
            -ExpectedD2xxEvidence $dllEvidence `
            -ExpectedSourceCommit ('a' * 40) `
            -RequireManifest
        Assert-True -Condition ($validatedZip.EntryCount -ge 6) -Message 'Validated ZIP entry count is too small.'

        [System.IO.File]::AppendAllText($dllPath, 'corruption')
        $corruptZip = Join-Path $testRoot 'corrupt-package.zip'
        New-PortablePackageZip -PackageDirectory $packageDirectory -ZipPath $corruptZip
        Assert-Throws `
            -Action {
                $null = Assert-PortablePackageZip `
                    -ZipPath $corruptZip `
                    -ExpectedApplicationEvidence $validatedDirectory.Application `
                    -ExpectedD2xxEvidence $dllEvidence `
                    -ExpectedSourceCommit ('a' * 40) `
                    -RequireManifest
            } `
            -MessagePattern 'D2XX evidence'

        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $unsafeZip = Join-Path $testRoot 'unsafe-entry.zip'
        $archive = [System.IO.Compression.ZipFile]::Open(
            $unsafeZip,
            [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            $null = $archive.CreateEntry('../escape.txt')
        }
        finally {
            $archive.Dispose()
        }
        Assert-Throws `
            -Action {
                $null = Assert-PortablePackageZip `
                    -ZipPath $unsafeZip `
                    -ExpectedApplicationEvidence $validatedDirectory.Application `
                    -ExpectedD2xxEvidence $dllEvidence
            } `
            -MessagePattern 'unsafe entry path'
    }

    foreach ($stage in @(
        'BeforePublish',
        'AfterZipPublish',
        'AfterDirectoryBackup',
        'AfterDirectoryPublish',
        'AfterValidation')) {
        Invoke-Test "transaction rollback preserves the exact prior package after $stage" {
            $fixture = New-TransactionFixture -Name "rollback-$stage"
            Assert-Throws `
                -Action {
                    Publish-PortablePackageTransaction `
                        -ArtifactsDirectory $fixture.Artifacts `
                        -StagedPackageDirectory $fixture.StagedDirectory `
                        -StagedZipPath $fixture.StagedZip `
                        -FinalPackageDirectory $fixture.FinalDirectory `
                        -FinalZipPath $fixture.FinalZip `
                        -ValidatePublishedPackage (Get-PassingValidator) `
                        -FailureInjectionStage $stage
                } `
                -MessagePattern 'previous package state was restored'
            Assert-OriginalFixtureRestored -Fixture $fixture

            $preservedReplacement = @(
                Get-ChildItem -LiteralPath $fixture.Artifacts -Recurse -Force |
                    Where-Object {
                        $_.FullName -like '*quarantine*' -or
                        $_.FullName -like '*\.staging\*'
                    }
            )
            Assert-True `
                -Condition ($preservedReplacement.Count -gt 0) `
                -Message 'Failed replacement evidence was not preserved.'
        }
    }

    Invoke-Test 'validator failure rolls back both published outputs' {
        $fixture = New-TransactionFixture -Name 'validator-failure'
        Assert-Throws `
            -Action {
                Publish-PortablePackageTransaction `
                    -ArtifactsDirectory $fixture.Artifacts `
                    -StagedPackageDirectory $fixture.StagedDirectory `
                    -StagedZipPath $fixture.StagedZip `
                    -FinalPackageDirectory $fixture.FinalDirectory `
                    -FinalZipPath $fixture.FinalZip `
                    -ValidatePublishedPackage { throw 'synthetic final validation failure' }
            } `
            -MessagePattern 'previous package state was restored'
        Assert-OriginalFixtureRestored -Fixture $fixture
    }

    Invoke-Test 'ambiguous prior state is refused without changing staged or final files' {
        $fixture = New-TransactionFixture -Name 'ambiguous-state'
        [System.IO.File]::Delete($fixture.FinalZip)
        $stagedDirectoryHash = (Get-FileHash `
            -LiteralPath (Join-Path $fixture.StagedDirectory 'state.txt') `
            -Algorithm SHA256).Hash
        $stagedZipHash = (Get-FileHash -LiteralPath $fixture.StagedZip -Algorithm SHA256).Hash

        Assert-Throws `
            -Action {
                Publish-PortablePackageTransaction `
                    -ArtifactsDirectory $fixture.Artifacts `
                    -StagedPackageDirectory $fixture.StagedDirectory `
                    -StagedZipPath $fixture.StagedZip `
                    -FinalPackageDirectory $fixture.FinalDirectory `
                    -FinalZipPath $fixture.FinalZip `
                    -ValidatePublishedPackage (Get-PassingValidator)
            } `
            -MessagePattern 'Existing portable-package state is ambiguous'

        Assert-Equal `
            -Expected 'OLD-DIRECTORY' `
            -Actual ([System.IO.File]::ReadAllText((Join-Path $fixture.FinalDirectory 'state.txt'))) `
            -Message 'Ambiguous-state refusal changed the prior directory.'
        Assert-Equal `
            -Expected $stagedDirectoryHash `
            -Actual (Get-FileHash -LiteralPath (Join-Path $fixture.StagedDirectory 'state.txt') -Algorithm SHA256).Hash `
            -Message 'Ambiguous-state refusal changed the staged directory.'
        Assert-Equal `
            -Expected $stagedZipHash `
            -Actual (Get-FileHash -LiteralPath $fixture.StagedZip -Algorithm SHA256).Hash `
            -Message 'Ambiguous-state refusal changed the staged ZIP.'
    }

    Invoke-Test 'successful transaction replaces both outputs only after validation' {
        $fixture = New-TransactionFixture -Name 'success'
        Publish-PortablePackageTransaction `
            -ArtifactsDirectory $fixture.Artifacts `
            -StagedPackageDirectory $fixture.StagedDirectory `
            -StagedZipPath $fixture.StagedZip `
            -FinalPackageDirectory $fixture.FinalDirectory `
            -FinalZipPath $fixture.FinalZip `
            -ValidatePublishedPackage (Get-PassingValidator)

        Assert-Equal `
            -Expected 'NEW-DIRECTORY' `
            -Actual ([System.IO.File]::ReadAllText((Join-Path $fixture.FinalDirectory 'state.txt'))) `
            -Message 'Successful transaction did not publish the new directory.'
        Assert-Equal `
            -Expected 'NEW-ZIP' `
            -Actual ([System.IO.File]::ReadAllText($fixture.FinalZip) `
            ) `
            -Message 'Successful transaction did not publish the new ZIP.'
        Assert-True `
            -Condition (-not (Test-Path -LiteralPath $fixture.StagedDirectory)) `
            -Message 'Successful transaction left the staged directory active.'
        Assert-True `
            -Condition (-not (Test-Path -LiteralPath $fixture.StagedZip)) `
            -Message 'Successful transaction left the staged ZIP active.'
        Assert-Equal `
            -Expected 0 `
            -Actual @(
                Get-ChildItem -LiteralPath $fixture.Artifacts -Force |
                    Where-Object { $_.Name -like '.portable-*' }
            ).Count `
            -Message 'Successful transaction left a backup or quarantine file.'
    }

    Write-Host "Portable package regression tests passed: $script:passedTests"
}
finally {
    $fullTestRoot = [System.IO.Path]::GetFullPath($testRoot)
    $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if ($fullTestRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $fullTestRoot -PathType Container)) {
        Remove-Item -LiteralPath $fullTestRoot -Recurse -Force
    }
}
