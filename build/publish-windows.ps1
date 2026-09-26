# Windows 用の配布ファイル（AbletonMulti.exe 1 つ）を dist/ に作る
# dist\win-x64\AbletonMulti.exe を起動したままでも作れるように、いったん一時フォルダに作ってから zip にする
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$build = Join-Path ([System.IO.Path]::GetTempPath()) "abletonmulti-publish-win"
if (Test-Path $build) { Remove-Item $build -Recurse -Force }

dotnet publish (Join-Path $root "src\AbletonMulti.App") -c Release -r win-x64 -o $build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$zip = Join-Path $root "dist\AbletonMulti-windows.zip"
New-Item -ItemType Directory -Force (Join-Path $root "dist") | Out-Null
Compress-Archive -Path (Join-Path $build "AbletonMulti.exe") -DestinationPath $zip -Force

$out = Join-Path $root "dist\win-x64"
New-Item -ItemType Directory -Force $out | Out-Null
try {
    Copy-Item (Join-Path $build "AbletonMulti.exe") $out -Force
} catch {
    Write-Host "dist\win-x64\AbletonMulti.exe は起動中なので置き換えませんでした（zip は新しくなっています）"
}
Write-Host "完成: $zip"
