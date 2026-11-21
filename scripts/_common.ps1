# 开发脚本共用的辅助定义（由其它脚本 dot-source 引入，不单独执行）。
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:RepoRoot  = Split-Path -Parent $PSScriptRoot
$script:AppDir    = Join-Path $RepoRoot 'src\MailClient.App'
$script:AppProj   = Join-Path $AppDir 'MailClient.App.csproj'
$script:ManifestSrc = Join-Path $AppDir 'Package.appxmanifest'
$script:TestProj  = Join-Path $RepoRoot 'tests\MailClient.Tests\MailClient.Tests.csproj'
$script:AppTestProj = Join-Path $RepoRoot 'tests\MailClient.App.Tests\MailClient.App.Tests.csproj'

# 联调测试（需要真实账号凭据）统一打 Trait("Category","Live")，此处按 Trait 排除，
# 不再依赖类名前缀 —— 否则改名就会让过滤失效，或误伤不需要凭据的用例。
$script:UnitTestFilter = 'Category!=Live'

function Get-OutDir([string]$Configuration, [string]$Platform) {
    $rid = 'win-' + $Platform.ToLowerInvariant()
    Join-Path $AppDir "bin\$Platform\$Configuration\net8.0-windows10.0.26100.0\$rid"
}

# 在同一份 obj+bin 里切换「打包 / 免打包 / 框架依赖」会残留过时产物
#（runtimeconfig.json、AppxManifest.xml 等）。记住上次的构建模式，变了就清空。
function Set-BuildMode([string]$Mode) {
    $marker = Join-Path $AppDir 'obj\.build-mode'
    $last = if (Test-Path $marker) { (Get-Content $marker -Raw).Trim() } else { '' }
    if ($last -ne $Mode) {
        if ($last) { Write-Host "构建模式变更（$last -> $Mode）：清理 src\MailClient.App 的 obj 与 bin" -ForegroundColor Yellow }
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
    if ($LASTEXITCODE -ne 0) { throw "dotnet build 失败（退出码 $LASTEXITCODE）。" }
}

function Test-DeveloperMode {
    $v = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -Name AllowDevelopmentWithoutDevLicense -ErrorAction SilentlyContinue
    return ($null -ne $v -and $v.AllowDevelopmentWithoutDevLicense -eq 1)
}

# 从生成的 AppxManifest 里读出框架依赖，并报告本机已安装的版本。
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
    Write-Host "启动 shell:AppsFolder\$aumid" -ForegroundColor Cyan
    Start-Process explorer.exe "shell:AppsFolder\$aumid"
}
