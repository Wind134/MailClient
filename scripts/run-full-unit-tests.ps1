# 跑全量单元测试（自动排除需要真实账号的联调测试，规则见 _common.ps1）。
. (Join-Path $PSScriptRoot '_common.ps1')

$logDir = Join-Path $RepoRoot 'artifacts'
New-Item -ItemType Directory $logDir -Force | Out-Null
$log = Join-Path $logDir 'test-full.log'

dotnet test $TestProj -c Release -p:Platform=x64 --filter $UnitTestFilter 2>&1 | Tee-Object -FilePath $log
exit $LASTEXITCODE
