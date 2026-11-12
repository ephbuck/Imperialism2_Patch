# PackMapTiles.ps1
# Repacks extracted BMP tiles into Imperialism II MapTiles.rsrc format.
param(
    [string]$TileDir = "obj\all_tiles",
    [string]$OriginalRsrc = "Imperialism II - Patched\Data\MapTiles.rsrc",
    [string]$OutputRsrc = "Imperialism II - Patched\Data\MapTiles.rsrc"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $OriginalRsrc)) {
    Write-Error "Original rsrc not found: $OriginalRsrc"
}
if (-not (Test-Path $TileDir)) {
    Write-Error "Tile directory not found: $TileDir"
}

$origBytes = [System.IO.File]::ReadAllBytes($OriginalRsrc)
$sig = [System.Text.Encoding]::ASCII.GetString($origBytes, 0, 4)
if ($sig -ne "rsrc") {
    Write-Error "Invalid rsrc signature: $sig"
}

$version = [BitConverter]::ToInt32($origBytes, 4)
$tableOffset = [BitConverter]::ToInt32($origBytes, 8)
$count = [BitConverter]::ToInt32($origBytes, 12)

Write-Host "Packing $count tiles from '$TileDir' into '$OutputRsrc'..."

$entries = @()
$currentDataOffset = 0x10 + $count * 16

for ($i = 0; $i -lt $count; $i++) {
    $entryOff = $tableOffset + $i * 16
    $tag = [System.Text.Encoding]::ASCII.GetString($origBytes, $entryOff, 4)
    $id = [BitConverter]::ToInt32($origBytes, $entryOff + 4)
    
    $bmpPath = Join-Path $TileDir "tile_${id}.bmp"
    if (-not (Test-Path $bmpPath)) {
        Write-Error "Missing tile file: $bmpPath"
    }

    $bmpBytes = [System.IO.File]::ReadAllBytes($bmpPath)
    $dataLen = $bmpBytes.Length - 14

    $entries += [PSCustomObject]@{
        Tag = $tag
        ID = $id
        Offset = $currentDataOffset
        Length = $dataLen
        BmpBytes = $bmpBytes
    }
    $currentDataOffset += $dataLen
}

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)

# Header
$bw.Write([System.Text.Encoding]::ASCII.GetBytes("rsrc"))
$bw.Write([int32]$version)
$bw.Write([int32]$tableOffset)
$bw.Write([int32]$count)

# Table entries
foreach ($e in $entries) {
    $bw.Write([System.Text.Encoding]::ASCII.GetBytes($e.Tag))
    $bw.Write([int32]$e.ID)
    $bw.Write([int32]$e.Offset)
    $bw.Write([int32]$e.Length)
}

# Tile data payloads
foreach ($e in $entries) {
    $bw.Write($e.BmpBytes, 14, $e.Length)
}

$repacked = $ms.ToArray()
$bw.Close()
$ms.Dispose()

# Backup original if overwriting in place and backup doesn't exist
$bak = "$OutputRsrc.orig"
if (-not (Test-Path $bak) -and (Test-Path $OutputRsrc)) {
    Copy-Item $OutputRsrc $bak
    Write-Host "Created backup: $bak"
}

[System.IO.File]::WriteAllBytes($OutputRsrc, $repacked)
Write-Host "Successfully packed $($entries.Count) tiles into '$OutputRsrc' ($($repacked.Length) bytes)."
