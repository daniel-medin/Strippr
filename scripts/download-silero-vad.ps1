param(
    [string]$Destination = "tools/silero/silero_vad.onnx"
)

$ErrorActionPreference = "Stop"

$destinationPath = if ([System.IO.Path]::IsPathRooted($Destination)) {
    $Destination
} else {
    Join-Path $PSScriptRoot "..\\$Destination"
}

$destinationPath = [System.IO.Path]::GetFullPath($destinationPath)
$destinationDirectory = Split-Path -Parent $destinationPath
if (-not (Test-Path $destinationDirectory)) {
    New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
}

$url = "https://github.com/snakers4/silero-vad/raw/master/src/silero_vad/data/silero_vad.onnx"
Write-Host "Downloading Silero VAD model from $url"
Invoke-WebRequest -Uri $url -OutFile $destinationPath
Write-Host "Saved model to $destinationPath"
