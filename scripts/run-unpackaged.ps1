<#
.SYNOPSIS
  Build and run MailClient WITHOUT MSIX packaging (no package identity, no Developer Mode needed).
.DESCRIPTION
  Default = self-contained Windows App SDK runtime (-p:MailClientUnpackaged=true), the same flow used for
  screenshots and CI. -FrameworkDependent uses the installed Microsoft.WindowsAppRuntime.2 framework package
  through the bootstrapper instead (smaller output; verifies the runtime installed on this machine).
  The local database defaults to %LOCALAPPDATA%\MailClient\mail.db; pass -DbPath to use a throw-away demo DB.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [ValidateSet('x64', 'x86', 'ARM64')][string]$Platform = 'x64',
    [switch]$FrameworkDependent,
    [switch]$NoBuild,
    [switch]$NoLaunch,
    [string]$DbPath
)
. (Join-Path $PSScriptRoot '_common.ps1')

$mode = if ($FrameworkDependent) { 'unpackaged-fd' } else { 'unpackaged' }
if (-not $NoBuild) {
    Set-BuildMode $mode
    $extra = @('-p:MailClientUnpackaged=true')
    if ($FrameworkDependent) { $extra += '-p:MailClientFrameworkDependent=true' }
    Invoke-AppBuild $extra $Configuration $Platform
}
$exe = Join-Path (Get-OutDir $Configuration $Platform) 'MailClient.App.exe'
if (-not (Test-Path $exe)) { throw "Not found: $exe (build first)." }
if ($NoLaunch) { Write-Host "Built: $exe"; return }

if ($DbPath) { $env:MAILCLIENT_DB = $DbPath }
Write-Host "Starting $exe" -ForegroundColor Green
Start-Process $exe -WorkingDirectory (Split-Path $exe)
