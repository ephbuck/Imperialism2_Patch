param(
    [Parameter(Mandatory=$true)][string]$GameDirectory
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$source = (Resolve-Path -LiteralPath $GameDirectory).Path
$target = Join-Path $repo 'obj\graphics-game'
if (-not (Test-Path -LiteralPath (Join-Path $source 'Data'))) { throw 'GameDirectory must contain the legitimate game Data directory.' }
$expected = '998384BB696F02D37A6725CEA33F98BC2CA711B803C2638BED07A91D687CA7CC'
if ((Get-FileHash -LiteralPath (Join-Path $repo 'ImpII.exe')).Hash -ne $expected) { throw 'Unsupported repository executable; revalidate graphics anchors first.' }
if (Test-Path -LiteralPath $target) { throw "Sandbox already exists: $target. Preserve it and choose a fresh workspace rather than overwriting saves." }
$audioDlls = @('_inmm.dll','libogg.dll','libvorbis.dll','libvorbisfile.dll')
foreach ($dll in $audioDlls) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $dll) -PathType Leaf)) { throw "Missing game dependency: $dll" }
}
foreach ($file in @('dplayx.dll','dplayx.ini')) {
    if (-not (Test-Path -LiteralPath (Join-Path $repo $file) -PathType Leaf)) { throw "Missing repository dependency: $file" }
}
New-Item -ItemType Directory -Path $target | Out-Null
foreach ($folder in @('Data','Movies','MUSIC','Save')) {
    $from = Join-Path $source $folder
    if (Test-Path -LiteralPath $from) { Copy-Item -LiteralPath $from -Destination $target -Recurse -Force }
}
foreach ($dll in $audioDlls) {
    Copy-Item -LiteralPath (Join-Path $source $dll) -Destination $target
}
Copy-Item -LiteralPath (Join-Path $repo 'ImpII.exe') -Destination (Join-Path $target 'Imperialism II.exe')
Copy-Item -LiteralPath (Join-Path $repo 'dplayx.dll') -Destination $target
Copy-Item -LiteralPath (Join-Path $repo 'dplayx.ini') -Destination $target
[pscustomobject]@{Directory=$target; ExecutableSha256=(Get-FileHash -LiteralPath (Join-Path $target 'Imperialism II.exe')).Hash; Source=$source} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $target 'sandbox.json') -Encoding UTF8
Write-Output $target
