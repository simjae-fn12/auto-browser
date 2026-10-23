param(
    [Parameter(Mandatory=$true, Position=0)][string]$Command,
    [Parameter(Position=1)][string]$Arg1,
    [Parameter(Position=2)][string]$Arg2,
    [Parameter(Position=3)][string]$Arg3
)

$connectionPath = Join-Path $env:APPDATA 'pluck-windows-native\connection.json'
if (-not (Test-Path -LiteralPath $connectionPath)) { throw 'pluck Windows Native를 먼저 실행하세요.' }
$connection = Get-Content -LiteralPath $connectionPath -Raw -Encoding UTF8 | ConvertFrom-Json
# Every command acts for one session. Run `session` once, then set $env:PLUCK_SESSION to the id it prints.
$body = @{ command = $Command; session = $env:PLUCK_SESSION }
switch ($Command) {
    'session' { $body.label = $Arg1 }
    'active' { }
    'new' { $body.url = $Arg1 }
    'list' { }
    'save' { }
    'show' { $body.id = [int]$Arg1 }
    'snapshot' { $body.id = [int]$Arg1; if ($Arg2 -eq 'full') { $body.textLimit = 20000; $body.elementLimit = 250 }; if ($Arg2 -eq 'diff') { $body.diff = $true } }
    'close' { $body.id = [int]$Arg1 }
    'screenshot' { $body.id = [int]$Arg1; if ($Arg3 -eq 'annotate') { $body.annotate = $true } }
    'goto' { $body.id = [int]$Arg1; $body.url = $Arg2 }
    'click' { $body.id = [int]$Arg1; $body.target = $Arg2 }
    'fill' { $body.id = [int]$Arg1; $body.target = $Arg2; $body.value = $Arg3 }
    'hover' { $body.id = [int]$Arg1; $body.target = $Arg2 }
    'type' { $body.id = [int]$Arg1; $body.value = $Arg2 }
    'select' { $body.id = [int]$Arg1; $body.target = $Arg2; $body.value = $Arg3 }
    'clickAt' { $body.id = [int]$Arg1; $body.x = [double]$Arg2; $body.y = [double]$Arg3 }
    'back' { $body.id = [int]$Arg1 }
    'forward' { $body.id = [int]$Arg1 }
    'press' { $body.id = [int]$Arg1; $body.key = $Arg2 }
    'scroll' { $body.id = [int]$Arg1; $body.direction = $Arg2; $body.pixels = [int]$Arg3 }
    'wait' { $body.id = [int]$Arg1; $body.target = $Arg2; $body.timeout = [int]$Arg3 }
    'js' { $body.id = [int]$Arg1; $body.code = $Arg2 }
    default { throw "Unknown command: $Command" }
}
$result = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$($connection.port)/" -Headers @{ Authorization = "Bearer $($connection.token)" } -ContentType 'application/json; charset=utf-8' -Body ($body | ConvertTo-Json -Compress)
if ($Command -eq 'screenshot') {
    $outputPath = if ($Arg2) { $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Arg2) } else { Join-Path (Get-Location) "tab-$Arg1.png" }
    [System.IO.File]::WriteAllBytes($outputPath, [Convert]::FromBase64String($result.png))
    Write-Output $outputPath
} else {
    $result | ConvertTo-Json -Depth 20
}
