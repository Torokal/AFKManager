# Builds AFKManager.dll without the .NET SDK, using the C# compiler that ships with Windows (.NET Framework 4.x, C# 5).
# The source is deliberately C# 5 compatible. With the .NET SDK installed you can use `dotnet build -c Release` instead.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Managed "<server>\valheim_server_Data\Managed" -BepInExCore "<server>\BepInEx\core"
# Game/BepInEx assemblies are only referenced, never copied or committed.
param(
    [Parameter(Mandatory = $true)][string]$Managed,
    [Parameter(Mandatory = $true)][string]$BepInExCore,
    [string]$Out = ""
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Out) { $Out = Join-Path $root "bin\Release" }
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }
New-Item -ItemType Directory -Force $Out | Out-Null

$gameRefs = "mscorlib", "netstandard", "System", "System.Core", "UnityEngine", "UnityEngine.CoreModule",
            "UnityEngine.AnimationModule", "assembly_valheim", "assembly_utils", "Splatform"
$refs = @()
foreach ($r in $gameRefs) {
    $p = Join-Path $Managed "$r.dll"
    if (-not (Test-Path $p)) { throw "Missing reference: $p" }
    $refs += "/r:$p"
}
foreach ($r in "BepInEx", "0Harmony") {
    $p = Join-Path $BepInExCore "$r.dll"
    if (-not (Test-Path $p)) { throw "Missing reference: $p" }
    $refs += "/r:$p"
}
$sources = Get-ChildItem (Join-Path $root "src") -Filter *.cs | ForEach-Object { $_.FullName }
$dll = Join-Path $Out "AFKManager.dll"
& $csc /nologo /target:library /nostdlib /noconfig /optimize+ /debug- /warn:4 "/out:$dll" @refs @sources
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }
Write-Host "Built $dll"
