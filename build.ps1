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

Write-Host "=== 1. Compiling Imp2Net.dll (x86) ==="
$imp2NetDll = Join-Path $objDir "Imp2Net.dll"
& $csc /nologo /platform:x86 /target:library /optimize+ `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    /out:$imp2NetDll (Join-Path $srcDir "Imp2Net.cs") (Join-Path $srcDir "WorldSync.cs")
if ($LASTEXITCODE -ne 0) { throw "csc.exe failed with exit code $LASTEXITCODE" }

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

# Copy dplayx.dll to repo root and working game folder
$rootDplayx = Join-Path $repoRoot "dplayx.dll"
Copy-Item -LiteralPath $builtDplayx -Destination $rootDplayx -Force
Write-Host "Built: $rootDplayx ($((Get-Item $rootDplayx).Length) bytes)"

$iniContent = @"
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
if (-not (Test-Path $rootIni)) {
    [System.IO.File]::WriteAllText($rootIni, $iniContent, [System.Text.Encoding]::ASCII)
}

if (Test-Path -LiteralPath $gameDir) {
    Write-Host "=== 3. Patching & Deploying to Working Game Folder ==="
    $gameExe = Join-Path $gameDir "Imperialism II.exe"
    & (Join-Path $srcDir "PatchExe.ps1") -InputExe $gameExe -OutputExe $gameExe
    $rootExe = Join-Path $repoRoot "ImpII.exe"
    if (-not (Test-Path -LiteralPath $rootExe) -or
        (Get-FileHash -LiteralPath $gameExe).Hash -ne (Get-FileHash -LiteralPath $rootExe).Hash) {
        Copy-Item -LiteralPath $gameExe -Destination $rootExe -Force
    }
    Copy-Item -LiteralPath $builtDplayx -Destination (Join-Path $gameDir "dplayx.dll") -Force
    $gameIni = Join-Path $gameDir "dplayx.ini"
    if (-not (Test-Path $gameIni)) {
        [System.IO.File]::WriteAllText($gameIni, $iniContent, [System.Text.Encoding]::ASCII)
    }
    Write-Host "Deployed patched Imperialism II.exe, dplayx.dll, and dplayx.ini to: $gameDir"
}

# Keep the desktop launchers in the test VMs on the same build. Diagnostic logging
# is enabled only in this test bundle; preserve the user's normal configuration.
$vmDeployDir = Join-Path $objDir "vm-deploy"
New-Item -ItemType Directory -Force -Path $vmDeployDir | Out-Null
$vmSourceExe = Join-Path $repoRoot "ImpII.exe"
if (Test-Path -LiteralPath $vmSourceExe) {
    Copy-Item -LiteralPath $vmSourceExe -Destination (Join-Path $vmDeployDir "Imperialism II.exe") -Force
    Copy-Item -LiteralPath $builtDplayx -Destination (Join-Path $vmDeployDir "dplayx.dll") -Force
    $vmIniContent = [System.IO.File]::ReadAllText($rootIni) -replace '(?m)^Log=0\r?$', 'Log=1'
    [System.IO.File]::WriteAllText((Join-Path $vmDeployDir "dplayx.ini"), $vmIniContent, [System.Text.Encoding]::ASCII)
    Write-Host "Prepared VM launch bundle: $vmDeployDir"
}

Write-Host "=== Build Complete ==="
