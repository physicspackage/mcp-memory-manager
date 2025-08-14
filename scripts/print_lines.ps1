param(
  [string]$Path,
  [int]$Start=1,
  [int]$End=999999
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Path)) { throw "File not found: $Path" }
$lines = Get-Content -Path $Path
for($i=$Start; $i -le [Math]::Min($End, $lines.Length); $i++){
  "{0:D4}: {1}" -f $i, $lines[$i-1]
}

