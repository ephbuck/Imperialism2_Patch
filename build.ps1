$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcDir   = Join-Path $repoRoot "src"
$objDir   = Join-Path $repoRoot "obj"
$gameDir  = Join-Path $repoRoot "Imperialism II - Patched"

$csc   = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
$ilasm = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\ilasm.exe"

if (-not (Test-Path $csc))   { throw "32-bit csc.exe not found at $csc" }
if (-not (Test-Path $ilasm)) { throw "32-bit ilasm.exe not found at $ilasm" }

New-Item -ItemType Directory -Force -Path $objDir | Out-Null

Write-Host "=== 1. Compiling Imp2Net.dll (x86 with GraphicsBridge) ==="
$imp2NetDll = Join-Path $objDir "Imp2Net.dll"
& $csc /nologo /platform:x86 /target:library /optimize+ /unsafe `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    /out:$imp2NetDll (Join-Path $srcDir "Imp2Net.cs") (Join-Path $srcDir "GraphicsBridge.cs")
if ($LASTEXITCODE -ne 0) { throw "csc.exe failed with exit code $LASTEXITCODE" }

# Sync Imp2Net.dll to tests directory for test harness
$testsDir = Join-Path $repoRoot "tests"
if (Test-Path $testsDir) {
    Copy-Item -LiteralPath $imp2NetDll -Destination (Join-Path $testsDir "Imp2Net.dll") -Force
}

Write-Host "=== 2. Assembling standalone 32-bit dplayx.dll (with embedded Imp2Net.dll) ==="
Copy-Item -LiteralPath (Join-Path $srcDir "dplayx.il") -Destination (Join-Path $objDir "dplayx.il") -Force
$builtDplayx = Join-Path $objDir "dplayx.dll"
Push-Location $objDir
try {
    & $ilasm /nologo /quiet /dll /output=$builtDplayx "dplayx.il"
    if ($LASTEXITCODE -ne 0) { throw "ilasm.exe failed with exit code $LASTEXITCODE" }
} finally {
    Pop-Location
}

# Copy dplayx.dll to repo root
$rootDplayx = Join-Path $repoRoot "dplayx.dll"
Copy-Item -LiteralPath $builtDplayx -Destination $rootDplayx -Force
Write-Host "Built: $rootDplayx ($((Get-Item $rootDplayx).Length) bytes)"

# Clean up any legacy DirectDraw wrappers that cause top-left pinning or interference
Write-Host "=== 3. Cleaning Legacy DirectDraw Wrappers ==="
$legacyFiles = @(
    (Join-Path $repoRoot "ddraw.dll"),
    (Join-Path $repoRoot "ddraw.ini"),
    (Join-Path $repoRoot "cnc-ddraw config.exe")
)
foreach ($f in $legacyFiles) {
    if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force }
}

$iniContent = @"
[Graphics]
; Display mode: Fullscreen (borderless 4K/native resolution) or Windowed
Mode=Fullscreen

; Scaling aspect ratio: Fit (maintains 4:3 with side pillarboxes) or Stretch
AspectRatio=Fit

; Upscaling filter: Halftone (smooth anti-aliased) or Nearest (sharp pixel art)
Filter=Halftone

; Interactive world map camera zoom via mouse wheel
ZoomEnabled=1

; World map zoom downsampling filter: Smooth (anti-aliased box average), Sharp (contrast-preserved), or Nearest (crisp pixel art)
ZoomFilter=Smooth

[Multiplayer]
; Single port used for both UDP LAN discovery and TCP session traffic.
; For Internet hosting, forward only TCP port 2350 (UDP optional).
Port=2350

; Network interface selection:
;   All              = Listen on 0.0.0.0 when hosting; scan across ALL interfaces when joining.
;   <Adapter Name>   = Bind/scan a specific adapter (e.g., Ethernet, Wi-Fi, Tailscale).
;   <IPv4 Address>   = Bind/scan a specific local IPv4 (e.g., 192.168.1.50).
; Tip: Hold SHIFT while clicking Host or Join in-game to open the interactive
;      Network Interface Picker window, or append @<AdapterOrIP> in the Join IP box!
BindInterface=All

; Set PromptInterface=1 to always pop up the Network Interface Picker on Host/Join.
PromptInterface=0

; Timeout in milliseconds when discovering games.
DiscoveryTimeoutMs=3000

; Set Log=1 to write diagnostic logs to dplayx.log.
Log=0
"@

$rootIni = Join-Path $repoRoot "dplayx.ini"
[System.IO.File]::WriteAllText($rootIni, $iniContent, [System.Text.Encoding]::ASCII)

if (Test-Path -LiteralPath $gameDir) {
    Write-Host "=== 4. Patching & Deploying to Working Game Folder ==="
    $gameExe = Join-Path $gameDir "Imperialism II.exe"
    & (Join-Path $srcDir "PatchExe.ps1") -InputExe $gameExe -OutputExe $gameExe
    Copy-Item -LiteralPath $gameExe -Destination (Join-Path $repoRoot "ImpII.exe") -Force
    Copy-Item -LiteralPath $builtDplayx -Destination (Join-Path $gameDir "dplayx.dll") -Force
    $gameIni = Join-Path $gameDir "dplayx.ini"
    [System.IO.File]::WriteAllText($gameIni, $iniContent, [System.Text.Encoding]::ASCII)

    # Clean legacy wrapper files from game directory
    $gameLegacyFiles = @(
        (Join-Path $gameDir "ddraw.dll"),
        (Join-Path $gameDir "ddraw.ini"),
        (Join-Path $gameDir "cnc-ddraw config.exe")
    )
    foreach ($f in $gameLegacyFiles) {
        if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force }
    }
    $gameShaders = Join-Path $gameDir "Shaders"
    if (Test-Path -LiteralPath $gameShaders) { Remove-Item -Path $gameShaders -Recurse -Force }

    Write-Host "Deployed patched Imperialism II.exe, dplayx.dll, and dplayx.ini to: $gameDir"
}

Write-Host "=== Build Complete ==="
