$ErrorActionPreference = 'Stop'
$path = Join-Path $PSScriptRoot '..\src\McpMemoryManager.Server\Tools\ToolHost.cs'
if (-not (Test-Path $path)) { throw "File not found: $path" }
$content = Get-Content -Path $path -Raw
Write-Host ("Length=" + $content.Length)
param([int]$Start=0, [int]$Len=2000)
if ($Start -lt 0) { $Start = 0 }
if ($Start -ge $content.Length) { $Start = $content.Length - 1 }
$len2 = [Math]::Min($Len, $content.Length - $Start)
Write-Output ($content.Substring($Start, $len2))

