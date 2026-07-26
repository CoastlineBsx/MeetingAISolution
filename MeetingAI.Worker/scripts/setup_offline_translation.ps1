param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$workerRoot = Split-Path -Parent $PSScriptRoot
$modelRoot = Join-Path $workerRoot 'models\translation'
$converterRoot = Join-Path $env:TEMP 'MeetingAI-translation-converter'
$converterPython = Join-Path $converterRoot 'Scripts\python.exe'
$converterExe = Join-Path $converterRoot 'Scripts\ct2-transformers-converter.exe'

if (-not (Test-Path -LiteralPath $converterPython)) {
    python -m venv $converterRoot
}

& $converterPython -m pip install --disable-pip-version-check --upgrade `
    'ctranslate2==4.7.2' `
    'sentencepiece==0.2.1' `
    'transformers==5.14.1' `
    'torch'

New-Item -ItemType Directory -Force -Path $modelRoot | Out-Null

function Convert-TranslationModel {
    param(
        [Parameter(Mandatory)][string]$Model,
        [Parameter(Mandatory)][string]$Revision,
        [Parameter(Mandatory)][string]$OutputName
    )

    $outputPath = Join-Path $modelRoot $OutputName
    $modelFile = Join-Path $outputPath 'model.bin'
    if ((Test-Path -LiteralPath $modelFile) -and -not $Force) {
        Write-Host "Already ready: $outputPath"
        return
    }

    $arguments = @(
        '--model', $Model,
        '--revision', $Revision,
        '--output_dir', $outputPath,
        '--quantization', 'int8',
        '--copy_files', 'source.spm', 'target.spm', 'tokenizer_config.json'
    )
    if ($Force) {
        $arguments += '--force'
    }

    & $converterExe @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Model conversion failed: $Model"
    }
}

Convert-TranslationModel `
    -Model 'Helsinki-NLP/opus-mt-en-zh' `
    -Revision '408d9bc410a388e1d9aef112a2daba955b945255' `
    -OutputName 'opus-mt-en-zh'

Convert-TranslationModel `
    -Model 'Helsinki-NLP/opus-mt-zh-en' `
    -Revision 'cf109095479db38d6df799875e34039d4938aaa6' `
    -OutputName 'opus-mt-zh-en'

Write-Host "Offline translation models are ready in: $modelRoot"
