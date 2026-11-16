param([switch]$SkipMultiplayer)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$out=Join-Path $repo 'obj\hd-tests'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$csc='C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
function Run-Test([string]$name,[string[]]$sources,[string[]]$arguments=@()) {
    $exe=Join-Path $out "$name.exe"
    $files=@($sources | ForEach-Object { Join-Path $repo $_ })
    & $csc /nologo /platform:x86 /optimize+ /r:System.Drawing.dll /r:System.Windows.Forms.dll "/out:$exe" @files
    if($LASTEXITCODE -ne 0){throw "$name compilation failed"}
    & $exe @arguments
    if($LASTEXITCODE -ne 0){throw "$name failed"}
}
Push-Location $repo
try {
    & (Join-Path $PSScriptRoot 'TestMapViewportGeometry.ps1')
    Run-Test 'TestHdLayout' @('src\MapViewportGeometry.cs','src\HdLayout.cs','tests\TestHdLayout.cs')
    Run-Test 'TestNativeMapCache' @('src\NativeMapCache.cs','tests\TestNativeMapCache.cs') @((Join-Path $repo 'ImpII.exe'))
    Run-Test 'TestNativeMapInput' @('src\NativeMapInput.cs','tests\TestNativeMapInput.cs')
    Run-Test 'TestNativeMapEdges' @('src\NativeMapInput.cs','tests\TestNativeMapEdges.cs') @((Join-Path $repo 'ImpII.exe'))
    Run-Test 'TestNativeMapProjection' @('src\MapViewportGeometry.cs','src\HdLayout.cs','src\NativeMapCache.cs','src\NativeMapInput.cs',
        'src\NativeMapRenderer.cs','src\WindowedMapBridge.cs','tests\TestNativeMapProjection.cs') @((Join-Path $repo 'ImpII.exe'))
    & node (Join-Path $repo 'tests\TestRenderingProbe.js')
    if($LASTEXITCODE -ne 0){throw 'Rendering probe fixtures failed'}
    & node --check (Join-Path $repo 'src\graphics\observe-rendering.js')
    if($LASTEXITCODE -ne 0){throw 'Rendering probe syntax failed'}
    & (Join-Path $PSScriptRoot 'BuildWindowedMap.ps1') -BuildOnly
    Run-Test 'TestWindowedMapBridge' @('src\MapViewportGeometry.cs','src\HdLayout.cs','src\NativeMapCache.cs','src\NativeMapInput.cs',
        'src\NativeMapRenderer.cs','src\WindowedMapBridge.cs','tests\TestWindowedMapBridge.cs')
    if(-not $SkipMultiplayer) {
        Run-Test 'TestMultiplayer' @('tests\TestMultiplayer.cs') @((Join-Path $repo 'obj\windowed-map\dplayx.dll'))
    }
} finally { Pop-Location }
