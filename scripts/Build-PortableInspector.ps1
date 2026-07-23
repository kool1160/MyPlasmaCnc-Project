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

& $DotnetCommand run --project $peInspectorProject --configuration Release -- $applicationExecutable --architecture x86
if ($LASTEXITCODE -ne 0) {
    throw 'The packaged application executable is not an x86 PE file.'
}

Compress-Archive -LiteralPath (Get-ChildItem -LiteralPath $packageDirectory | Select-Object -ExpandProperty FullName) -DestinationPath $packageZip -Force

if (-not (Test-Path -LiteralPath $packageZip -PathType Leaf)) {
    throw "Portable ZIP was not created: $packageZip"
}

Write-Host ''
Write-Host 'Portable package created:'
Write-Host $packageDirectory
Write-Host 'Portable ZIP created:'
Write-Host $packageZip
