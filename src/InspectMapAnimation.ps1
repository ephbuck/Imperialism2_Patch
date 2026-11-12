param(
    [string]$Exe = (Join-Path (Split-Path -Parent $PSScriptRoot) 'ImpII.exe')
)

$ErrorActionPreference = 'Stop'
$path = (Resolve-Path -LiteralPath $Exe).Path
$bytes = [IO.File]::ReadAllBytes($path)
$pe = [BitConverter]::ToInt32($bytes, 0x3C)
if ([BitConverter]::ToInt32($bytes, $pe) -ne 0x4550 -or
    [BitConverter]::ToUInt16($bytes, $pe + 4) -ne 0x14C) { throw 'Expected an x86 PE executable.' }
$imageBase = [BitConverter]::ToUInt32($bytes, $pe + 52)
if ($imageBase -ne 0x400000) { throw 'Unsupported image base; these addresses are for retail 1.03.' }
$sectionCount = [BitConverter]::ToUInt16($bytes, $pe + 6)
$sectionTable = $pe + 24 + [BitConverter]::ToUInt16($bytes, $pe + 20)

function FileOffset([int]$va) {
    $rva = $va - $imageBase
    for ($i = 0; $i -lt $sectionCount; $i++) {
        $s = $sectionTable + 40 * $i
        $start = [BitConverter]::ToUInt32($bytes, $s + 12)
        $size = [BitConverter]::ToUInt32($bytes, $s + 16)
        if ($rva -ge $start -and $rva -lt ($start + $size)) {
            return [int]($rva - $start + [BitConverter]::ToUInt32($bytes, $s + 20))
        }
    }
    throw ('VA 0x{0:X8} is not backed by file data.' -f $va)
}
function RelativeTarget([int]$va, [byte]$opcode) {
    $offset = FileOffset $va
    if ($bytes[$offset] -ne $opcode) { throw ('Unexpected opcode at 0x{0:X8}.' -f $va) }
    return $va + 5 + [BitConverter]::ToInt32($bytes, $offset + 1)
}

Write-Output ('Executable: ' + $path)
Write-Output ('SHA256: ' + (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash)
$rows = @(
    @('TAnimation',           0x6F6D90, 0x4041F6, 0x4B7DE0),
    @('TCivAnimation',        0x6F6DE8, 0x408E40, 0x4B8250),
    @('TOneTimeAnimation',    0x6F6E30, 0x4063E8, 0x4B8460),
    @('TFocusAnimation',      0x6F6E78, 0x402608, 0x4B8690),
    @('TTransFocusAnimation', 0x6F6EC8, 0x402608, 0x4B8690),
    @('TScriptedAnimation',   0x6F6F20, 0x408922, 0x4B9090)
)
$report = foreach ($row in $rows) {
    $slot = $row[1] + 0x28
    $thunk = [BitConverter]::ToInt32($bytes, (FileOffset $slot))
    $target = RelativeTarget $thunk 0xE9
    if ($thunk -ne $row[2] -or $target -ne $row[3]) { throw ('Tick signature mismatch: ' + $row[0]) }
    [pscustomobject]@{ Class=$row[0]; VTable=('0x{0:X8}' -f $row[1]); TickSlot=('0x{0:X8}' -f $slot);
        Thunk=('0x{0:X8}' -f $thunk); Implementation=('0x{0:X8}' -f $target) }
}
$report | Format-Table -AutoSize | Out-String | Write-Output
foreach ($call in @(0x4B7DFC, 0x4B8285, 0x4B84A0)) {
    if ((RelativeTarget $call 0xE8) -ne 0x408A49) { throw 'Animation Invalidate call signature mismatch.' }
}
if ((RelativeTarget 0x408A49 0xE9) -ne 0x49FBF0) { throw 'TView::Invalidate thunk mismatch.' }
if ((RelativeTarget 0x404E8F 0xE9) -ne 0x4AAA40) { throw 'CopyBits thunk mismatch.' }
if ((RelativeTarget 0x55AB36 0xE8) -ne 0x40104B -or (RelativeTarget 0x40104B 0xE9) -ne 0x4B9EC0) { throw 'Map animation-retention call signature mismatch.' }
foreach ($slot in @(0x703AC4, 0x703D54, 0x703FE4)) {
    if ([BitConverter]::ToInt32($bytes, (FileOffset $slot)) -ne 0x4078C9) { throw 'Map ScreenToTile slot signature mismatch.' }
}
if ((RelativeTarget 0x4078C9 0xE9) -ne 0x55A5A0) { throw 'Map ScreenToTile thunk mismatch.' }
foreach ($call in @(0x559BC2, 0x559CA5, 0x55B35E, 0x55B442, 0x55D889)) {
    if ((RelativeTarget $call 0xE8) -ne 0x404E8F) { throw 'Map CopyBits call signature mismatch.' }
}
$cacheWidth = [BitConverter]::ToInt32($bytes, (FileOffset 0x559932))
if ($cacheWidth -ne 7040) { throw 'Map terrain-cache width signature mismatch.' }
Write-Output 'Verified: Tick -> Invalidate, map input/animation-retention paths, map CopyBits sites, and 7040px (110 x 64) terrain-cache allocation.'
Write-Output 'The shipped Imperialism II.map is useful for names, but its linked addresses differ from this executable.'
