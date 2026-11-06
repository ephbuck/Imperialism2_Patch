param(
    [Parameter(Mandatory = $true)]
    [string]$InputExe,
    [Parameter(Mandatory = $true)]
    [string]$OutputExe
)

$ErrorActionPreference = "Stop"

$bytes = [System.IO.File]::ReadAllBytes($InputExe)
if ($bytes.Length -ne 4024320) {
    throw "Unexpected Imperialism II.exe size: $($bytes.Length) bytes (expected 4,024,320 bytes for v1.03 GOG)."
}

function WriteBytes([int]$offset, [byte[]]$patch) {
    [Array]::Copy($patch, 0, $bytes, $offset, $patch.Length)
}

function AssertBytesOrPatched([string]$name, [int]$offset, [byte[]]$orig, [byte[]]$patched) {
    $matchOrig = $true
    $matchPatched = $true
    for ($i = 0; $i -lt $orig.Length; $i++) {
        if ($bytes[$offset + $i] -ne $orig[$i]) { $matchOrig = $false }
        if ($bytes[$offset + $i] -ne $patched[$i]) { $matchPatched = $false }
    }
    if (-not $matchOrig -and -not $matchPatched) {
        $actual = ($bytes[$offset..($offset + $orig.Length - 1)] | ForEach-Object { $_.ToString("X2") }) -join " "
        throw "Byte verification failed at '$name' (0x$($offset.ToString('X6'))): found [$actual]."
    }
    WriteBytes $offset $patched
}

# 1. _winCdPlay assertion dialog bypass (win_cd.cpp @407)
AssertBytesOrPatched "_winCdPlay Assert" 0x27B16C `
    ([byte[]](0xE8, 0x40, 0xAB, 0xD8, 0xFF)) `
    ([byte[]](0x90, 0x90, 0x90, 0x90, 0x90))

# 2. Expand .text VirtualSize within RawSize 0x2E7A00 for COM and world-sync thunks.
$peOff = [BitConverter]::ToInt32($bytes, 0x3C)
$optSize = [BitConverter]::ToInt16($bytes, $peOff + 20)
$textVSizeOff = $peOff + 24 + $optSize + 8
$curVSize = [BitConverter]::ToInt32($bytes, $textVSizeOff)
if ($curVSize -lt 0x002E79C0) {
    WriteBytes $textVSizeOff ([BitConverter]::GetBytes([int]0x002E79C0))
}

# 3. Write .rdata thunk pointer & strings at fOff 0x342500 (VA 0x00743700)
#    [0x00743700] = dd 0x006E8860 (VA of DPlayCoCreateThunk)
#    [0x00743704] = "DPLAYX.dll\0"
#    [0x00743710] = "Imp2CoCreateInstance\0"
WriteBytes 0x342500 ([BitConverter]::GetBytes([int]0x006E8860))
$dllStr = [System.Text.Encoding]::ASCII.GetBytes("DPLAYX.dll" + [char]0)
WriteBytes 0x342504 $dllStr
$fnStr  = [System.Text.Encoding]::ASCII.GetBytes("Imp2CoCreateInstance" + [char]0)
WriteBytes 0x342510 $fnStr

# 4. Write DPlayCoCreateThunk at fOff 0x2E7C60 (VA 0x006E8860)
#    Checks DPLAYX.dll!Imp2CoCreateInstance first; falls back to ole32.dll!CoCreateInstance if absent.
$thunk = [byte[]](
    0x68, 0x04, 0x37, 0x74, 0x00,             # push 0x00743704 ("DPLAYX.dll")
    0xFF, 0x15, 0x48, 0xC0, 0x76, 0x00,       # call dword ptr [KERNEL32!GetModuleHandleA]
    0x85, 0xC0,                               # test eax, eax
    0x74, 0x12,                               # jz fallback
    0x68, 0x10, 0x37, 0x74, 0x00,             # push 0x00743710 ("Imp2CoCreateInstance")
    0x50,                                     # push eax
    0xFF, 0x15, 0x08, 0xC2, 0x76, 0x00,       # call dword ptr [KERNEL32!GetProcAddress]
    0x85, 0xC0,                               # test eax, eax
    0x74, 0x02,                               # jz fallback
    0xFF, 0xE0,                               # jmp eax
    0xFF, 0x25, 0xAC, 0xC7, 0x76, 0x00        # fallback: jmp dword ptr [ole32!CoCreateInstance]
)
WriteBytes 0x2E7C60 $thunk

