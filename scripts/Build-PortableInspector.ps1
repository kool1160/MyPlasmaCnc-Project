[CmdletBinding()]
param(
    [string]$DotnetCommand = 'dotnet'
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$applicationProject = Join-Path $repositoryRoot 'src\MyPlasm.Inspector.App\MyPlasm.Inspector.App.csproj'
$peInspectorProject = Join-Path $repositoryRoot 'tools\MyPlasm.Inspector.PeInspector\MyPlasm.Inspector.PeInspector.csproj'
$localDll = Join-Path $repositoryRoot 'native\local\ftd2xx.dll'
$packageTemplateDirectory = Join-Path $repositoryRoot 'packaging\portable-win-x86'
$artifactsDirectory = Join-Path $repositoryRoot 'artifacts'
$packageDirectory = Join-Path $artifactsDirectory 'MyPlasmInspector-win-x86-diagnostic'
$packageZip = Join-Path $artifactsDirectory 'MyPlasmInspector-win-x86-diagnostic.zip'
$applicationExecutable = Join-Path $packageDirectory 'MyPlasm Inspector.exe'
$packagedDll = Join-Path $packageDirectory 'native\ftd2xx.dll'

function Assert-WinX86Executable {
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

    Write-Host "Packaged executable: $Path"
    Write-Host 'Architecture: X86 (PE32 executable)'
}

if (-not (Get-Command $DotnetCommand -ErrorAction SilentlyContinue)) {
    throw 'A .NET 8 SDK was not found. Install the .NET 8 SDK, then run this file again.'
}

if (-not (Test-Path -LiteralPath $localDll -PathType Leaf)) {
    throw "The required vendor DLL is missing: $localDll`nCopy a legally obtained x86 ftd2xx.dll there. The directory is intentionally ignored by Git."
}

foreach ($template in @('Launch MyPlasm Inspector.bat', 'Launch MyPlasm Inspector Diagnostic.bat', 'README-FIRST.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $packageTemplateDirectory $template) -PathType Leaf)) {
        throw "Package template is missing: $template"
    }
}

& $DotnetCommand run --project $peInspectorProject --configuration Release -- $localDll --architecture x86
if ($LASTEXITCODE -ne 0) {
    throw 'The local ftd2xx.dll is not an x86 PE file compatible with the win-x86 package.'
}

New-Item -ItemType Directory -Path $artifactsDirectory -Force | Out-Null
if (Test-Path -LiteralPath $packageDirectory) {
    Remove-Item -LiteralPath $packageDirectory -Recurse -Force
}

if (Test-Path -LiteralPath $packageZip) {
    Remove-Item -LiteralPath $packageZip -Force
}

& $DotnetCommand publish $applicationProject --configuration Release --runtime win-x86 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false --output $packageDirectory
if ($LASTEXITCODE -ne 0) {
    throw 'Self-contained win-x86 publish failed.'
}

Copy-Item -LiteralPath (Join-Path $packageTemplateDirectory 'Launch MyPlasm Inspector.bat') -Destination (Join-Path $packageDirectory 'Launch MyPlasm Inspector.bat')
Copy-Item -LiteralPath (Join-Path $packageTemplateDirectory 'Launch MyPlasm Inspector Diagnostic.bat') -Destination (Join-Path $packageDirectory 'Launch MyPlasm Inspector Diagnostic.bat')
Copy-Item -LiteralPath (Join-Path $packageTemplateDirectory 'README-FIRST.txt') -Destination (Join-Path $packageDirectory 'README-FIRST.txt')

foreach ($requiredFile in @($applicationExecutable, $packagedDll, (Join-Path $packageDirectory 'Launch MyPlasm Inspector.bat'), (Join-Path $packageDirectory 'Launch MyPlasm Inspector Diagnostic.bat'), (Join-Path $packageDirectory 'README-FIRST.txt'))) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Portable package is missing required file: $requiredFile"
    }
}

& $DotnetCommand run --project $peInspectorProject --configuration Release -- $packagedDll --architecture x86
if ($LASTEXITCODE -ne 0) {
    throw 'The packaged ftd2xx.dll did not pass the x86 compatibility check.'
}

Assert-WinX86Executable -Path $applicationExecutable

Compress-Archive -LiteralPath (Get-ChildItem -LiteralPath $packageDirectory | Select-Object -ExpandProperty FullName) -DestinationPath $packageZip -Force

if (-not (Test-Path -LiteralPath $packageZip -PathType Leaf)) {
    throw "Portable ZIP was not created: $packageZip"
}

Write-Host ''
Write-Host 'Portable package created:'
Write-Host $packageDirectory
Write-Host 'Portable ZIP created:'
Write-Host $packageZip
