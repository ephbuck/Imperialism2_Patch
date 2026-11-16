param(
    [string]$GameDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'obj\graphics-game')
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$target = (Resolve-Path -LiteralPath $GameDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $target 'Data') -PathType Container)) {
    throw 'Use a copied Imperialism II installation containing Data.'
}
$obj = Join-Path $repo 'obj\windowed-map'
New-Item -ItemType Directory -Path $obj -Force | Out-Null
$csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
$ilasm = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\ilasm.exe'
$sources = @('Imp2Net.cs','WorldSync.cs','NativeMapCache.cs','NativeMapInput.cs','NativeMapRenderer.cs','WindowedMapBridge.cs','WindowedMapStartup.cs') |
    ForEach-Object { Join-Path $PSScriptRoot $_ }
& $csc /nologo /platform:x86 /target:library /optimize+ /r:System.Drawing.dll /r:System.Windows.Forms.dll "/out:$obj\Imp2Net.dll" @sources
if ($LASTEXITCODE -ne 0) { throw 'Windowed map compilation failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'dplayx.il') -Destination $obj -Force
Push-Location $obj
try {
    & $ilasm /nologo /quiet /dll /output=dplayx.dll dplayx.il
    if ($LASTEXITCODE -ne 0) { throw 'Windowed map DLL assembly failed.' }
} finally { Pop-Location }
$inputExe = Join-Path $repo 'ImpII.exe'
$outputExe = Join-Path $obj 'Imperialism II.exe'
& (Join-Path $PSScriptRoot 'PatchWindowedMap.ps1') -InputExe $inputExe -OutputExe $outputExe
# Back up these three install files once; saves/assets are never overwritten.
foreach ($name in @('Imperialism II.exe','dplayx.dll')) {
    $existing = Join-Path $target $name
    if ((Test-Path -LiteralPath $existing) -and -not (Test-Path -LiteralPath "$existing.before-windowed-map")) {
        Copy-Item -LiteralPath $existing -Destination "$existing.before-windowed-map"
    }
}
Copy-Item -LiteralPath $outputExe -Destination $target -Force
Copy-Item -LiteralPath (Join-Path $obj 'dplayx.dll') -Destination $target -Force
if (-not (Test-Path -LiteralPath (Join-Path $target 'windowed-map.ini'))) {
    [IO.File]::WriteAllText((Join-Path $target 'windowed-map.ini'), "[WindowedMap]`r`n; Presence enables the native map window.`r`n", [Text.Encoding]::ASCII)
}
Write-Output "Built and installed resizable native map in: $target"
