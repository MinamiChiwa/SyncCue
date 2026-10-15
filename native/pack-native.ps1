# SyncCue native green pack 0.2.14 — slim top-level + single-file exe
$ErrorActionPreference = "Stop"
$native = "D:\Working\SyncCue\sync-cue-native"
$out = "D:\Working\SyncCue\sync-cue-native-release"
$stage = Join-Path $out "SyncCue-native-win-x64"
$publish = Join-Path $out "publish-tmp"
$ver = "0.2.14"

Get-Process SyncCue, cloudflared -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue }

Push-Location $native
dotnet publish -c Release -r win-x64 --self-contained true -o $publish `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=None `
  -p:DebugSymbols=false `
  -p:Version=$ver
Pop-Location

# Fresh stage
if (Test-Path $stage) {
  try { Remove-Item $stage -Recurse -Force -ErrorAction Stop }
  catch {
    # locked: wipe contents best-effort then rebuild into stage
    Get-ChildItem $stage -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
  }
}
New-Item -ItemType Directory -Path $stage -Force | Out-Null

# Top-level: only launcher + readme
Copy-Item (Join-Path $publish "SyncCue.exe") (Join-Path $stage "SyncCue.exe") -Force
python (Join-Path $native "_write_usage_pack.py") $stage $native $ver
if ($LASTEXITCODE -ne 0) { throw "write usage failed" }

# Sidecars in subfolders
$tools = Join-Path $stage "tools"
New-Item -ItemType Directory -Path $tools -Force | Out-Null
$cf = "D:\Working\SyncCue\sync-cue-v1\.runtime\tunnel-bin\cloudflared.exe"
if (Test-Path $cf) { Copy-Item $cf (Join-Path $tools "cloudflared.exe") -Force }

New-Item -ItemType Directory -Path (Join-Path $stage "server") -Force | Out-Null
Copy-Item "D:\Working\SyncCue\sync-cue-v1\server\*" (Join-Path $stage "server") -Recurse -Force
if (Test-Path "D:\Working\SyncCue\sync-cue-v1\shared") {
  New-Item -ItemType Directory -Path (Join-Path $stage "shared") -Force | Out-Null
  Copy-Item "D:\Working\SyncCue\sync-cue-v1\shared\*" (Join-Path $stage "shared") -Recurse -Force
}
if (Test-Path "D:\Working\SyncCue\sync-cue-v1\node_modules\ws") {
  New-Item -ItemType Directory -Path (Join-Path $stage "node_modules") -Force | Out-Null
  Copy-Item "D:\Working\SyncCue\sync-cue-v1\node_modules\ws" (Join-Path $stage "node_modules\ws") -Recurse -Force
}
Copy-Item "D:\Working\SyncCue\sync-cue-v1\package.json" (Join-Path $stage "package.json") -Force

Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue

$zip = Join-Path $out "SyncCue-native-$ver-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal

$topFiles = @(Get-ChildItem $stage -File | Select-Object -ExpandProperty Name)
$topDirs = @(Get-ChildItem $stage -Directory | Select-Object -ExpandProperty Name)
$exe = Get-Item (Join-Path $stage "SyncCue.exe")
Write-Host "TOP_FILES=$($topFiles -join ',')"
Write-Host "TOP_DIRS=$($topDirs -join ',')"
Write-Host "EXE_SIZE=$($exe.Length) EXE_TIME=$($exe.LastWriteTime)"
Write-Host "OK $zip"
