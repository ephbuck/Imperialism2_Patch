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

# 2. Expand .text VirtualSize slightly (0x2E784F -> 0x2E7A00, within RawSize 0x2E7A00) for thunks
$peOff = [BitConverter]::ToInt32($bytes, 0x3C)
$optSize = [BitConverter]::ToInt16($bytes, $peOff + 20)
$textVSizeOff = $peOff + 24 + $optSize + 8
$curVSize = [BitConverter]::ToInt32($bytes, $textVSizeOff)
if ($curVSize -lt 0x002E7A00) {
    WriteBytes $textVSizeOff ([BitConverter]::GetBytes([int]0x002E7A00))
}

# 3. Write .rdata thunk pointer & strings at fOff 0x342500 (VA 0x00743700)
#    [0x00743700] = dd 0x006E8860 (VA of DPlayCoCreateThunk)
#    [0x00743704] = "DPLAYX.dll\0"
#    [0x00743710] = "Imp2CoCreateInstance\0"
#    [0x00743750] = "Imp2Initialize\0"
WriteBytes 0x342500 ([BitConverter]::GetBytes([int]0x006E8860))
$dllStr = [System.Text.Encoding]::ASCII.GetBytes("DPLAYX.dll" + [char]0)
WriteBytes 0x342504 $dllStr
$fnStr  = [System.Text.Encoding]::ASCII.GetBytes("Imp2CoCreateInstance" + [char]0)
WriteBytes 0x342510 $fnStr
$initFnStr = [System.Text.Encoding]::ASCII.GetBytes("Imp2Initialize" + [char]0)
WriteBytes 0x342550 $initFnStr

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

# 10. Write Imp2InitThunk at fOff 0x2E7DB0 (VA 0x006E89B0)
#     Calls DPLAYX.dll!Imp2Initialize on application startup, with fallback to ole32.dll!CoInitialize.
$initThunk = [byte[]](
    0x68, 0x04, 0x37, 0x74, 0x00,             # push 0x00743704 ("DPLAYX.dll")
    0xFF, 0x15, 0x48, 0xC0, 0x76, 0x00,       # call dword ptr [KERNEL32!GetModuleHandleA]
    0x85, 0xC0,                               # test eax, eax
    0x74, 0x12,                               # jz fallback
    0x68, 0x50, 0x37, 0x74, 0x00,             # push 0x00743750 ("Imp2Initialize")
    0x50,                                     # push eax (hModule)
    0xFF, 0x15, 0x08, 0xC2, 0x76, 0x00,       # call dword ptr [KERNEL32!GetProcAddress]
    0x85, 0xC0,                               # test eax, eax
    0x74, 0x02,                               # jz fallback
    0xFF, 0xE0,                               # jmp eax
    0xFF, 0x25, 0xB4, 0xC7, 0x76, 0x00        # fallback: jmp dword ptr [ole32!CoInitialize]
)
WriteBytes 0x2E7DB0 $initThunk

# 11. Redirect CoInitialize startup call at fOff 0x89D52 (VA 0x0048A952) to Imp2InitThunk
#     Original: FF 15 B4 C7 76 00  (call dword ptr [ole32!CoInitialize])
#     Patched:  E8 59 E0 25 00 90  (call 0x006E89B0; nop)
AssertBytesOrPatched "Startup CoInitialize -> Imp2Initialize" 0x89D52 `
    ([byte[]](0xFF, 0x15, 0xB4, 0xC7, 0x76, 0x00)) `
    ([byte[]](0xE8, 0x59, 0xE0, 0x25, 0x00, 0x90))

# 12. Patch CIncludeView::McEnable at fOff 0x962B0 (VA 0x00496EB0)
#     Keeps CIncludeView mouse input enabled (m_bMcEnabled = 1) across modal dialog lifecycles
AssertBytesOrPatched "CIncludeView::McEnable" 0x962B0 `
    ([byte[]](0x8B, 0x54, 0x24, 0x04, 0x8B, 0x41, 0x6C, 0x81, 0xE2, 0xFF, 0x00, 0x00, 0x00, 0x89, 0x51, 0x6C, 0xC2, 0x04, 0x00, 0x90)) `
    ([byte[]](0xC7, 0x41, 0x6C, 0x01, 0x00, 0x00, 0x00, 0xB8, 0x01, 0x00, 0x00, 0x00, 0xC2, 0x04, 0x00, 0x90, 0x90, 0x90, 0x90, 0x90))

# 13. Expand TDisplayMgr::UpdateTheGWorld offscreen buffer allocation at fOff 0x1322AC (VA 0x00532EAC)
#     Expands tactical offscreen buffer from 864x728 to 2464x2304 (Right=2400, Bottom=2240) to support up to 25% zoom-out (Option B)
AssertBytesOrPatched "UpdateTheGWorld Buffer Expansion" 0x1322AC `
    ([byte[]](0xA1, 0x88, 0xBC, 0x75, 0x00, 0xBA, 0xC0, 0xFF, 0xFF, 0xFF, 0x89, 0x44, 0x24, 0x08, 0xA1, 0x8C, 0xBC, 0x75, 0x00, 0x83, 0xC0, 0x40, 0x89, 0x54, 0x24, 0x00, 0x89, 0x44, 0x24, 0x0C)) `
    ([byte[]](0xBA, 0xC0, 0xFF, 0xFF, 0xFF, 0x89, 0x54, 0x24, 0x00, 0x89, 0x54, 0x24, 0x04, 0xC7, 0x44, 0x24, 0x08, 0x60, 0x09, 0x00, 0x00, 0xC7, 0x44, 0x24, 0x0C, 0xC0, 0x08, 0x00, 0x00, 0x90))

[System.IO.File]::WriteAllBytes($OutputExe, $bytes)
$hash = (Get-FileHash -LiteralPath $OutputExe -Algorithm SHA256).Hash
Write-Host "Patched EXE written to: $OutputExe (SHA-256: $hash)"
