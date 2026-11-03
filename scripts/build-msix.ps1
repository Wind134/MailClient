<#
.SYNOPSIS
  Build a SIGNED sideload .msix (+ the Windows App Runtime framework msix) without installing anything.
.DESCRIPTION
  1. Creates a self-signed test certificate (Subject must equal Package.appxmanifest Identity/@Publisher, here
     CN=AppPublisher) once, exports dev.pfx/dev.cer to %LOCALAPPDATA%\MailClient\devcert and removes it from
     the certificate stores again (nothing is trusted or installed on the machine).
  2. Builds the package, signs it with signtool using the pfx.
  3. Prints the (administrator) steps needed to install it. Windows refuses an untrusted publisher, so the
     .cer has to be added to LocalMachine\TrustedPeople once; this script does NOT do that.
  For a real release replace the test certificate by your code-signing certificate (-PfxPath/-PfxPassword) and
  set Identity/@Publisher in Package.appxmanifest to that certificate's subject.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [ValidateSet('x64', 'x86', 'ARM64')][string]$Platform = 'x64',
    [string]$PfxPath,
    [string]$PfxPassword = 'mc-dev',
    [string]$OutputDir
)
. (Join-Path $PSScriptRoot '_common.ps1')

[xml]$src = Get-Content $ManifestSrc -Raw -Encoding UTF8
$publisher = $src.Package.Identity.Publisher
$certDir = Join-Path $env:LOCALAPPDATA 'MailClient\devcert'
if (-not $OutputDir) { $OutputDir = Join-Path $RepoRoot 'artifacts\msix' }

if (-not $PfxPath) {
    $PfxPath = Join-Path $certDir 'dev.pfx'
    if (-not (Test-Path $PfxPath)) {
        New-Item -ItemType Directory $certDir -Force | Out-Null
        $c = New-SelfSignedCertificate -Type Custom -Subject $publisher -KeyUsage DigitalSignature `
            -FriendlyName 'MailClient dev signing (temp)' -CertStoreLocation Cert:\CurrentUser\My `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
        $pw = ConvertTo-SecureString $PfxPassword -AsPlainText -Force
        Export-PfxCertificate -Cert $c -FilePath $PfxPath -Password $pw | Out-Null
        Export-Certificate -Cert $c -FilePath (Join-Path $certDir 'dev.cer') | Out-Null
        Remove-Item "Cert:\CurrentUser\My\$($c.Thumbprint)"
        Write-Host "Created test certificate $publisher in $certDir (removed from the cert store again)." -ForegroundColor Green
    }
}

Set-BuildMode 'msix'
Invoke-AppBuild @('-p:GenerateAppxPackageOnBuild=true', '-p:AppxPackageSigningEnabled=false', "-p:AppxPackageDir=$OutputDir\", '-p:AppxBundle=Never', '-p:UapAppxPackageBuildMode=SideloadOnly') $Configuration $Platform

$msix = Get-ChildItem $OutputDir -Recurse -Filter '*.msix' | Where-Object { $_.Directory.Name -notin 'x64', 'x86', 'arm64', 'win32' -and $_.FullName -notmatch '\\Dependencies\\' } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $msix) { throw "No .msix found under $OutputDir" }

$signtool = Get-ChildItem (Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools\*\bin\*\x64\signtool.exe') | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $signtool) { throw 'signtool.exe not found in the Microsoft.Windows.SDK.BuildTools package.' }
& $signtool.FullName sign /fd SHA256 /f $PfxPath /p $PfxPassword $msix.FullName
if ($LASTEXITCODE -ne 0) { throw "signtool failed ($LASTEXITCODE)" }

$cer = Join-Path (Split-Path $PfxPath) 'dev.cer'
Write-Host @"

Signed package : $($msix.FullName)
Framework msix : $(Join-Path $msix.DirectoryName "Dependencies\$Platform\Microsoft.WindowsAppRuntime.2.msix")
Test certificate: $cer

To install on THIS machine (one-time, elevated PowerShell; this changes the machine trust store, so it is not automated):
  Import-Certificate -FilePath '$cer' -CertStoreLocation Cert:\LocalMachine\TrustedPeople
  Add-AppxPackage '$($msix.FullName)'
Remove again with:
  Get-AppxPackage 49635C82-16E9-4745-B089-82337985CB02 | Remove-AppxPackage
  Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Subject -eq '$publisher' | Remove-Item
"@
