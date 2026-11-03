# Shared helpers for the dev scripts (dot-sourced; not meant to be run directly).
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:RepoRoot  = Split-Path -Parent $PSScriptRoot
$script:AppDir    = Join-Path $RepoRoot 'src\MailClient.App'
$script:AppProj   = Join-Path $AppDir 'MailClient.App.csproj'
$script:ManifestSrc = Join-Path $AppDir 'Package.appxmanifest'

function Get-OutDir([string]$Configuration, [string]$Platform) {
    $rid = 'win-' + $Platform.ToLowerInvariant()
    Join-Path $AppDir "bin\$Platform\$Configuration\net8.0-windows10.0.26100.0\$rid"
}

# Switching between packaged / unpackaged / framework-dependent in the same obj+bin leaves stale
# artefacts (runtimeconfig.json, AppxManifest.xml, ...). Remember the last mode and wipe on change.
function Set-BuildMode([string]$Mode) {
    $marker = Join-Path $AppDir 'obj\.build-mode'
    $last = if (Test-Path $marker) { (Get-Content $marker -Raw).Trim() } else { '' }
    if ($last -ne $Mode) {
        if ($last) { Write-Host "Build mode changed ($last -> $Mode): cleaning src\MailClient.App\obj and bin" -ForegroundColor Yellow }
        foreach ($d in 'obj', 'bin') {
            $p = Join-Path $AppDir $d
            if (Test-Path $p) { Remove-Item $p -Recurse -Force }
        }
        New-Item -ItemType Directory (Join-Path $AppDir 'obj') -Force | Out-Null
        Set-Content -Path $marker -Value $Mode
    }
}

function Invoke-AppBuild([string[]]$Extra, [string]$Configuration, [string]$Platform) {
    Write-Host "dotnet build $($Extra -join ' ')" -ForegroundColor Cyan
    & dotnet build $AppProj -c $Configuration "-p:Platform=$Platform" @Extra --nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)." }
}

function Test-DeveloperMode {
    $v = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -Name AllowDevelopmentWithoutDevLicense -ErrorAction SilentlyContinue
    return ($null -ne $v -and $v.AllowDevelopmentWithoutDevLicense -eq 1)
}

# Reads the framework dependency from the generated AppxManifest and reports what is installed.
function Get-RuntimeDependency([string]$ManifestPath, [string]$Platform) {
    [xml]$m = Get-Content $ManifestPath -Raw -Encoding UTF8
    $ns = New-Object Xml.XmlNamespaceManager($m.NameTable)
    $ns.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $dep = $m.SelectSingleNode('//f:PackageDependency[starts-with(@Name,"Microsoft.WindowsAppRuntime")]', $ns)
    if (-not $dep) { return $null }
    $min = [version]$dep.MinVersion
    $installed = Get-AppxPackage -Name $dep.Name | Where-Object {
        $_.Architecture -eq $Platform -and [version]$_.Version -ge $min
    } | Sort-Object { [version]$_.Version } -Descending
    [pscustomobject]@{ Name = $dep.Name; MinVersion = $min; Installed = @($installed) }
}

function Start-AppWithAumid([string]$PackageFamilyName, [string]$AppId = 'App') {
    $aumid = "$PackageFamilyName!$AppId"
    Write-Host "Launching shell:AppsFolder\$aumid" -ForegroundColor Cyan
    Start-Process explorer.exe "shell:AppsFolder\$aumid"
}
