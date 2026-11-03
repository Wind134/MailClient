<#
.SYNOPSIS
  生成 .exe 安装包（MailClient-Setup-<版本>.exe）和免安装便携版 zip，输出到 artifacts\release\。
.DESCRIPTION
  1. 免打包 + 自包含（Windows App SDK 与 .NET 都带在里面）发布 MailClient.App，不依赖任何运行时、也不需要开发人员模式。
  2. 发布 WinUI 3 安装程序（installer\MailClient.Setup，安装 / 卸载界面）。
  3. 合并为一个负载目录，压成 payload.zip，嵌入 installer\MailClient.Setup.Bootstrap（net472 小壳）
     —— 最终得到一个自解压的单文件 MailClient-Setup-<版本>.exe：双击 → 解压到 %TEMP% → 启动 WinUI 安装界面。
  4. 便携版：自包含应用目录直接打成 zip（解压即用，不写注册表、不建快捷方式）。
  版本号只读 Directory.Build.props 里的 MailClientVersion（改版本只改那一处）。
  不会签名；不会改系统设置；不会碰 %LocalAppData%\MailClient。
.PARAMETER SkipTests
  默认会先跑单元测试（排除 Live* 联调测试，与 run-full-unit-tests.ps1 相同）；加此开关跳过。
.PARAMETER OutDir
  输出目录，默认 artifacts\release。
#>
[CmdletBinding()]
param(
    [string]$OutDir,
    [switch]$SkipTests,
    [switch]$KeepWork
)
. (Join-Path $PSScriptRoot '_common.ps1')
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$Configuration = 'Release'
$Platform = 'x64'
$Rid = 'win-x64'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$env:DOTNET_NOLOGO = '1'

# ---- 版本号：Directory.Build.props 是唯一来源 ----
[xml]$props = Get-Content (Join-Path $RepoRoot 'Directory.Build.props') -Raw -Encoding UTF8
$node = $props.SelectSingleNode('//MailClientVersion')
$Version = if ($node) { $node.InnerText.Trim() } else { $null }
if (-not $Version) { throw 'Directory.Build.props 里没有 MailClientVersion。' }
$Short = ([version]$Version)
# 文件名里用 1.0.14（不带末尾 .0），和 Git 标签 v1.0.14 对应
$FileVersion = if ($Short.Revision -eq 0) { $Short.ToString(3) } else { $Short.ToString() }

if (-not $OutDir) { $OutDir = Join-Path $RepoRoot 'artifacts\release' }
$Work = Join-Path $RepoRoot 'artifacts\build'
$AppOut = Join-Path $Work 'app'
$SetupOut = Join-Path $Work 'setup'
$Payload = Join-Path $Work 'payload'
$PayloadZip = Join-Path $Work 'payload.zip'
$SetupProj = Join-Path $RepoRoot 'installer\MailClient.Setup\MailClient.Setup.csproj'
$BootProj = Join-Path $RepoRoot 'installer\MailClient.Setup.Bootstrap\MailClient.Setup.Bootstrap.csproj'
$TestProj = Join-Path $RepoRoot 'tests\MailClient.Tests\MailClient.Tests.csproj'

function Invoke-Dotnet([string[]]$Arguments) {
    Write-Host "dotnet $($Arguments -join ' ')" -ForegroundColor Cyan
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) 失败（exit $LASTEXITCODE）。" }
}

function New-ZipFromFolder([string]$Folder, [string]$ZipPath, [string]$Prefix) {
    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
    $root = (Resolve-Path $Folder).Path.TrimEnd('\')
    $fs = [IO.File]::Open($ZipPath, [IO.FileMode]::CreateNew)
    try {
        $zip = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create)
        try {
            Get-ChildItem $root -Recurse -File | ForEach-Object {
                $rel = $_.FullName.Substring($root.Length + 1).Replace('\', '/')
                if ($Prefix) { $rel = "$Prefix/$rel" }
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $rel, [IO.Compression.CompressionLevel]::Optimal)
            }
        } finally { $zip.Dispose() }
    } finally { $fs.Dispose() }
}

Write-Host "== MailClient $Version 安装包构建 ==" -ForegroundColor Green

# ---- 0. 单元测试 ----
if (-not $SkipTests) {
    $filter = 'FullyQualifiedName!~LiveGmail&FullyQualifiedName!~LiveGraph&FullyQualifiedName!~LiveImap&FullyQualifiedName!~LiveRefresh'
    Invoke-Dotnet @('test', $TestProj, '-c', $Configuration, "-p:Platform=$Platform", '--filter', $filter, '--nologo', '-v:q')
}

# ---- 1. 清理工作目录 ----
if (Test-Path $Work) { Remove-Item $Work -Recurse -Force }
New-Item -ItemType Directory $Work, $OutDir -Force | Out-Null

# ---- 2. 应用：免打包 + 自包含 ----
Set-BuildMode 'unpackaged'
Invoke-Dotnet @('publish', $AppProj, '-c', $Configuration, "-p:Platform=$Platform", '-r', $Rid,
    '-p:MailClientUnpackaged=true', '-o', $AppOut, '--nologo', '-v:q')
