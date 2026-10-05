<#
.SYNOPSIS
    Builds a release: portable zip + installer + update signature, and (with -Publish) a GitHub release.

.DESCRIPTION
    1. build.ps1                 -> dist\stage + dist\EmpyrionServerManager-<v>-win-x64.zip (portable)
    2. Inno Setup                -> dist\EmpyrionServerManager-Setup-<v>.exe
    3. tools\UpdateSigner        -> dist\EmpyrionServerManager-Setup-<v>.exe.sig (checked by the in-app updater)
    4. -Publish: git tag v<v>, push, `gh release create` with the three files and the CHANGELOG section as notes

    The update signing key must exist (default %USERPROFILE%\.empyrion-server-manager\update-signing-key.pem) and is
    never committed. Its public half is embedded in src\Updates.cs.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File release.ps1            # build + sign only
    powershell -ExecutionPolicy Bypass -File release.ps1 -Publish   # ...and publish to GitHub
#>
param(
    [switch] $Publish,
    [string] $SigningKey = (Join-Path $env:USERPROFILE '.empyrion-server-manager\update-signing-key.pem')
)
$ErrorActionPreference = 'Stop'
$root    = $PSScriptRoot
$version = ([xml](Get-Content (Join-Path $root 'src\EmpyrionManager.csproj'))).Project.PropertyGroup.Version | Select-Object -First 1
$dist    = Join-Path $root 'dist'

function Find-Tool($name, [string[]] $candidates) {
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    throw "$name not found."
}
$iscc = Find-Tool 'ISCC.exe' @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe")
if (-not (Test-Path $SigningKey)) { throw "Update signing key not found: $SigningKey" }

# 1. publish + portable zip (also runs the personal-data safety checks)
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw "build.ps1 failed" }

# 2. installer
Write-Host "Building installer v$version ..."
& $iscc /Q "/DAppVersion=$version" (Join-Path $root 'installer\EmpyrionServerManager.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }
$setup = Join-Path $dist "EmpyrionServerManager-Setup-$version.exe"

# 3. sign + verify against the public key the app ships with
$signer = Join-Path $root 'tools\UpdateSigner'
dotnet run --project $signer -c Release -- sign $setup $SigningKey
dotnet run --project $signer -c Release -- verify $setup "$setup.sig" (Join-Path $root 'tools\update-public-key.pem')
if ($LASTEXITCODE -ne 0) { throw "Signature check failed" }

$zip = Join-Path $dist "EmpyrionServerManager-$version-win-x64.zip"
Write-Host "`nRelease files:"
Get-Item $setup, "$setup.sig", $zip | ForEach-Object { "  {0,-50} {1,6} MB" -f $_.Name, [math]::Round($_.Length / 1MB, 1) }

if (-not $Publish) { Write-Host "`nNot published (run with -Publish)."; return }

# 4. GitHub release
$gh = Find-Tool 'gh.exe' @("$env:ProgramFiles\GitHub CLI\gh.exe")
$notes = Join-Path $dist "notes-$version.md"
$changelog = [IO.File]::ReadAllText((Join-Path $root 'CHANGELOG.md'))   # UTF-8 (Get-Content would misread it on PowerShell 5.1)
$section = [regex]::Match($changelog, "(?ms)^## $([regex]::Escape($version))\s*\r?\n(.*?)(?=^## |\z)").Groups[1].Value.Trim()
@"
$section

**Install:** download ``EmpyrionServerManager-Setup-$version.exe`` and run it (Windows may show a SmartScreen warning for this unsigned installer: More info -> Run anyway).
Already installed? The dashboard offers the update automatically. ``.sig`` is the update signature checked by the app.
Portable: ``EmpyrionServerManager-$version-win-x64.zip`` (unzip and run ``EmpyrionManager.exe``; no auto-update).
"@ | Set-Content $notes -Encoding utf8

git -C $root tag -a "v$version" -m "v$version"
git -C $root push origin "v$version"
& $gh release create "v$version" $setup "$setup.sig" $zip --repo AndrewBrooks1988/empyrion-server-manager --title "v$version" --notes-file $notes
