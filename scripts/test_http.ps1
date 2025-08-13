$ErrorActionPreference = 'Stop'
$useRoot = $false
if ($args.Length -gt 0 -and $args[0] -eq 'root') { $useRoot = $true }

Write-Host 'Starting server (--http 127.0.0.1:8765)...'
$proc = Start-Process dotnet -ArgumentList 'run --project src/McpMemoryManager.Server -- --http 127.0.0.1:8765' -PassThru -WindowStyle Hidden

# Wait for server readiness (max ~10s)
$ready = $false
for ($i=0; $i -lt 20; $i++) {
  try {
    Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:8765/' -Method Get -TimeoutSec 1 | Out-Null
    $ready = $true; break
  } catch { Start-Sleep -Milliseconds 500 }
}
if (-not $ready) { throw 'Server did not become ready in time.' }

try {
  Write-Host '--- /mcp initialize ---'
  $initBody = '{"jsonrpc":"2.0","id":1,"method":"initialize"}'
  if ($useRoot) { $uri = 'http://127.0.0.1:8765/' } else { $uri = 'http://127.0.0.1:8765/mcp' }
  $resp1 = Invoke-WebRequest -UseBasicParsing -Uri $uri -Method Post -ContentType 'application/json' -Body $initBody -TimeoutSec 5
  Write-Output $resp1.Content

  Write-Host '--- /mcp tools/list (truncated) ---'
  $listBody = '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'
  $resp2 = Invoke-WebRequest -UseBasicParsing -Uri $uri -Method Post -ContentType 'application/json' -Body $listBody -TimeoutSec 5
  $out = $resp2.Content
  if ($out.Length -gt 500) {
    Write-Output ($out.Substring(0,500))
    Write-Output '...'
    Write-Output ('len=' + $out.Length)
  } else {
    Write-Output $out
  }

  Write-Host '--- /sse headers ---'
  $req = [System.Net.WebRequest]::Create('http://127.0.0.1:8765/sse')
  $req.Method = 'GET'
  $req.Timeout = 2000
  try {
    $resp = $req.GetResponse()
    Write-Output ('status=' + [int]$resp.StatusCode)
    Write-Output ('content-type=' + $resp.ContentType)
    Write-Output ($resp.Headers.ToString())
    $resp.Close()
  }
  catch {
    Write-Output ('SSE connect error: ' + $_.Exception.Message)
  }
}
finally {
  if ($proc -and !$proc.HasExited) {
    Stop-Process -Id $proc.Id -Force
  }
  Write-Host 'Server stopped.'
}
