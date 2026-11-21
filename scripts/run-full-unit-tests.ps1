# 跑全量单元测试（自动排除需要真实账号的联调测试，规则见 _common.ps1）。
param(
    [switch]$NoBuild
)

. (Join-Path $PSScriptRoot '_common.ps1')

$logDir = Join-Path $RepoRoot 'artifacts'
New-Item -ItemType Directory $logDir -Force | Out-Null
$log = Join-Path $logDir 'test-full.log'
Set-Content -Path $log -Value $null

# 两个测试工程分别执行：核心逻辑（net8.0）与 App 层（net8.0-windows）。
# App 层测试只覆盖不依赖 WinUI 视觉树的纯逻辑，显式关闭 Windows App SDK 的模块自动初始化，
# 避免在未预装 Windows App Runtime 的机器（如 GitHub Actions windows-latest）上于 <Module>.cctor 阶段抛 COMException。
$coreArgs = @()
if ($NoBuild) { $coreArgs += '--no-build' }

$appTestProps = @(
    '-p:WindowsAppSdkDeploymentManagerInitialize=false',
    '-p:WindowsAppSdkBootstrapInitialize=false',
    '-p:WindowsAppSdkUndockedRegFreeWinRTInitialize=false',
    '-p:WindowsAppSdkCompatibilityInitialize=false'
)

$failed = 0

Write-Host "== dotnet test $(Split-Path -Leaf $TestProj) ==" -ForegroundColor Cyan
dotnet test $TestProj -c Release -p:Platform=x64 @coreArgs --filter $UnitTestFilter 2>&1 | Tee-Object -FilePath $log -Append
if ($LASTEXITCODE -ne 0) { $failed = 1 }

Write-Host "== dotnet test $(Split-Path -Leaf $AppTestProj) ==" -ForegroundColor Cyan
dotnet test $AppTestProj -c Release -p:Platform=x64 @appTestProps --filter $UnitTestFilter 2>&1 | Tee-Object -FilePath $log -Append
if ($LASTEXITCODE -ne 0) { $failed = 1 }

exit $failed
