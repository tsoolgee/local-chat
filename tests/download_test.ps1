# Download engine test: simple vs accelerated speed, stop + hard kill + resume, SHA256 check, discard.
# usage: pwsh tests/download_test.ps1 <folder with LocalChat.exe and engine\>
param([string]$Root)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $Root 'LocalChat.exe'
$url = 'https://huggingface.co/Green-Sky/SD-Turbo-GGUF/resolve/main/sd_turbo-f16-q8_0.gguf'
$sha = 'd50be7655f0a554cf8041c145d88b210bd5f3c545423119dee62ae08cae51580'
$dest = Join-Path $Root 'מודלים\תמונות\sd_turbo-f16-q8_0.gguf'
$script:fail = 0

function Start-App { $p = Start-Process $exe -ArgumentList '--no-window' -PassThru; Start-Sleep 5; $script:base = 'http://localhost:' + (Get-Content (Join-Path $Root 'נתונים\port.txt')).Trim() + '/api'; $p }
function Api($route, $body) {
  if ($body) { irm "$script:base/$route" -Method Post -ContentType 'application/json; charset=utf-8' -Body ($body | ConvertTo-Json -Compress) }
  else { irm "$script:base/$route" }
}
function Set-Mode($mode, $conns) { irm "$script:base/settings" -Method Put -ContentType 'application/json' -Body (@{ downloadMode = $mode; downloadConnections = $conns; proxy = '' } | ConvertTo-Json) | Out-Null }
function Job($id) { @(Api 'downloads') | ? { $_.id -eq $id } | select -First 1 }
function Check($ok, $what) { if ($ok) { "  PASS $what" } else { "  FAIL $what"; $script:fail++ } }

# measures bytes/second over a fixed window, after the first bytes arrive
function Measure-Speed($mode, $conns, $seconds) {
  Set-Mode $mode $conns
  $id = (Api 'download' @{ url = $url; kind = 'image' }).id
  $t = [Diagnostics.Stopwatch]::StartNew()
  do { Start-Sleep -Milliseconds 300; $j = Job $id } while ($j.done -lt 1MB -and $t.Elapsed.TotalSeconds -lt 60)
  $d0 = $j.done; $t.Restart()
  Start-Sleep $seconds
  $j = Job $id
  $mbps = ($j.done - $d0) / $t.Elapsed.TotalSeconds / 1MB
  Api 'download-cancel' @{ id = $id } | Out-Null; Start-Sleep 2
  Api 'download-discard' @{ id = $id } | Out-Null
  "{0,-6} x{1,-2}: {2,7:N1} MB/s  (connections reported: {3})" -f $mode, $conns, $mbps, $j.connections
  return $mbps
}

$p = Start-App
"== speed (20s each, direct connection)"
$s1 = Measure-Speed 'simple' 1 20
$s8 = Measure-Speed 'fast' 8 20
$s16 = Measure-Speed 'fast' 16 20
Check (-not (Test-Path "$dest.part") -and -not (Test-Path "$dest.part.json")) 'discard removed the temporary files'

"== stop, hard kill, resume"
Set-Mode 'fast' 8
$id = (Api 'download' @{ url = $url; kind = 'image' }).id
do { Start-Sleep 1; $j = Job $id } while ($j.done -lt 400MB)
Api 'download-cancel' @{ id = $id } | Out-Null; Start-Sleep 2
$j = Job $id; "  after cancel: state=$($j.state) done=$([int]($j.done/1MB))MB"
Check ($j.state -eq 'cancelled') 'cancel stops the download'
Check (Test-Path "$dest.part.json") 'state file kept after cancel'
# continue, then kill the app hard in the middle
Api 'download-resume' @{ id = $id } | Out-Null
do { Start-Sleep 1; $j = Job $id } while ($j.done -lt 1000MB -and $j.state -eq 'downloading')
"  before kill: done=$([int]($j.done/1MB))MB"
Stop-Process -Id $p.Id -Force; Start-Sleep 2
$p = Start-App
$left = @(Api 'downloads') | ? { $_.leftover }
"  after restart: " + (($left | % { "$($_.id) $($_.state) $([int]($_.done/1MB))MB/$([int]($_.total/1MB))MB" }) -join '; ')
Check ($left.Count -eq 1 -and $left[0].done -gt 500MB) 'unfinished download is listed after restart with its progress'
$rid = $left[0].id
Api 'download-resume' @{ id = $rid } | Out-Null
$t = [Diagnostics.Stopwatch]::StartNew()
do { Start-Sleep 2; $j = Job $rid } while ($j.state -eq 'downloading' -and $t.Elapsed.TotalMinutes -lt 15)
"  final: state=$($j.state) $($j.error)"
Check ($j.state -eq 'done') 'resumed download finished'
$h = (Get-FileHash $dest -Algorithm SHA256).Hash.ToLower()
Check ($h -eq $sha) "SHA256 matches ($h)"
Check (-not (Test-Path "$dest.part.json")) 'state file removed when done'

"== discard a stopped download"
Set-Mode 'fast' 8
$u2 = 'https://huggingface.co/bartowski/SmolLM2-360M-Instruct-GGUF/resolve/main/SmolLM2-360M-Instruct-Q8_0.gguf'
$id2 = (Api 'download' @{ url = $u2 }).id
do { Start-Sleep 1; $j = Job $id2 } while ($j.done -lt 50MB -and $j.state -eq 'downloading')
Api 'download-cancel' @{ id = $id2 } | Out-Null; Start-Sleep 2
$d2 = Join-Path $Root 'מודלים\SmolLM2-360M-Instruct-Q8_0.gguf'
Check (Test-Path "$d2.part") 'partial file exists before discard'
Api 'download-discard' @{ id = $id2 } | Out-Null
Check (-not (Test-Path "$d2.part") -and -not (Test-Path "$d2.part.json") -and -not (Job $id2)) 'discard deletes files and the list entry'

Stop-Process -Id $p.Id -Force
"SUMMARY simple=$([math]::Round($s1,1))MB/s fast8=$([math]::Round($s8,1))MB/s fast16=$([math]::Round($s16,1))MB/s failures=$script:fail"
if ($script:fail) { exit 1 }
