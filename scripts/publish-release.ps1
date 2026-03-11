[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version = "1.0.0",

    [string]$RuntimeIdentifier = "win-x64"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-FourPartVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    $baseVersion = $Version.Split('-', 2)[0]
    $parts = [System.Collections.Generic.List[string]]::new()
    $parts.AddRange([string[]]$baseVersion.Split('.'))

    while ($parts.Count -lt 4) {
        $parts.Add("0")
    }

    if ($parts.Count -ne 4) {
        throw "Version '$Version' must contain three or four numeric segments before any prerelease suffix."
    }

    return [string]::Join('.', $parts)
}

function Ensure-BundledFfmpegPackage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot
    )

    $ffmpegRoot = Join-Path $RepoRoot "tools\ffmpeg"
    $packageRoot = Join-Path $ffmpegRoot "package"
    $archivePath = Join-Path $ffmpegRoot "ffmpeg.zip"

    $existingExecutable = Get-ChildItem -Path $packageRoot -Filter "ffmpeg.exe" -File -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1

    if ($null -ne $existingExecutable) {
        return
    }

    if (-not (Test-Path -LiteralPath $archivePath)) {
        throw "Bundled FFmpeg archive not found at '$archivePath'."
    }

    if (Test-Path -LiteralPath $packageRoot) {
        Remove-Item -LiteralPath $packageRoot -Recurse -Force
    }

    Write-Host "Extracting bundled FFmpeg from $archivePath"
    Expand-Archive -LiteralPath $archivePath -DestinationPath $packageRoot -Force

    $extractedExecutable = Get-ChildItem -Path $packageRoot -Filter "ffmpeg.exe" -File -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1

    if ($null -eq $extractedExecutable) {
        throw "FFmpeg extraction completed, but ffmpeg.exe was not found under '$packageRoot'."
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$projectPath = Join-Path $repoRoot "Strippr.csproj"
$artifactsRoot = Join-Path $repoRoot "artifacts"
$artifactName = "Strippr-v$Version-$RuntimeIdentifier"
$buildRoot = Join-Path ([System.IO.Path]::GetTempPath()) "Strippr-release\$artifactName"
$baseOutputPath = Join-Path $buildRoot "bin\"
$baseIntermediateOutputPath = Join-Path $buildRoot "obj\"
$publishDirectory = Join-Path $artifactsRoot $artifactName
$zipPath = Join-Path $artifactsRoot "$artifactName.zip"
$checksumPath = Join-Path $artifactsRoot "$artifactName.sha256.txt"
$assemblyVersion = Get-FourPartVersion -Version $Version

Ensure-BundledFfmpegPackage -RepoRoot $repoRoot

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null

foreach ($path in @($publishDirectory, $zipPath, $checksumPath)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

if (Test-Path -LiteralPath $buildRoot) {
    Remove-Item -LiteralPath $buildRoot -Recurse -Force
}

$publishArguments = @(
    "publish",
    $projectPath,
    "-c", "Release",
    "-r", $RuntimeIdentifier,
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:DebugType=None",
    "-p:DebugSymbols=false",
    "-p:Version=$Version",
    "-p:AssemblyVersion=$assemblyVersion",
    "-p:FileVersion=$assemblyVersion",
    "-p:BaseOutputPath=$baseOutputPath",
    "-p:BaseIntermediateOutputPath=$baseIntermediateOutputPath",
    "-o", $publishDirectory
)

Write-Host "Publishing $artifactName"
& dotnet @publishArguments

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed."
}

$developmentSettingsPath = Join-Path $publishDirectory "appsettings.Development.json"
if (Test-Path -LiteralPath $developmentSettingsPath) {
    Remove-Item -LiteralPath $developmentSettingsPath -Force
}

Get-ChildItem -Path $publishDirectory -Filter "*.pdb" -File -Recurse -ErrorAction SilentlyContinue |
    Remove-Item -Force

Compress-Archive -Path $publishDirectory -DestinationPath $zipPath -CompressionLevel Optimal

$hash = Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
$checksumLine = "{0} *{1}" -f $hash.Hash.ToLowerInvariant(), ([System.IO.Path]::GetFileName($zipPath))
Set-Content -LiteralPath $checksumPath -Value $checksumLine -NoNewline

if (Test-Path -LiteralPath $buildRoot) {
    Remove-Item -LiteralPath $buildRoot -Recurse -Force
}

Write-Host "Release output:"
Write-Host "  Publish folder: $publishDirectory"
Write-Host "  Zip file:       $zipPath"
Write-Host "  SHA-256 file:   $checksumPath"
