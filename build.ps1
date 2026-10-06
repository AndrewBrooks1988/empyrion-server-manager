<#
.SYNOPSIS
    Builds Empyrion Server Manager and packages a shareable release zip in dist\.

.DESCRIPTION
    1. dotnet publish (self-contained, single file) into a clean staging folder
    2. adds README.md
    3. refuses to package if personal settings or secrets slipped in
    4. zips to dist\EmpyrionServerManager-<version>-win-x64.zip

    Your own install (app\, with manager-settings.json) is not touched.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build.ps1
#>
$ErrorActionPreference = 'Stop'
$root    = $PSScriptRoot
$src     = Join-Path $root 'src'
$stage   = Join-Path $root 'dist\stage'
$version = ([xml](Get-Content (Join-Path $src 'EmpyrionManager.csproj'))).Project.PropertyGroup.Version | Select-Object -First 1
$zip     = Join-Path $root "dist\EmpyrionServerManager-$version-win-x64.zip"

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null

Write-Host "Publishing v$version ..."
dotnet publish $src -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $stage -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# IIS leftovers aren't needed on a desktop install
'web.config' | ForEach-Object { $f = Join-Path $stage $_; if (Test-Path $f) { Remove-Item $f } }
Copy-Item (Join-Path $root 'README.md') $stage

# ---- the on-screen alert server mod (bundled in mod\, installed into the server from the dashboard).
# Built against Mif.dll from a local Empyrion install (Eleon's file - referenced, never redistributed).
$modProj = Join-Path $root 'mod\EmpyrionManagerAlerts'
Write-Host "Building alert mod ..."
dotnet build $modProj -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Alert mod build failed (is EmpyrionManaged pointing at your server's Managed folder?)" }
$modOut = Join-Path $stage 'mod\EmpyrionManagerAlerts'
New-Item -ItemType Directory -Force $modOut | Out-Null
Copy-Item (Join-Path $modProj 'bin\Release\net48\EmpyrionManagerAlerts.dll'), (Join-Path $modProj 'bin\Release\net48\EmpyrionManagerAlerts_Info.yaml') $modOut

# ---- safety checks: nothing personal in a release
$personal = Get-ChildItem $stage -Recurse -File | Where-Object { $_.Name -match '^manager-settings\.json|\.bak$|players\.json|sent-messages' }
if ($personal) { throw "Personal files in the release: $($personal.Name -join ', ')" }
$textFiles = Get-ChildItem $stage -Recurse -File -Include *.json, *.ps1, *.js, *.html, *.css, *.md
$hits = $textFiles | Select-String -Pattern '\b7656119\d{10}\b', 'Tel_Pwd:\s*\S{8,}', 'AccessPasswordHash"\s*:\s*"\d' -List
if ($hits) { throw "Possible secrets/SteamIDs in: $($hits.Path -join ', ')" }

if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Write-Host "Release: $zip ($([math]::Round((Get-Item $zip).Length / 1MB)) MB)"
