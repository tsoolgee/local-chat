# Download engine test: simple vs accelerated speed, stop + hard kill + resume, SHA256 check, discard.
# usage: pwsh tests/download_test.ps1 <folder with LocalChat.exe and engine\>
param([string]$Root)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $Root 'LocalChat.exe'
$url = 'https://huggingface.co/unsloth/gemma-4-E4B-it-GGUF/resolve/main/gemma-4-E4B-it-Q4_K_M.gguf'
$sha = '85a896a047553e842f25297ee5b031d64ff30147d9c4af17b1e4b394cd1fab87'
$dest = Join-Path $Root 'מודלים\gemma-4-E4B-it-Q4_K_M.gguf'
$script:fail = 0

function Start-App { $p = Start-Process $exe -ArgumentList '--no-window' -PassThru; Start-Sleep 5; $script:base = 'http://localhost:' + (Get-Content (Join-Path $Root 'נתונים\port.txt')).Trim() + '/api'; $p }
function Api($route, $body) {
  if ($body) { irm "$script:base/$route" -Method Post -ContentType 'application/json; charset=utf-8' -Body ($body | ConvertTo-Json -Compress) }
  else { irm "$script:base/$route" }
}
function Set-Mode($mode, $conns) { irm "$script:base/settings" -Method Put -ContentType 'application/json' -Body (@{ downloadMode = $mode; downloadConnections = $conns; proxy = '' } | ConvertTo-Json) | Out-Null }
function Job($id) { @(Api 'downloads') | ? { $_.id -eq $id } | select -First 1 }
function Wait-Stopped($id) { $t = [Diagnostics.Stopwatch]::StartNew(); do { Start-Sleep -Milliseconds 300; $j = Job $id } while ($j -and $j.state -in 'downloading','queued' -and $t.Elapsed.TotalSeconds -lt 90); "  stopped after $([math]::Round($t.Elapsed.TotalSeconds,1))s: $($j.state)" | Write-Host; $j }
function Check($ok, $what) { if ($ok) { "  PASS $what" } else { "  FAIL $what"; $script:fail++ } }

# measures bytes/second over a fixed window, after the first bytes arrive
function Measure-Speed($mode, $conns, $seconds) {
  Set-Mode $mode $conns
  $id = (Api 'download' @{ url = $url; kind = 'chat' }).id
  $t = [Diagnostics.Stopwatch]::StartNew()
  do { Start-Sleep -Milliseconds 300; $j = Job $id } while ($j.done -lt 1MB -and $t.Elapsed.TotalSeconds -lt 60)
  $d0 = $j.done; $t.Restart()
  Start-Sleep $seconds
  $j = Job $id
  $mbps = ($j.done - $d0) / $t.Elapsed.TotalSeconds / 1MB
  Api 'download-cancel' @{ id = $id } | Out-Null; Wait-Stopped $id | Out-Null
  Api 'download-discard' @{ id = $id } | Out-Null
  Write-Host ("{0,-6} x{1,-2}: {2,7:N1} MB/s  (connections reported: {3})" -f $mode, $conns, $mbps, $j.connections)
  return $mbps
}

$p = Start-App
"== speed (20s each, direct connection)"
$s1 = Measure-Speed 'simple' 1 8
$s8 = Measure-Speed 'fast' 8 8
$s16 = Measure-Speed 'fast' 16 8
Check (-not (Test-Path "$dest.part") -and -not (Test-Path "$dest.part.json")) 'discard removed the temporary files'