if (-not (Test-Path (Join-Path $AppOut 'MailClient.App.exe'))) { throw '应用发布失败：没有 MailClient.App.exe。' }

# Windows App SDK 2.x 自包含发布不会带上 Microsoft.WindowsAppRuntime.Insights.Resource.dll
# （微软 issue #6071 / #6387，2.5.1 仍未修复），缺它时 AppNotificationManager.Register() 会抛 0x8007007E，系统通知全部失效。
# 这个 dll 只存在于 Microsoft.WindowsAppSDK.Runtime 包里的框架 MSIX，这里把它单独解出来放到应用目录。
$InsightsDll = 'Microsoft.WindowsAppRuntime.Insights.Resource.dll'
if (-not (Test-Path (Join-Path $AppOut $InsightsDll))) {
    $nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
    $runtimePkg = Join-Path $nugetRoot 'microsoft.windowsappsdk.runtime'
    $msix = Get-ChildItem $runtimePkg -Recurse -Filter 'Microsoft.WindowsAppRuntime.2.msix' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "win10-$([regex]::Escape(($Rid -replace '^win-', '')))\\" } |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $msix) { throw "找不到 Windows App SDK Runtime 包里的框架 MSIX（$runtimePkg），无法取得 $InsightsDll。请先还原 NuGet 包。" }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($msix.FullName)
    try {
        $entry = $zip.Entries | Where-Object { $_.FullName -eq $InsightsDll -or $_.Name -eq $InsightsDll } | Select-Object -First 1
        if (-not $entry) { throw "$($msix.Name) 里没有 $InsightsDll。" }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $AppOut $InsightsDll), $true)
    } finally { $zip.Dispose() }
    Write-Host "已补入 $InsightsDll（来自 $($msix.Directory.Parent.Parent.Name) 的框架包）"
}

# ---- 3. 安装程序（WinUI 3） ----
Invoke-Dotnet @('publish', $SetupProj, '-c', $Configuration, "-p:Platform=$Platform", '-r', $Rid,
    '-o', $SetupOut, '--nologo', '-v:q')

# ---- 4. 合并负载：先放安装程序，再用应用的文件覆盖同名运行时文件 ----
New-Item -ItemType Directory $Payload -Force | Out-Null
Copy-Item (Join-Path $SetupOut '*') $Payload -Recurse -Force
Copy-Item (Join-Path $AppOut '*') $Payload -Recurse -Force
# 安装程序自己用到的文件清单：卸载时这些文件要等卸载程序退出后才能删
$setupRoot = (Resolve-Path $SetupOut).Path.TrimEnd('\')
Get-ChildItem $SetupOut -Recurse -File | ForEach-Object { $_.FullName.Substring($setupRoot.Length + 1) } |
    Set-Content -LiteralPath (Join-Path $Payload 'setup-files.txt') -Encoding UTF8
foreach ($must in 'MailClient.App.exe', 'MailClient.Setup.exe', 'MailClient.Setup.Core.dll') {
    if (-not (Test-Path (Join-Path $Payload $must))) { throw "负载里缺少 $must。" }
}
New-ZipFromFolder $Payload $PayloadZip ''
Write-Host ("payload.zip = {0:N1} MB" -f ((Get-Item $PayloadZip).Length / 1MB))

# ---- 5. 自解压外壳 ----
Invoke-Dotnet @('build', $BootProj, '-c', $Configuration, "-p:PayloadZip=$PayloadZip", "-p:Version=$Version", '--nologo', '-v:q')
$bootExe = Get-ChildItem (Join-Path $RepoRoot 'installer\MailClient.Setup.Bootstrap\bin') -Recurse -Filter 'MailClient-Setup.exe' |
    Where-Object { $_.FullName -match '\\Release\\' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $bootExe) { throw '没有找到生成的 MailClient-Setup.exe。' }
$setupName = "MailClient-Setup-$FileVersion.exe"
Copy-Item $bootExe.FullName (Join-Path $OutDir $setupName) -Force

# ---- 6. 便携版 zip（只含应用本身，不含安装程序） ----
$zipName = "MailClient-$FileVersion-win-x64-portable.zip"
New-ZipFromFolder $AppOut (Join-Path $OutDir $zipName) 'MailClient'

# ---- 7. 校验和 ----
$sums = foreach ($n in $setupName, $zipName) {
    $h = (Get-FileHash (Join-Path $OutDir $n) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$h  $n"
}
Set-Content -Path (Join-Path $OutDir 'SHA256SUMS.txt') -Value $sums -Encoding ascii

if (-not $KeepWork) { Remove-Item $Work -Recurse -Force }

Write-Host ''
Write-Host '完成：' -ForegroundColor Green
Get-ChildItem $OutDir -File | ForEach-Object { '  {0}  ({1:N1} MB)' -f $_.FullName, ($_.Length / 1MB) }
Write-Host '提示：安装包与便携版未签名，首次运行 Windows SmartScreen 会提示「更多信息 → 仍要运行」。' -ForegroundColor Yellow
