param(
    [int]$Count = 1
)

$ErrorActionPreference = "Stop"

$projectRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")
$exePath = Join-Path $projectRoot "Builds\WoopRider.exe"

if (!(Test-Path -LiteralPath $exePath)) {
    throw "Build executable not found: $exePath"
}

for ($i = 1; $i -le $Count; $i++) {
    $logPath = Join-Path $projectRoot "Builds\client-$i.log"
    Start-Process -FilePath $exePath -ArgumentList @("-logFile", $logPath)
}