"== stop, hard kill, resume"
Set-Mode 'fast' 8
$id = (Api 'download' @{ url = $url; kind = 'chat' }).id
do { Start-Sleep -Milliseconds 200; $j = Job $id } while ($j.done -lt 0.15 * [math]::Max(1.0, [double]$j.total) -and $j.state -eq 'downloading')
Api 'download-cancel' @{ id = $id } | Out-Null; Wait-Stopped $id | Out-Null
$j = Job $id; "  after cancel: state=$($j.state) done=$([int]($j.done/1MB))MB"
Check ($j.state -eq 'cancelled') 'cancel stops the download'
"  .part exists: $(Test-Path "$dest.part")  .part.json exists: $(Test-Path "$dest.part.json")"
$kept = $j.done
Check (Test-Path "$dest.part.json") 'state file kept after cancel'
# continue, then kill the app hard in the middle
Api 'download-resume' @{ id = $id } | Out-Null
Start-Sleep 3; $j = Job $id
Check ($j.done -ge $kept * 0.95) "resume after cancel continues from $([int]($kept/1MB))MB (now $([int]($j.done/1MB))MB)"
do { Start-Sleep -Milliseconds 200; $j = Job $id } while ($j.done -lt 0.4 * [double]$j.total -and $j.state -eq 'downloading')
"  before kill: done=$([int]($j.done/1MB))MB"
Stop-Process -Id $p.Id -Force; Start-Sleep 2
$p = Start-App
$left = @(Api 'downloads') | ? { $_.leftover }
"  after restart: " + (($left | % { "$($_.id) $($_.state) $([int]($_.done/1MB))MB/$([int]($_.total/1MB))MB" }) -join '; ')
Check ($left.Count -eq 1 -and $left[0].done -gt 0.2 * $left[0].total) 'unfinished download is listed after restart with its progress'
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
Start-Sleep -Milliseconds 300
Api 'download-cancel' @{ id = $id2 } | Out-Null; Wait-Stopped $id2 | Out-Null
$d2 = Join-Path $Root 'מודלים\SmolLM2-360M-Instruct-Q8_0.gguf'
Check (Test-Path "$d2.part") 'partial file exists before discard'
Api 'download-discard' @{ id = $id2 } | Out-Null
Check (-not (Test-Path "$d2.part") -and -not (Test-Path "$d2.part.json") -and -not (Job $id2)) 'discard deletes files and the list entry'


"== automatic mode"
# fast line: stays on one connection
irm "$script:base/settings" -Method Put -ContentType 'application/json' -Body (@{ downloadMode = 'auto'; autoSwitchMBps = 0 } | ConvertTo-Json) | Out-Null
Remove-Item $dest -Force
$id3 = (Api 'download' @{ url = $url; kind = 'chat' }).id
Start-Sleep 9; $j = Job $id3
"  auto on a fast line after 9s: connections=$($j.connections) done=$([int]($j.done/1MB))MB"
Api 'download-cancel' @{ id = $id3 } | Out-Null; Wait-Stopped $id3 | Out-Null; Api 'download-discard' @{ id = $id3 } | Out-Null
# pretend the line is slow: must switch to many connections and still produce the right file
irm "$script:base/settings" -Method Put -ContentType 'application/json' -Body (@{ downloadMode = 'auto'; autoSwitchMBps = 100000 } | ConvertTo-Json) | Out-Null
$id3 = (Api 'download' @{ url = $url; kind = 'chat' }).id
Start-Sleep 9; $j = Job $id3
"  auto on a 'slow' line after 9s: connections=$($j.connections) done=$([int]($j.done/1MB))MB"
Check ($j.connections -gt 1) 'automatic mode switches to many connections when one is slow'
$t = [Diagnostics.Stopwatch]::StartNew()
do { Start-Sleep 2; $j = Job $id3 } while ($j.state -eq 'downloading' -and $t.Elapsed.TotalMinutes -lt 15)
$h = (Get-FileHash $dest -Algorithm SHA256).Hash.ToLower()
Check ($j.state -eq 'done' -and $h -eq $sha) "switched download is complete and correct ($h)"

Stop-Process -Id $p.Id -Force
"SUMMARY simple=$([math]::Round($s1,1))MB/s fast8=$([math]::Round($s8,1))MB/s fast16=$([math]::Round($s16,1))MB/s failures=$script:fail"
if ($script:fail) { exit 1 }
