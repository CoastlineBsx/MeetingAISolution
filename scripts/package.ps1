param(
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release',
    [switch] $SkipBuild,
    [string] $NativeBundle,
    [string] $NativeBundleSha256
)

. (Join-Path $PSScriptRoot 'common.ps1')
$root = Get-RepositoryRoot
if (-not $SkipBuild) {
    $arguments = @{ Configuration = $Configuration }
    if ($NativeBundle) { $arguments.NativeBundle = $NativeBundle }
    if ($NativeBundleSha256) { $arguments.NativeBundleSha256 = $NativeBundleSha256 }
    & (Join-Path $PSScriptRoot 'build.ps1') @arguments
}

$binRoot = Join-Path $root "artifacts\bin\$Configuration\x64"
$hostExecutable = Get-ChildItem -LiteralPath $binRoot -Filter 'MeetingAI.Host.exe' -Recurse -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$worker = Get-ChildItem -LiteralPath $binRoot -Filter 'MeetingAI.Worker.exe' -Recurse -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $hostExecutable -or -not $worker) {
    throw 'Host or Worker output is missing. Run scripts\build.ps1 first.'
}

$publishRoot = Join-Path $root "artifacts\publish\MeetingAI-$Configuration-win-x64"
if (Test-Path -LiteralPath $publishRoot) {
    Remove-Item -LiteralPath $publishRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
Copy-Item -Path (Join-Path $hostExecutable.Directory.FullName '*') -Destination $publishRoot -Recurse -Force
Copy-Item -Path (Join-Path $worker.Directory.FullName '*') -Destination $publishRoot -Recurse -Force
Copy-Item -LiteralPath (Join-Path $root 'dependencies\models.json') -Destination (Join-Path $publishRoot 'models.json') -Force
New-Item -ItemType File -Path (Join-Path $publishRoot 'portable.flag') -Force | Out-Null

Get-ChildItem -LiteralPath $publishRoot -Filter '*.pdb' -File | Remove-Item -Force
Get-ChildItem -LiteralPath $publishRoot -Filter '*.lib' -File | Remove-Item -Force
$smokeTest = Join-Path $publishRoot 'sherpa_dual_stream_hotword_smoke_test.exe'
if (Test-Path -LiteralPath $smokeTest) { Remove-Item -LiteralPath $smokeTest -Force }

$readme = @'
MeetingAI for Windows x64

This is a portable package. Application data and model adapter caches are kept
under the local data directory beside the executables, so the package can run
from any writable drive. It contains the .NET and Windows App SDK runtimes,
but not AI models.
Install the required model feature bundles under MeetingAI.Worker\models, or
use the repository model restore script before packaging. Start
MeetingAI.Host.exe after the models are ready.
'@
Set-Content -LiteralPath (Join-Path $publishRoot 'README.txt') -Value $readme -Encoding utf8

$archive = "$publishRoot.zip"
if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
Compress-Archive -Path (Join-Path $publishRoot '*') -DestinationPath $archive -CompressionLevel Optimal
Write-Host "Package: $archive"
