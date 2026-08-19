param(
    [string] $OutputDirectory
)

. (Join-Path $PSScriptRoot 'common.ps1')
$root = Get-RepositoryRoot
$manifest = Get-NativeManifest
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root 'artifacts\bundles'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$missing = @(Get-MissingNativeDependency -Configuration Release)
$missing += @(Get-MissingNativeDependency -Configuration Debug)
$missing = @($missing | Sort-Object -Unique)
if ($missing.Count -gt 0) {
    throw "Cannot package incomplete native dependencies:`n  - $($missing -join "`n  - ")"
}

$staging = Join-Path ([IO.Path]::GetTempPath()) ('meetingai-native-' + [Guid]::NewGuid().ToString('N'))
$archive = Join-Path $OutputDirectory $manifest.archiveName
try {
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    foreach ($relativeDirectory in $manifest.directories) {
        $source = Join-Path $root $relativeDirectory
        $destination = Join-Path $staging $relativeDirectory
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination -Recurse -Force
    }
    if (Test-Path -LiteralPath $archive) {
        Remove-Item -LiteralPath $archive -Force
    }
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $archive -CompressionLevel Optimal
}
finally {
    Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
}

$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
$sidecar = "$archive.sha256"
Set-Content -LiteralPath $sidecar -Value "$hash  $($manifest.archiveName)" -Encoding ascii
Write-Host "Bundle: $archive"
Write-Host "SHA256: $hash"
