# Creates the Thunderstore package dist\AFKManager-<version>.zip from an already built bin\Release\AFKManager.dll.
#
# Archive layout (metadata at the zip root, no outer folder; forward-slash entry names):
#   manifest.json, icon.png, README.md, CHANGELOG.md, LICENSE
#   plugins/AFKManager/AFKManager.dll
#
# Usage:  powershell -ExecutionPolicy Bypass -File packaging\build-package.ps1 [-Dll <path to AFKManager.dll>]
param([string]$Dll = "")
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $Dll) { $Dll = Join-Path $repo "bin\Release\AFKManager.dll" }
if (-not (Test-Path $Dll)) { throw "AFKManager.dll not found at $Dll - build first (see README)." }

$manifest = Get-Content (Join-Path $repo "manifest.json") -Raw | ConvertFrom-Json
$version = $manifest.version_number
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "manifest.json: version_number '$version' is not major.minor.patch" }
if ($manifest.name -notmatch '^[a-zA-Z0-9_]+$') { throw "manifest.json: name may only contain a-z A-Z 0-9 _" }
if ($manifest.description.Length -gt 250) { throw "manifest.json: description is longer than 250 characters" }

$entries = [ordered]@{
    "manifest.json"                     = Join-Path $repo "manifest.json"
    "icon.png"                          = Join-Path $repo "icon.png"
    "README.md"                         = Join-Path $repo "README.md"
    "CHANGELOG.md"                      = Join-Path $repo "CHANGELOG.md"
    "LICENSE"                           = Join-Path $repo "LICENSE"
    "plugins/AFKManager/AFKManager.dll" = $Dll
}
foreach ($path in $entries.Values) { if (-not (Test-Path $path)) { throw "Missing package file: $path" } }

$dist = Join-Path $repo "dist"
New-Item -ItemType Directory -Force $dist | Out-Null
$zip = Join-Path $dist "AFKManager-$version.zip"
if ([IO.File]::Exists($zip)) { [IO.File]::Delete($zip) }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
# Entry names are given explicitly: Compress-Archive on Windows PowerShell 5.1 would write backslashes.
$archive = [IO.Compression.ZipFile]::Open($zip, 'Create')
try {
    foreach ($name in $entries.Keys) {
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $entries[$name], $name)
    }
} finally { $archive.Dispose() }

Write-Host "Created $zip"
Write-Host ("SHA-256 " + (Get-FileHash $zip -Algorithm SHA256).Hash)
