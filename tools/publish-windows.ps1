#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$RuntimeIdentifier = 'win-x64'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($env:OS -ne 'Windows_NT') {
    throw 'Build this portable application on Windows.'
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Install the .NET 10 SDK before building the application.'
}

$repoDirectory = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoDirectory 'src/MeshCoreMessenger.Desktop/MeshCoreMessenger.Desktop.csproj'
$outputDirectory = Join-Path $repoDirectory "artifacts/windows/$RuntimeIdentifier"
$buildDirectory = Join-Path $outputDirectory ('build.' + [Guid]::NewGuid().ToString('N'))
$applicationDirectory = Join-Path $buildDirectory 'MeshCoreMessenger'
$archivePath = Join-Path $buildDirectory "MeshCoreMessenger-$RuntimeIdentifier.zip"

New-Item -ItemType Directory -Path $applicationDirectory -Force | Out-Null

# Keep all managed and native dependencies together, including the .NET runtime.
$publishArguments = @(
    'publish', $projectPath,
    '-c', 'Release',
    '-r', $RuntimeIdentifier,
    '--self-contained', 'true',
    '--disable-build-servers', '-m:1',
    '-p:UseSharedCompilation=false',
    '-p:PublishTrimmed=false',
    '-p:PublishSingleFile=false',
    '-o', $applicationDirectory
)
& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$executablePath = Join-Path $applicationDirectory 'MeshCoreMessenger.Desktop.exe'
$notificationResourcePath = Join-Path $applicationDirectory 'Microsoft.WindowsAppRuntime.Insights.Resource.dll'
foreach ($requiredPath in @($executablePath, $notificationResourcePath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required published file is missing: $requiredPath"
    }
}

# Archive the entire portable directory; distribute it together with its dependencies.
Compress-Archive -LiteralPath $applicationDirectory -DestinationPath $archivePath -CompressionLevel Optimal
Write-Host "Local app: $executablePath"
Write-Host "Archive: $archivePath"