# 5. Redirect all 5 CDirectPlay CoCreateInstance call sites to [0x00743700]
$origIat = [byte[]](0xAC, 0xC7, 0x76, 0x00)
$newPtr  = [byte[]](0x00, 0x37, 0x74, 0x00)
foreach ($off in @(0x08F0C8, 0x08F26B, 0x08F35C, 0x08F683, 0x0904A9)) {
    AssertBytesOrPatched "CDirectPlay CoCreateInstance @0x$($off.ToString('X'))" $off $origIat $newPtr
}

# 6. Unlock TTCPIPDialog 'addr' input field max length from 15 (0x0F) to 127 (0x7F) chars
AssertBytesOrPatched "TTCPIPDialog addr maxChars" 0x1961B3 `
    ([byte[]](0x66, 0xC7, 0x86, 0xA0, 0x00, 0x00, 0x00, 0x0F, 0x00)) `
    ([byte[]](0x66, 0xC7, 0x86, 0xA0, 0x00, 0x00, 0x00, 0x7F, 0x00))

# 7. Bypass StripIPAddressString in AskIPAddress so hostnames, :port, and @interface are preserved
AssertBytesOrPatched "AskIPAddress StripIPAddressString" 0x2781DD `
    ([byte[]](0xE8, 0xFC, 0xE4, 0xD8, 0xFF)) `
    ([byte[]](0x90, 0x90, 0x90, 0x90, 0x90))

# 8. Increase default Join EnumSessions timeout from 1000ms to 5000ms
AssertBytesOrPatched "CDirectPlay::Join EnumSessions timeout" 0x08F831 `
    ([byte[]](0x05, 0xE8, 0x03, 0x00, 0x00)) `
    ([byte[]](0x05, 0x88, 0x13, 0x00, 0x00))

# 9. Suppress WNetMgr.cpp @927 unhandled DPSYS_* assertion dialog
AssertBytesOrPatched "WNetMgr.cpp @927 Assert" 0x27D463 `
    ([byte[]](0xE8, 0xBA, 0x71, 0xD8, 0xFF)) `
    ([byte[]](0x90, 0x90, 0x90, 0x90, 0x90))

# 10. World synchronization. These addresses are verified against the GOG 1.03
# release, not Imperialism II.map (which describes an older executable).
AssertBytesOrPatched "Native world serializer" 0x2793D0 `
    ([byte[]](0x64,0xA1,0,0,0,0,0x6A,0xFF)) `
    ([byte[]](0x64,0xA1,0,0,0,0,0x6A,0xFF))
AssertBytesOrPatched "Native world loader" 0x2794E0 `
    ([byte[]](0x8B,0x54,0x24,4,0x8B,0x0D,0x8C,0x98,0x75,0)) `
    ([byte[]](0x8B,0x54,0x24,4,0x8B,0x0D,0x8C,0x98,0x75,0))
AssertBytesOrPatched "Native client country writer" 0x103A20 `
    ([byte[]](0x51,0x53,0x55,0x56,0x8B,0x74,0x24,0x14,0x57,0x8B,0xF9)) `
    ([byte[]](0x51,0x53,0x55,0x56,0x8B,0x74,0x24,0x14,0x57,0x8B,0xF9))
function RelativeBranch([byte]$opcode, [int]$fromVA, [int]$toVA) {
    return [byte[]](@($opcode) + @([BitConverter]::GetBytes([int]($toVA - $fromVA - 5))))
}

# Resolve exports on each invocation, preserving cdecl arguments / native fallback.
# Existing CoCreate strings end at 0x342524; reserve distinct names after them.
$loadName = [Text.Encoding]::ASCII.GetBytes("Imp2LoadWorld" + [char]0)
$startName = [Text.Encoding]::ASCII.GetBytes("Imp2CanStart" + [char]0)
AssertBytesOrPatched "World loader export name" 0x342530 ([byte[]]::new($loadName.Length)) $loadName
AssertBytesOrPatched "World start-check export name" 0x342540 ([byte[]]::new($startName.Length)) $startName
$resolvePrefix = [byte[]](
    0x68,0x04,0x37,0x74,0x00, 0xFF,0x15,0x48,0xC0,0x76,0x00,
    0x85,0xC0,0x74,0x12, 0x68,0x30,0x37,0x74,0x00, 0x50,
    0xFF,0x15,0x08,0xC2,0x76,0x00, 0x85,0xC0,0x74,0x02, 0xFF,0xE0
)
$loadThunk = [byte[]](@($resolvePrefix) + @(RelativeBranch 0xE9 0x006E88B1 0x005C19C0))
AssertBytesOrPatched "World loader export thunk" 0x2E7C90 ([byte[]]::new($loadThunk.Length)) $loadThunk
# Result 2 means the snapshot was ingested for a player still selecting a country.
# Use the existing lounge refresh, retaining its native pointer and phase.
$loadResult = [byte[]](@(0x3C,2,0x0F,0x84) + @([BitConverter]::GetBytes([int](0x58C57B-0x6E8948))) +
    @(0x84,0xC0,0x0F,0x85) + @([BitConverter]::GetBytes([int](0x58C6CB-0x6E8950))) +
    @(RelativeBranch 0xE9 0x6E8950 0x58C5BD))
