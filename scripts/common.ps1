Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:RepositoryRoot = Split-Path -Parent $PSScriptRoot

function Get-RepositoryRoot {
    return $script:RepositoryRoot
}

function Get-MSBuildPath {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
        throw 'Visual Studio Installer (vswhere.exe) was not found. Install Visual Studio 2022 first.'
    }

    $installation = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($installation)) {
        throw 'Visual Studio 2022 with MSBuild was not found. Install the components from .vsconfig.'
    }

    $msbuild = Join-Path $installation 'MSBuild\Current\Bin\amd64\MSBuild.exe'
    if (-not (Test-Path -LiteralPath $msbuild -PathType Leaf)) {
        $msbuild = Join-Path $installation 'MSBuild\Current\Bin\MSBuild.exe'
    }
    if (-not (Test-Path -LiteralPath $msbuild -PathType Leaf)) {
        throw "MSBuild.exe was not found under $installation."
    }
    return $msbuild
}

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)] [string] $FilePath,
        [Parameter(Mandatory)] [string[]] $ArgumentList
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE."
    }
}

function Get-NativeManifest {
    $path = Join-Path $script:RepositoryRoot 'dependencies\native-win-x64.json'
    return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}

function Get-MissingNativeDependency {
    param([ValidateSet('Debug', 'Release')] [string] $Configuration)

    $manifest = Get-NativeManifest
    $required = @($manifest.required.common) + @($manifest.required.$Configuration)
    return @($required | Where-Object {
        -not (Test-Path -LiteralPath (Join-Path $script:RepositoryRoot $_) -PathType Leaf)
    })
}

function Assert-Sha256 {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Expected
    )

    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    $normalizedExpected = $Expected.Trim().ToLowerInvariant()
    if ($actual -ne $normalizedExpected) {
        throw "SHA256 mismatch for $Path. Expected $normalizedExpected but got $actual."
    }
}

function Get-DownloadedFile {
    param(
        [Parameter(Mandatory)] [string] $Source,
        [Parameter(Mandatory)] [string] $Destination
    )

    if (Test-Path -LiteralPath $Source -PathType Leaf) {
        Copy-Item -LiteralPath $Source -Destination $Destination -Force
        return
    }

    $uri = $null
    if ([Uri]::TryCreate($Source, [UriKind]::Absolute, [ref]$uri) -and
        $uri.Scheme -in @('https', 'http')) {
        $headers = @{}
        $downloadToken = [Environment]::GetEnvironmentVariable('MEETINGAI_DOWNLOAD_TOKEN')
        if ([string]::IsNullOrWhiteSpace($downloadToken)) {
            $downloadToken = [Environment]::GetEnvironmentVariable('GITHUB_TOKEN')
        }
        if (-not [string]::IsNullOrWhiteSpace($downloadToken)) {
            $headers.Authorization = "Bearer $downloadToken"
            $headers.Accept = 'application/octet-stream'
        }
        Invoke-WebRequest -Uri $uri -OutFile $Destination -Headers $headers -UseBasicParsing
        return
    }

    throw "Dependency source is neither a file nor an HTTP(S) URI: $Source"
}
