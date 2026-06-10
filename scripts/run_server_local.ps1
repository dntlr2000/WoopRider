$ErrorActionPreference = "Stop"

$projectRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")
$exePath = Join-Path $projectRoot "Builds\WoopRider.exe"
$logDirectory = Join-Path $projectRoot "Logs\ServerLogs"
$logStamp = Get-Date -Format "yyyyMMdd-HHmmss"
$logPath = Join-Path $logDirectory "wooprider-server-$logStamp.log"

if (!(Test-Path -LiteralPath $exePath)) {
    throw "Build executable not found: $exePath"
}

New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
Write-Host "Server log: $logPath"
& $exePath -server -port 7777 -roomId test-room-1 -maxPlayers 6 -logFile $logPath
