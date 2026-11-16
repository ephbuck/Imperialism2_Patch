param(
    [Parameter(Mandatory=$true)][string]$InputExe,
    [Parameter(Mandatory=$true)][string]$OutputExe
)
$ErrorActionPreference = 'Stop'
$bytes = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $InputExe).Path)
if ($bytes.Length -ne 4024320) { throw 'Expected GOG 1.03 executable.' }
$inputHash=(Get-FileHash -LiteralPath $InputExe -Algorithm SHA256).Hash
if($inputHash -notin @('998384BB696F02D37A6725CEA33F98BC2CA711B803C2638BED07A91D687CA7CC','BE5DA3D719702B2EF1450F17D038D4DB378FA40CD580E44D90E611C8B88E4FAE')) {
    throw 'Unsupported executable digest. Revalidate the graphics profile before patching another build.'
}
function Patch([int]$offset, [byte[]]$original, [byte[]]$value) {
    $sameOriginal = $true; $samePatched = $true
    for ($i=0; $i -lt $value.Length; $i++) {
        if ($bytes[$offset+$i] -ne $original[$i]) { $sameOriginal=$false }
        if ($bytes[$offset+$i] -ne $value[$i]) { $samePatched=$false }
    }
    if (-not ($sameOriginal -or $samePatched)) { throw ('Unexpected windowed-map patch bytes at 0x{0:X}' -f $offset) }
    [Array]::Copy($value,0,$bytes,$offset,$value.Length)
}
$name = [Text.Encoding]::ASCII.GetBytes("Imp2Initialize"+[char]0)
Patch 0x342550 ([byte[]]::new($name.Length)) $name
$thunk = [byte[]](
    0x68,4,0x37,0x74,0,0xFF,0x15,0x48,0xC0,0x76,0,0x85,0xC0,0x74,0x12,
    0x68,0x50,0x37,0x74,0,0x50,0xFF,0x15,8,0xC2,0x76,0,0x85,0xC0,0x74,2,0xFF,0xE0,
    0xFF,0x25,0xB4,0xC7,0x76,0)
Patch 0x2E7DB0 ([byte[]]::new($thunk.Length)) $thunk
Patch 0x89D52 ([byte[]](0xFF,0x15,0xB4,0xC7,0x76,0)) ([byte[]](0xE8,0x59,0xE0,0x25,0,0x90))
# The new startup thunk ends at RVA 0x2E89D7, inside existing raw padding.
$pe=[BitConverter]::ToInt32($bytes,0x3C)
$sectionHeader=$pe+24+[BitConverter]::ToInt16($bytes,$pe+20)
$old=[BitConverter]::ToInt32($bytes,$sectionHeader+8)
if ($old -lt 0x2E79E0) { [Array]::Copy([BitConverter]::GetBytes([int]0x2E79E0),0,$bytes,$sectionHeader+8,4) }
[IO.File]::WriteAllBytes($OutputExe,$bytes)
Write-Output "Windowed map executable: $((Get-FileHash -LiteralPath $OutputExe).Hash)"
