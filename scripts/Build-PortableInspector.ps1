[CmdletBinding()]
param(
    [string]$DotnetCommand = 'dotnet'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$applicationProject = Join-Path $repositoryRoot 'src\MyPlasm.Inspector.App\MyPlasm.Inspector.App.csproj'
$peInspectorProject = Join-Path $repositoryRoot 'tools\MyPlasm.Inspector.PeInspector\MyPlasm.Inspector.PeInspector.csproj'
$localDll = Join-Path $repositoryRoot 'native\local\ftd2xx.dll'
$packageTemplateDirectory = Join-Path $repositoryRoot 'packaging\portable-win-x86'
$artifactsDirectory = Join-Path $repositoryRoot 'artifacts'
$packageDirectory = Join-Path $artifactsDirectory 'MyPlasmInspector-win-x86-diagnostic'
$packageZip = Join-Path $artifactsDirectory 'MyPlasmInspector-win-x86-diagnostic.zip'
$transactionId = [Guid]::NewGuid().ToString('N')
$stagingRoot = Join-Path $artifactsDirectory ".portable-staging-$transactionId"
$stagedPackageDirectory = Join-Path $stagingRoot 'MyPlasmInspector-win-x86-diagnostic'
$stagedPackageZip = Join-Path $stagingRoot 'MyPlasmInspector-win-x86-diagnostic.zip'
$applicationExecutable = Join-Path $stagedPackageDirectory 'MyPlasm Inspector.exe'
$packagedDll = Join-Path $stagedPackageDirectory 'native\ftd2xx.dll'
$manifestPath = Join-Path $stagedPackageDirectory 'package-manifest.json'

Import-Module (Join-Path $PSScriptRoot 'PortablePackage.psm1') -Force

if (-not (Get-Command $DotnetCommand -ErrorAction SilentlyContinue)) {
    throw 'A .NET 8 SDK was not found. Install the .NET 8 SDK, then run this file again.'
}
if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw 'Git was not found. A source commit is required to evidence-lock the package.'
}

$sourceCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'The exact source commit could not be determined.'
}
$workingTreeChanges = @(& git -C $repositoryRoot status --porcelain=v1 -uall)
if ($LASTEXITCODE -ne 0) {
    throw 'The Git working-tree state could not be determined.'
}
if ($workingTreeChanges.Count -ne 0) {
    throw 'Portable packaging requires a clean Git working tree so package evidence matches one exact source commit.'
}

if (-not (Test-Path -LiteralPath $localDll -PathType Leaf)) {
    throw "The required vendor DLL is missing: $localDll`nCopy a legally obtained x86 ftd2xx.dll there. The directory is intentionally ignored by Git."
}

foreach ($template in @(
    'Launch MyPlasm Inspector.bat',
    'Launch MyPlasm Inspector Diagnostic.bat',
    'README-FIRST.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $packageTemplateDirectory $template) -PathType Leaf)) {
        throw "Package template is missing: $template"
    }
}

$trustedD2xxEvidence = Get-TrustedD2xxEvidence
$localDllEvidence = Assert-TrustedD2xxFile `
    -Path $localDll `
    -ExpectedEvidence $trustedD2xxEvidence

& $DotnetCommand run `
    --project $peInspectorProject `
    --configuration Release `
    -- $localDll `
    --architecture x86
if ($LASTEXITCODE -ne 0) {
    throw 'The trusted local ftd2xx.dll is not an x86 PE DLL compatible with the win-x86 package.'
}

New-Item -ItemType Directory -Path $artifactsDirectory -Force | Out-Null