AssertBytesOrPatched "World loader result thunk" 0x2E7D40 ([byte[]]::new($loadResult.Length)) $loadResult
$loadResultSite = [byte[]](@(RelativeBranch 0xE9 0x58C5B5 0x6E8940) + @(0x90,0x90,0x90))
AssertBytesOrPatched "World loader lounge continuation" 0x18B9B5 `
    ([byte[]](0x84,0xC0,0x0F,0x85,0x0E,1,0,0)) $loadResultSite
# Generated/scenario worlds first use native staging initialization. Apply the
# host's map after that setup and before the common lounge refresh.
$mapApply = [byte[]](@(0x50,0x52,0x51,0x6A,0,0xA1,0xE8,0x06,0x76,0,0xFF,0x70,0x60) +
    @(RelativeBranch 0xE8 0x6E896D 0x6E8890) + @(0x83,0xC4,8,0x84,0xC0,0x59,0x5A,0x58,0x0F,0x84) +
    @([BitConverter]::GetBytes([int](0x58C5BD-0x6E8980))) + @(0x8B,0x4B,0x40,0x33,0xF6) +
    @(RelativeBranch 0xE9 0x6E8985 0x58C580))
AssertBytesOrPatched "Staging map apply thunk" 0x2E7D60 ([byte[]]::new($mapApply.Length)) $mapApply
AssertBytesOrPatched "Staging map apply site" 0x18B97B `
    ([byte[]](0x8B,0x4B,0x40,0x33,0xF6)) (RelativeBranch 0xE9 0x58C57B 0x6E8960)
# ReadFrom reserves former human seats with DPID -2. A lobby claim must be
# able to take that disconnected seat, while positive live owners still pass
# through the original requester-ID comparison. The game thread serializes claims.
$reclaimSeat = [byte[]](@(0x83,0xF8,0xFE,0x0F,0x84) +
    @([BitConverter]::GetBytes([int](0x58B979-0x6E8999))) + @(0x85,0xC0,0x0F,0x84) +
    @([BitConverter]::GetBytes([int](0x58B979-0x6E89A1))) +
    @(RelativeBranch 0xE9 0x6E89A1 0x58B804))
AssertBytesOrPatched "Reserved country reclaim thunk" 0x2E7D90 ([byte[]]::new($reclaimSeat.Length)) $reclaimSeat
AssertBytesOrPatched "Reserved country reclaim site" 0x18ABFC `
    ([byte[]](0x85,0xC0,0x0F,0x84,0x75,1,0,0)) `
    ([byte[]](@(RelativeBranch 0xE9 0x58B7FC 0x6E8990) + @(0x90,0x90,0x90)))
$canStartThunk = [byte[]](@($resolvePrefix) + @(0xB8,1,0,0,0,0xC3))
$canStartThunk[16] = 0x40 # export name at VA 0x00743740
AssertBytesOrPatched "World start-check export thunk" 0x2E7CC0 ([byte[]]::new($canStartThunk.Length)) $canStartThunk

# StartGame's first ten bytes have no relative operands. Preserve ECX (this).
$startGuard = [byte[]](@(0x51) + @(RelativeBranch 0xE8 0x006E8901 0x006E88C0) +
    @(0x59,0x85,0xC0,0x74,0x0F, 0x83,0xEC,0x1C,0xC7,0x41,0x40,0,0,0,0) +
    @(RelativeBranch 0xE9 0x006E8915 0x0058B14A) + @(0x33,0xC0,0xC3))
AssertBytesOrPatched "World start barrier" 0x2E7D00 ([byte[]]::new($startGuard.Length)) $startGuard
AssertBytesOrPatched "Initial options load call" 0x18B9AD `
    (RelativeBranch 0xE8 0x0058C5AD 0x00408E0E) `
    (RelativeBranch 0xE8 0x0058C5AD 0x006E8890)
AssertBytesOrPatched "StartGame entry" 0x18A540 `
    ([byte[]](0x83,0xEC,0x1C,0xC7,0x41,0x40,0,0,0,0)) `
    ([byte[]](@(RelativeBranch 0xE9 0x0058B140 0x006E8900) + @(0x90,0x90,0x90,0x90,0x90)))

[System.IO.File]::WriteAllBytes($OutputExe, $bytes)
$hash = (Get-FileHash -LiteralPath $OutputExe -Algorithm SHA256).Hash
Write-Host "Patched EXE written to: $OutputExe (SHA-256: $hash)"
