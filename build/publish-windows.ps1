# Windows 用の配布ファイル（Maltese.exe 1 つ）を dist/ に作る
# dist\win-x64\Maltese.exe を起動したままでも作れるように、いったん一時フォルダに作ってから zip にする
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$build = Join-Path ([System.IO.Path]::GetTempPath()) "maltese-publish-win"
if (Test-Path $build) { Remove-Item $build -Recurse -Force }

dotnet publish (Join-Path $root "src\Maltese.App") -c Release -r win-x64 -o $build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$zip = Join-Path $root "dist\Maltese-windows.zip"
New-Item -ItemType Directory -Force (Join-Path $root "dist") | Out-Null
Compress-Archive -Path (Join-Path $build "Maltese.exe") -DestinationPath $zip -Force

$out = Join-Path $root "dist\win-x64"
New-Item -ItemType Directory -Force $out | Out-Null
try {
    Copy-Item (Join-Path $build "Maltese.exe") $out -Force
} catch {
    Write-Host "dist\win-x64\Maltese.exe は起動中なので置き換えませんでした（zip は新しくなっています）"
}
Write-Host "完成: $zip"
