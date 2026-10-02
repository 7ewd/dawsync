# Windows x64 用の自己完結 exe と zip を作る。別途 .NET のインストールは不要。
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = [System.IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$stage = Join-Path $tempRoot ("dawsync-publish-" + [Guid]::NewGuid().ToString("N"))
$publish = Join-Path $stage "publish"
$dist = Join-Path $root "dist"
$out = Join-Path $dist "win-x64"
$zip = Join-Path $dist "DAW-Sync-windows.zip"

try {
    New-Item -ItemType Directory -Path $publish -Force | Out-Null
    dotnet publish (Join-Path $root "src\DawSync.App\DawSync.App.csproj") -c Release -r win-x64 --self-contained true -o $publish -p:BaseOutputPath="$stage\build\" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)." }

    $files = @(Get-ChildItem -LiteralPath $publish -File -Recurse)
    if ($files.Count -ne 1 -or $files[0].Name -ne "DawSync.exe") {
        $names = ($files | ForEach-Object { $_.FullName }) -join [Environment]::NewLine
        throw "Expected only DawSync.exe in the publish output. Found:$([Environment]::NewLine)$names"
    }

    New-Item -ItemType Directory -Path $out -Force | Out-Null
    $stagedZip = Join-Path $stage "DAW-Sync-windows.zip"
    Compress-Archive -LiteralPath $files[0].FullName -DestinationPath $stagedZip
    Copy-Item -LiteralPath $stagedZip -Destination $zip -Force

    # 既存の配布フォルダや起動中の exe は削除しない。
    try {
        Copy-Item -LiteralPath $files[0].FullName -Destination (Join-Path $out "DawSync.exe") -Force
    } catch {
        throw "The new zip is ready at '$zip', but DawSync.exe could not be replaced. Close the app and run this script again. $($_.Exception.Message)"
    }
    Write-Host "EXE: $(Join-Path $out 'DawSync.exe')"
    Write-Host "ZIP: $zip"
} finally {
    $resolvedStage = [System.IO.Path]::GetFullPath($stage)
    $tempPrefix = $tempRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedStage.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        [System.IO.Path]::GetFileName($resolvedStage) -notmatch '^dawsync-publish-[0-9a-f]{32}$') {
        throw "Refusing to remove an unexpected temporary directory: $resolvedStage"
    }
    if (Test-Path -LiteralPath $resolvedStage) {
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}
