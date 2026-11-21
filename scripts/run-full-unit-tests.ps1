# 跑全量单元测试（自动排除需要真实账号的联调测试，规则见 _common.ps1）。
. (Join-Path $PSScriptRoot '_common.ps1')

$logDir = Join-Path $RepoRoot 'artifacts'
New-Item -ItemType Directory $logDir -Force | Out-Null
$log = Join-Path $logDir 'test-full.log'

# 两个测试工程分别执行：核心逻辑（net8.0）与 App 层（net8.0-windows）。
$failed = 0
foreach ($proj in @($TestProj, $AppTestProj)) {
    Write-Host "== dotnet test $(Split-Path -Leaf $proj) ==" -ForegroundColor Cyan
    dotnet test $proj -c Release -p:Platform=x64 --filter $UnitTestFilter 2>&1 | Tee-Object -FilePath $log -Append
    if ($LASTEXITCODE -ne 0) { $failed = 1 }
}

exit $failed
