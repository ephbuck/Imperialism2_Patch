param(
    [ValidateRange(1,60)][int]$Seconds = 30,
    [string]$ReaScript
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$sandbox = Join-Path $repo 'obj\graphics-game'
$exe = Join-Path $sandbox 'Imperialism II.exe'
$probe = Join-Path $PSScriptRoot 'graphics\observe-rendering.js'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Run PrepareGraphicsSandbox.ps1 first.' }
if ((Get-FileHash -LiteralPath $exe).Hash -ne '998384BB696F02D37A6725CEA33F98BC2CA711B803C2638BED07A91D687CA7CC') { throw 'Unsupported sandbox executable.' }
$out = Join-Path $repo ('obj\rendering-observation-{0}.json' -f (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
$arguments = @('instrument-with-frida','spawn',$exe,'--script-file',$probe,'--duration-ms',($Seconds * 1000),'--format','json')
Write-Host "Observing sandbox for $Seconds seconds. Evidence: $out"
Write-Host 'REA releases instrumentation after capture; spawned process lifetime depends on the session owner.'
Push-Location $sandbox
try {
    if ($ReaScript) {
        & node $ReaScript @arguments | Set-Content -LiteralPath $out -Encoding UTF8
    } else {
        & npx.cmd --yes rea-agents@6.9.0 @arguments | Set-Content -LiteralPath $out -Encoding UTF8
    }
    if ($LASTEXITCODE -ne 0) { throw "REA failed ($LASTEXITCODE); inspect $out and stderr." }
    $record = Get-Content -LiteralPath $out -Raw | ConvertFrom-Json
    if ($record.error) { throw ($record.error | ConvertTo-Json -Depth 8) }
    if (-not $record.evidence_id) { throw 'Output was not a complete REA Evidence record.' }
    if ($record.normalized_result.messages_truncated) { throw 'Trace retention was truncated; reduce duration/detail.' }
    if ($record.normalized_result.cleanup_error) { throw "REA cleanup failed; retain the session and inspect $out." }
    $messages = @($record.normalized_result.messages)
    $ready = @($messages | Where-Object { $_.payload.kind -eq 'ready' })
    $errors = @($messages | Where-Object { $_.type -eq 'error' -or $_.payload.kind -eq 'refused' -or $_.payload.kind -eq 'probe-error' })
    if ($ready.Count -ne 1 -or $errors.Count -ne 0) { throw "Probe did not complete cleanly; inspect $out." }
    Write-Host "Evidence retained: $($record.evidence_id)"
} finally { Pop-Location }
Write-Output $out
