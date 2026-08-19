param(
    [Parameter(Mandatory)] [string] $Feature,
    [string] $Bundle,
    [string] $Sha256
)

. (Join-Path $PSScriptRoot 'common.ps1')
$root = Get-RepositoryRoot
$manifestPath = Join-Path $root 'dependencies\models.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$definition = @($manifest.features | Where-Object { $_.id -eq $Feature })
if ($definition.Count -ne 1) {
    $available = (@($manifest.features.id) -join ', ')
    throw "Unknown model feature '$Feature'. Available features: $available"
}
$definition = $definition[0]
$modelRoot = Join-Path $root $manifest.archiveRoot

$missing = @($definition.paths | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $modelRoot $_))
})
if ($missing.Count -eq 0) {
    Write-Host "Model feature '$Feature' is already installed."
    return
}

if ([string]::IsNullOrWhiteSpace($Bundle)) {
    $Bundle = [Environment]::GetEnvironmentVariable($definition.uriEnvironmentVariable)
}
if ([string]::IsNullOrWhiteSpace($Sha256)) {
    $Sha256 = [Environment]::GetEnvironmentVariable($definition.sha256EnvironmentVariable)
}
if ([string]::IsNullOrWhiteSpace($Bundle)) {
    throw "Model feature '$Feature' is missing. Pass -Bundle or set $($definition.uriEnvironmentVariable)."
}
if ([string]::IsNullOrWhiteSpace($Sha256)) {
    throw "A SHA256 is required. Pass -Sha256 or set $($definition.sha256EnvironmentVariable)."
}

$temporaryArchive = Join-Path ([IO.Path]::GetTempPath()) ([IO.Path]::GetRandomFileName() + '.zip')
try {
    Write-Host "Restoring model feature '$Feature' from $Bundle"
    Get-DownloadedFile -Source $Bundle -Destination $temporaryArchive
    Assert-Sha256 -Path $temporaryArchive -Expected $Sha256
    Expand-Archive -LiteralPath $temporaryArchive -DestinationPath $root -Force
}
finally {
    Remove-Item -LiteralPath $temporaryArchive -Force -ErrorAction SilentlyContinue
}

$missing = @($definition.paths | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $modelRoot $_))
})
if ($missing.Count -gt 0) {
    throw "Model archive did not contain the expected paths:`n  - $($missing -join "`n  - ")"
}
Write-Host "Model feature '$Feature' restored."
