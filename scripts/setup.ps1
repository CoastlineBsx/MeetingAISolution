param(
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release',
    [string] $NativeBundle,
    [string] $NativeBundleSha256,
    [string[]] $ModelFeature = @(),
    [switch] $SkipRestore
)

. (Join-Path $PSScriptRoot 'common.ps1')

$root = Get-RepositoryRoot
if ($env:OS -ne 'Windows_NT') {
    throw 'MeetingAI currently supports Windows x64 only.'
}
if (-not [Environment]::Is64BitOperatingSystem) {
    throw 'MeetingAI requires a 64-bit Windows operating system.'
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK was not found. Install .NET 8 SDK or use Visual Studio Installer.'
}
$null = Get-MSBuildPath

$missing = @(Get-MissingNativeDependency -Configuration $Configuration)
if ($missing.Count -gt 0) {
    $manifest = Get-NativeManifest
    if ([string]::IsNullOrWhiteSpace($NativeBundle)) {
        $NativeBundle = [Environment]::GetEnvironmentVariable($manifest.uriEnvironmentVariable)
    }
    if ([string]::IsNullOrWhiteSpace($NativeBundleSha256)) {
        $NativeBundleSha256 = [Environment]::GetEnvironmentVariable($manifest.sha256EnvironmentVariable)
    }
    if ([string]::IsNullOrWhiteSpace($NativeBundle)) {
        $preview = ($missing | Select-Object -First 5) -join "`n  - "
        throw @"
Native dependencies are incomplete. Missing examples:
  - $preview

Provide -NativeBundle <path-or-uri>, or set $($manifest.uriEnvironmentVariable).
Create the bundle on the original development computer with:
  .\scripts\New-NativeBundle.ps1
"@
    }
    if ([string]::IsNullOrWhiteSpace($NativeBundleSha256)) {
        throw "A SHA256 is required for the native bundle. Pass -NativeBundleSha256 or set $($manifest.sha256EnvironmentVariable)."
    }

    $temporaryArchive = Join-Path ([IO.Path]::GetTempPath()) ([IO.Path]::GetRandomFileName() + '.zip')
    try {
        Write-Host "Restoring native dependencies from $NativeBundle"
        Get-DownloadedFile -Source $NativeBundle -Destination $temporaryArchive
        Assert-Sha256 -Path $temporaryArchive -Expected $NativeBundleSha256
        Expand-Archive -LiteralPath $temporaryArchive -DestinationPath $root -Force
    }
    finally {
        Remove-Item -LiteralPath $temporaryArchive -Force -ErrorAction SilentlyContinue
    }
}

$missing = @(Get-MissingNativeDependency -Configuration $Configuration)
if ($missing.Count -gt 0) {
    throw "Native bundle extraction completed, but required files are still missing:`n  - $($missing -join "`n  - ")"
}
Write-Host "Native dependencies are ready for $Configuration x64."

foreach ($feature in $ModelFeature) {
    & (Join-Path $PSScriptRoot 'Restore-Models.ps1') -Feature $feature
}

if (-not $SkipRestore) {
    $msbuild = Get-MSBuildPath
    Invoke-CheckedCommand -FilePath $msbuild -ArgumentList @(
        (Join-Path $root 'MeetingAISolution.sln'),
        '/t:Restore',
        '/p:RestorePackagesConfig=true',
        "/p:Configuration=$Configuration",
        '/p:Platform=x64',
        '/v:minimal'
    )
}

Write-Host 'MeetingAI setup completed.'
