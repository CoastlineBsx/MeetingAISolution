param(
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release',
    [string] $NativeBundle,
    [string] $NativeBundleSha256,
    [switch] $SkipSetup
)

. (Join-Path $PSScriptRoot 'common.ps1')
$root = Get-RepositoryRoot

if (-not $SkipSetup) {
    $setupArguments = @{ Configuration = $Configuration }
    if (-not [string]::IsNullOrWhiteSpace($NativeBundle)) {
        $setupArguments.NativeBundle = $NativeBundle
    }
    if (-not [string]::IsNullOrWhiteSpace($NativeBundleSha256)) {
        $setupArguments.NativeBundleSha256 = $NativeBundleSha256
    }
    & (Join-Path $PSScriptRoot 'setup.ps1') @setupArguments
}

$msbuild = Get-MSBuildPath
Invoke-CheckedCommand -FilePath $msbuild -ArgumentList @(
    (Join-Path $root 'MeetingAISolution.sln'),
    '/m',
    '/restore',
    '/p:RestorePackagesConfig=true',
    "/p:Configuration=$Configuration",
    '/p:Platform=x64',
    '/v:minimal'
)

$worker = Get-ChildItem -LiteralPath (Join-Path $root "artifacts\bin\$Configuration\x64\MeetingAI.Worker") -Filter 'MeetingAI.Worker.exe' -Recurse -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$hostExecutable = Get-ChildItem -LiteralPath (Join-Path $root "artifacts\bin\$Configuration\x64") -Filter 'MeetingAI.Host.exe' -Recurse -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $worker) { throw 'Build reported success, but MeetingAI.Worker.exe was not found.' }
if (-not $hostExecutable) { throw 'Build reported success, but MeetingAI.Host.exe was not found.' }

Write-Host "Worker: $($worker.FullName)"
Write-Host "Host:   $($hostExecutable.FullName)"
