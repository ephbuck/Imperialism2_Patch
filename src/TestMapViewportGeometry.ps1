$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$outputDir = Join-Path $repoRoot 'obj\graphics-tests'
$compiler = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw "Missing x86 .NET compiler: $compiler" }
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$testExe = Join-Path $outputDir 'TestMapViewportGeometry.exe'
& $compiler /nologo /platform:x86 /target:exe /optimize+ /out:$testExe `
    (Join-Path $repoRoot 'src\MapViewportGeometry.cs') `
    (Join-Path $repoRoot 'tests\TestMapViewportGeometry.cs')
if ($LASTEXITCODE -ne 0) { throw "Geometry compilation failed: $LASTEXITCODE" }
& $testExe
if ($LASTEXITCODE -ne 0) { throw "Geometry tests failed: $LASTEXITCODE" }
