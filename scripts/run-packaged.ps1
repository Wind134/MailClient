<#
.SYNOPSIS
  Build the MSIX-packaged app, register it as a loose-file dev package and launch it by AUMID.
.DESCRIPTION
  NEVER start the packaged build's MailClient.App.exe straight from bin\ : it has no package identity, so the
  Windows App SDK module initializer throws REGDB_E_CLASSNOTREG (0x80040154) before Main. The exe only works
  when Windows activates it through its registered package (shell:AppsFolder\<PFN>!App).
  Requirements: Developer Mode ON (Settings > System > For developers), because Add-AppxPackage -Register of
  loose files is only allowed with a developer licence. This script only READS that setting and stops with
  instructions if it is off; it never changes system settings. If Developer Mode is not an option use
  scripts\build-msix.ps1 (signed .msix; the certificate must be trusted by an administrator once).
.PARAMETER InstallRuntime
  If the Microsoft.WindowsAppRuntime framework required by the manifest is missing, install it from the
  Windows App SDK NuGet package (tools\MSIX\win10-<arch>\Microsoft.WindowsAppRuntime.*.msix).
.PARAMETER Unregister
  Remove the dev registration instead of launching.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [ValidateSet('x64', 'x86', 'ARM64')][string]$Platform = 'x64',
    [switch]$NoBuild,
    [switch]$NoLaunch,
    [switch]$InstallRuntime,
    [switch]$Unregister
)
. (Join-Path $PSScriptRoot '_common.ps1')

[xml]$src = Get-Content $ManifestSrc -Raw
$identityName = $src.Package.Identity.Name

if ($Unregister) {
    Get-AppxPackage -Name $identityName | ForEach-Object { Write-Host "Removing $($_.PackageFullName)"; Remove-AppxPackage $_.PackageFullName }
    return
}

if (-not (Test-DeveloperMode)) {
    Write-Host @"

Developer Mode is OFF on this machine (HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock\AllowDevelopmentWithoutDevLicense != 1).
Registering a loose-file MSIX package then fails with 0x80073CFF, and starting the exe from bin\ crashes with REGDB_E_CLASSNOTREG.
Options (none is applied automatically):
  1. Turn on Settings > System > For developers > Developer Mode (needs admin), then re-run this script.
  2. scripts\build-msix.ps1 builds a signed .msix; trust its test certificate once from an elevated prompt (see docs\packaging.md).
  3. Use scripts\run-unpackaged.ps1 (no package identity needed).
"@ -ForegroundColor Yellow
    exit 2
}

if (-not $NoBuild) {
    Set-BuildMode 'packaged'
    Invoke-AppBuild @() $Configuration $Platform
}
$out = Get-OutDir $Configuration $Platform
$manifest = Join-Path $out 'AppxManifest.xml'
if (-not (Test-Path $manifest)) { throw "Not found: $manifest. The packaged build did not produce a layout (EnableMsixTooling)." }

$dep = Get-RuntimeDependency $manifest $Platform
if ($dep) {
    if ($dep.Installed.Count -eq 0) {
        Write-Host "Framework $($dep.Name) >= $($dep.MinVersion) ($Platform) is NOT installed." -ForegroundColor Yellow
        $nupkg = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windowsappsdk.runtime'
        $arch = if ($Platform -eq 'x64') { 'win10-x64' } elseif ($Platform -eq 'ARM64') { 'win10-arm64' } else { 'win10-x86' }
        $pkg = Get-ChildItem (Join-Path $nupkg "*\tools\MSIX\$arch") -Filter "$($dep.Name).*msix" -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
        if (-not $InstallRuntime) { throw "Re-run with -InstallRuntime to install $($pkg.FullName), or install the Windows App SDK runtime manually." }
        if (-not $pkg) { throw 'Runtime msix not found in the NuGet cache; restore the project first.' }
        Write-Host "Installing $($pkg.FullName)" -ForegroundColor Cyan
        Add-AppxPackage -Path $pkg.FullName
    } else {
        Write-Host "Runtime OK: $($dep.Name) $($dep.Installed[0].Version) (required >= $($dep.MinVersion))" -ForegroundColor Green
    }
}

Write-Host "Registering $manifest" -ForegroundColor Cyan
Add-AppxPackage -Register $manifest
$pkgInfo = Get-AppxPackage -Name $identityName
if (-not $pkgInfo) { throw 'Registration did not produce a package.' }
Write-Host "Registered $($pkgInfo.PackageFullName) (development mode: $($pkgInfo.IsDevelopmentMode))" -ForegroundColor Green
if ($NoLaunch) { return }
Start-AppWithAumid $pkgInfo.PackageFamilyName 'App'
