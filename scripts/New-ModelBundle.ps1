param(
    [Parameter(Mandatory)] [string] $Feature,
    [string] $OutputDirectory
)

. (Join-Path $PSScriptRoot 'common.ps1')
$root = Get-RepositoryRoot
$manifest = Get-Content -LiteralPath (Join-Path $root 'dependencies\models.json') -Raw | ConvertFrom-Json
$definition = @($manifest.features | Where-Object { $_.id -eq $Feature })
if ($definition.Count -ne 1) {
    throw "Unknown model feature '$Feature'. Available: $(@($manifest.features.id) -join ', ')"
}
$definition = $definition[0]
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root 'artifacts\bundles'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$modelRoot = Join-Path $root $manifest.archiveRoot
$missing = @($definition.paths | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $modelRoot $_))
})
if ($missing.Count -gt 0) {
    throw "Cannot package missing model paths:`n  - $($missing -join "`n  - ")"
}

$staging = Join-Path ([IO.Path]::GetTempPath()) ('meetingai-model-' + [Guid]::NewGuid().ToString('N'))
$archiveName = "meetingai-model-$Feature.zip"
$archive = Join-Path $OutputDirectory $archiveName
try {
    foreach ($relativePath in $definition.paths) {
        $source = Join-Path $modelRoot $relativePath
        $destination = Join-Path (Join-Path $staging $manifest.archiveRoot) $relativePath
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
Set-Content -LiteralPath "$archive.sha256" -Value "$hash  $archiveName" -Encoding ascii
Write-Host "Bundle: $archive"
Write-Host "SHA256: $hash"
