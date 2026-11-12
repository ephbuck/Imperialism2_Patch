param(
    [string]$Exe = (Join-Path (Split-Path -Parent $PSScriptRoot) 'ImpII.exe'),
    [switch]$AsJson
)

# Offline inspection only: never loads or patches the executable.
$ErrorActionPreference = 'Stop'
$resolved = (Resolve-Path -LiteralPath $Exe).Path
$bytes = [IO.File]::ReadAllBytes($resolved)
$pe = [BitConverter]::ToInt32($bytes, 0x3c)
if ([BitConverter]::ToUInt32($bytes, $pe) -ne 0x4550 -or [BitConverter]::ToUInt16($bytes, $pe + 4) -ne 0x14c) { throw 'Expected x86 PE.' }
$imageBase = [BitConverter]::ToUInt32($bytes, $pe + 52)
if ($imageBase -ne 0x400000) { throw 'Unsupported image base.' }
$sectionCount = [BitConverter]::ToUInt16($bytes, $pe + 6)
$table = $pe + 24 + [BitConverter]::ToUInt16($bytes, $pe + 20)
function Offset([int]$address) {
    $rva = $address - $imageBase
    for ($index = 0; $index -lt $sectionCount; $index++) {
        $section = $table + 40 * $index
        $start = [BitConverter]::ToUInt32($bytes, $section + 12)
        $size = [BitConverter]::ToUInt32($bytes, $section + 16)
        if ($rva -ge $start -and $rva -lt $start + $size) {
            return [int]($rva - $start + [BitConverter]::ToUInt32($bytes, $section + 20))
        }
    }
    throw ('Address not file-backed: 0x{0:X8}' -f $address)
}
function ReadInt([int]$address) { [BitConverter]::ToInt32($bytes, (Offset $address)) }
function Target([int]$address, [byte]$opcode) {
    if ($bytes[(Offset $address)] -ne $opcode) { throw ('Unexpected instruction at 0x{0:X8}' -f $address) }
    $address + 5 + (ReadInt ($address + 1))
}
function Expect([int]$address, [byte[]]$expected, [string]$description) {
    $at = Offset $address
    for ($index = 0; $index -lt $expected.Length; $index++) {
        if ($bytes[$at + $index] -ne $expected[$index]) { throw ('Unsupported {0} signature at 0x{1:X8}' -f $description, $address) }
    }
    [pscustomobject]@{address=('0x{0:X8}' -f $address); bytes=[BitConverter]::ToString($expected); observation=$description}
}
$anchors = @(
    Expect 0x55A5A9 ([byte[]](0x8B,0x47,0x04,0x03,0x46,0x68)) 'Tile lookup adds camera Y to point Y'
    Expect 0x55A5D2 ([byte[]](0x8D,0x44,0x11,0x20)) 'Odd row adds 32 before column conversion'
    Expect 0x55A5E9 ([byte[]](0x48)) 'Odd row decrements converted column'
    Expect 0x55A608 ([byte[]](0x66,0x89,0x01)) 'Tile lookup writes a 16-bit column'
    Expect 0x55D140 ([byte[]](0x51,0x8D,0x44,0x24,0x44,0x52,0x50,0xFF,0x15,0x44,0xC5,0x76,0x00)) 'Draw offsets its rectangle by the 64px buffer margin via OffsetRect'
    Expect 0x6816BF ([byte[]](0x80,0xCC,0x0C)) 'Float conversion selects x87 truncation, not floor'
    Expect 0x550347 ([byte[]](0x66,0x3D,0x6C,0x00)) 'Column normalization compares against 108'
    Expect 0x55036F ([byte[]](0x66,0x3D,0x3C,0x00)) 'Row normalization compares against 60'
    Expect 0x5594E0 ([byte[]](0xC7,0x05,0x28,0xC0,0x75,0x00,0,0,0,0,0xC7,0x05,0x2C,0xC0,0x75,0x00,0,0,0x90,0x3F)) 'Initializer sets row multiplier to IEEE754 double 1/64'
    Expect 0x559510 ([byte[]](0xC7,0x05,0x00,0xC0,0x75,0x00,0,0,0,0,0xC7,0x05,0x04,0xC0,0x75,0x00,0,0,0x90,0x3F)) 'Initializer sets column multiplier to IEEE754 double 1/64'
    Expect 0x423BD4 ([byte[]](0x8B,0x51,0x28,0x56,0x8B,0x70,0x04,0x2B,0xF2)) 'View point conversion subtracts the view origin from Y'
)
$methods = foreach ($vtable in @(0x703900,0x703B90,0x703E20)) {
    foreach ($slot in @(69,75,77,110,113,125)) {
        $pointer = ReadInt ($vtable + 4 * $slot)
        $implementation = if ($bytes[(Offset $pointer)] -eq 0xe9) { Target $pointer 0xe9 } else { $pointer }
        [pscustomobject]@{vtable=('0x{0:X8}' -f $vtable); slot=$slot; thunk=('0x{0:X8}' -f $pointer); implementation=('0x{0:X8}' -f $implementation)}
    }
}
$report = [ordered]@{
    path=$resolved
    sha256=(Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash
    evidence_kind='Direct PE bytes and verified x86 instruction anchors; not REA decompilation or execution'
    anchors=$anchors
    methods=@($methods)
    tile_projection=('0x{0:X8}' -f (Target 0x4050DD 0xe9))
    tile_normalization=('0x{0:X8}' -f (Target 0x408279 0xe9))
    row_multiplier_cell='0x0075C028; static initializer 0x005594E0 stores double 1/64'
    column_multiplier_cell='0x0075C000; static initializer 0x00559510 stores double 1/64'
    unknowns=@('Indirect callers require contextual verification', 'Static inspection does not prove live hook coverage or rendered pixels')
}
if ($AsJson) { $report | ConvertTo-Json -Depth 8 } else {
    'Executable: ' + $report.path
    'SHA256: ' + $report.sha256
    $methods | Format-Table -AutoSize
    $anchors | Format-Table -AutoSize
    'Projection: {0}; normalization: {1}' -f $report.tile_projection,$report.tile_normalization
}