$existingDirectoryPresent = Test-Path -LiteralPath $packageDirectory -PathType Container
$existingZipPresent = Test-Path -LiteralPath $packageZip -PathType Leaf
if ($existingDirectoryPresent -ne $existingZipPresent) {
    throw 'Existing package state is ambiguous. Preserve it for review; the package directory and ZIP must both exist or both be absent.'
}
if ($existingDirectoryPresent) {
    $existingPackage = Assert-PortablePackageDirectory `
        -PackageDirectory $packageDirectory `
        -ExpectedD2xxEvidence $trustedD2xxEvidence
    $null = Assert-PortablePackageZip `
        -ZipPath $packageZip `
        -ExpectedApplicationEvidence $existingPackage.Application `
        -ExpectedD2xxEvidence $trustedD2xxEvidence
    Write-Host 'Existing portable package validated and will remain active until its replacement passes every check.'
}

New-Item -ItemType Directory -Path $stagingRoot -ErrorAction Stop | Out-Null

try {
    & $DotnetCommand publish `
        $applicationProject `
        --configuration Release `
        --runtime win-x86 `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:PublishTrimmed=false `
        --output $stagedPackageDirectory
    if ($LASTEXITCODE -ne 0) {
        throw 'Self-contained win-x86 publish failed.'
    }

    Copy-Item `
        -LiteralPath (Join-Path $packageTemplateDirectory 'Launch MyPlasm Inspector.bat') `
        -Destination (Join-Path $stagedPackageDirectory 'Launch MyPlasm Inspector.bat')
    Copy-Item `
        -LiteralPath (Join-Path $packageTemplateDirectory 'Launch MyPlasm Inspector Diagnostic.bat') `
        -Destination (Join-Path $stagedPackageDirectory 'Launch MyPlasm Inspector Diagnostic.bat')
    Copy-Item `
        -LiteralPath (Join-Path $packageTemplateDirectory 'README-FIRST.txt') `
        -Destination (Join-Path $stagedPackageDirectory 'README-FIRST.txt')

    $stagedDllEvidence = Assert-TrustedD2xxFile `
        -Path $packagedDll `
        -ExpectedEvidence $trustedD2xxEvidence
    & $DotnetCommand run `
        --project $peInspectorProject `
        --configuration Release `
        -- $packagedDll `
        --architecture x86
    if ($LASTEXITCODE -ne 0) {
        throw 'The staged ftd2xx.dll did not pass the x86 DLL compatibility check.'
    }

    $applicationEvidence = Assert-WinX86Executable -Path $applicationExecutable
    $manifestJson = New-PortablePackageManifestJson `
        -SourceCommit $sourceCommit `
        -ApplicationEvidence $applicationEvidence `
        -D2xxEvidence $stagedDllEvidence
    [System.IO.File]::WriteAllText(
        $manifestPath,
        $manifestJson,
        [System.Text.UTF8Encoding]::new($false))

    $validatedDirectory = Assert-PortablePackageDirectory `
        -PackageDirectory $stagedPackageDirectory `
        -ExpectedD2xxEvidence $trustedD2xxEvidence `
        -ExpectedSourceCommit $sourceCommit `
        -RequireManifest

    New-PortablePackageZip `
        -PackageDirectory $stagedPackageDirectory `
        -ZipPath $stagedPackageZip
    $validatedZip = Assert-PortablePackageZip `
        -ZipPath $stagedPackageZip `
        -ExpectedApplicationEvidence $validatedDirectory.Application `
        -ExpectedD2xxEvidence $trustedD2xxEvidence `
        -ExpectedSourceCommit $sourceCommit `
        -RequireManifest

    $finalValidator = {
        param($publishedDirectory, $publishedZip)

        $publishedPackage = Assert-PortablePackageDirectory `
            -PackageDirectory $publishedDirectory `
            -ExpectedD2xxEvidence $trustedD2xxEvidence `
            -ExpectedSourceCommit $sourceCommit `
            -RequireManifest
        $null = Assert-PortablePackageZip `
            -ZipPath $publishedZip `
            -ExpectedApplicationEvidence $publishedPackage.Application `
            -ExpectedD2xxEvidence $trustedD2xxEvidence `
            -ExpectedSourceCommit $sourceCommit `
            -RequireManifest
    }

    Publish-PortablePackageTransaction `
        -ArtifactsDirectory $artifactsDirectory `
        -StagedPackageDirectory $stagedPackageDirectory `
        -StagedZipPath $stagedPackageZip `
        -FinalPackageDirectory $packageDirectory `
        -FinalZipPath $packageZip `
        -ValidatePublishedPackage $finalValidator

    if (Test-Path -LiteralPath $stagingRoot -PathType Container) {
        $fullArtifactsDirectory = [System.IO.Path]::GetFullPath($artifactsDirectory).TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar)
        $fullStagingRoot = [System.IO.Path]::GetFullPath($stagingRoot)
        if (-not $fullStagingRoot.StartsWith(
                $fullArtifactsDirectory + [System.IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove a staging directory outside artifacts: $fullStagingRoot"
        }
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }

    $publishedPackage = Assert-PortablePackageDirectory `
        -PackageDirectory $packageDirectory `
        -ExpectedD2xxEvidence $trustedD2xxEvidence `
        -ExpectedSourceCommit $sourceCommit `
        -RequireManifest
    $publishedZip = Assert-PortablePackageZip `
        -ZipPath $packageZip `
        -ExpectedApplicationEvidence $publishedPackage.Application `
        -ExpectedD2xxEvidence $trustedD2xxEvidence `
        -ExpectedSourceCommit $sourceCommit `
        -RequireManifest

    Write-Host ''
    Write-Host 'Portable package transaction completed:'
    Write-Host $packageDirectory
    Write-Host 'Portable ZIP transaction completed:'
    Write-Host $packageZip
    Write-Host "Source commit: $sourceCommit"
    Write-Host "D2XX version: $($localDllEvidence.FileVersion)"
    Write-Host "D2XX size: $($localDllEvidence.SizeBytes)"
    Write-Host "D2XX SHA-256: $($localDllEvidence.Sha256)"
    Write-Host "ZIP entries validated after reopen: $($publishedZip.EntryCount)"
}
catch {
    Write-Error `
        -ErrorAction Continue `
        "Portable package build failed. Existing final package state was not intentionally removed. Staging evidence remains at '$stagingRoot'. $($_.Exception.Message)"
    throw
}
