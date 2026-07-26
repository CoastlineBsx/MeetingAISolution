# Offline real-time translation

The Streaming Meeting page translates Sherpa-ONNX transcripts locally with
CTranslate2 4.7.2 and INT8 OPUS-MT models.

The optimized translation runtime is enabled in `Release | x64`. A Debug build
keeps transcription available but reports that translation is disabled.

## Prepare the models

From the repository root, run:

```powershell
powershell -ExecutionPolicy Bypass -File .\MeetingAI.Worker\scripts\setup_offline_translation.ps1
```

The script uses an isolated virtual environment under `%TEMP%` and writes the
two converted models to:

- `MeetingAI.Worker\models\translation\opus-mt-en-zh`
- `MeetingAI.Worker\models\translation\opus-mt-zh-en`

The model directories are intentionally ignored by Git. The application finds
them from the repository tree when running a local build.

## Runtime behavior

- `自动互译`: Chinese transcripts are translated to English and English
  transcripts are translated to Simplified Chinese.
- Partial hypotheses are throttled and superseded by newer revisions.
- Final hypotheses are never dropped and are drained before the session stops.
- Original text is shown immediately; its translation is added to the same
  caption when ready.
- Translation runs on a separate CPU queue and never blocks audio ingestion.

## Third-party licenses

- CTranslate2: MIT
- SentencePiece: Apache-2.0
- `Helsinki-NLP/opus-mt-en-zh`: Apache-2.0
- `Helsinki-NLP/opus-mt-zh-en`: CC-BY-4.0

Keep the corresponding license and attribution notices when distributing the
application.
