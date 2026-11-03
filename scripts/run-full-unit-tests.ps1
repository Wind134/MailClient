$ErrorActionPreference = 'Stop'
# This script lives in <repo>/scripts
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
# Exclude live/integration tests that need credentials
$filter = 'FullyQualifiedName!~LiveGmail&FullyQualifiedName!~LiveGraph&FullyQualifiedName!~LiveImap&FullyQualifiedName!~LiveRefresh'
$logDir = Join-Path $root 'artifacts'
New-Item -ItemType Directory $logDir -Force | Out-Null
$log = Join-Path $logDir 'test-full.log'
dotnet test tests\MailClient.Tests\MailClient.Tests.csproj -c Release -p:Platform=x64 --filter $filter 2>&1 | Tee-Object -FilePath $log
exit $LASTEXITCODE
