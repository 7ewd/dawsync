# Windows 用の配布ファイル（AbletonMulti.exe 1 つ）を dist/ に作る
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "dist\win-x64"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish (Join-Path $root "src\AbletonMulti.App") -c Release -r win-x64 -o $out
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$zip = Join-Path $root "dist\AbletonMulti-windows.zip"
Compress-Archive -Path (Join-Path $out "AbletonMulti.exe") -DestinationPath $zip -Force
Write-Host "完成: $zip"
