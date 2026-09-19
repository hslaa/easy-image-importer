# Animal recognition models

The app finds empty frames and suggests animals with **SpeciesNet** (Google), which combines
**MegaDetector v5a** (finds animals, people and vehicles) with a species classifier. The app
runs them with ONNX Runtime on the CPU and downloads them on first use (~393 MB), from the
release tagged `models-1` in this repository. The files are pinned by SHA-256 in
`src/EasyImageImporter.Core/Recognition/Recognizer.cs` (`ModelStore.Files`).

## Build

```bash
python3.12 -m venv .venv
.venv/bin/pip install speciesnet onnx onnxruntime onnxscript
.venv/bin/python tools/models/convert.py out/
```

`convert.py` writes:

| File | What |
|---|---|
| `detector.onnx` | MegaDetector v5a, weights stored as float16 (computes in float32) |
| `classifier.onnx` | SpeciesNet v4.0.3a "always crop" classifier, same |
| `norway.json` | classifier labels, the full taxonomy, and which taxa SpeciesNet's geofence rules out in Norway |

## Verify

First run SpeciesNet itself on a test card (see `tools/testdata`) to get a reference:

```bash
.venv/bin/python -m speciesnet.scripts.run_model --folders testdata/cards/reset-clock/DCIM \
    --predictions_json ref.json --country USA --admin1_region WA
.venv/bin/python tools/models/verify.py out/ testdata/cards/reset-clock/DCIM ref.json USA WA
```

This runs SpeciesNet's own preprocessing and decision code with the ONNX models swapped in; it
should report every prediction identical. Then write the reference the C# port is tested against
(Norway geofence, as the app uses) and run that test:

```bash
.venv/bin/python tools/models/verify.py out/ testdata/cards/reset-clock/DCIM - NOR
EASYIMAGEIMPORTER_MODELS=out dotnet test --filter RecognitionTests
```

## Publish

Upload `detector.onnx`, `classifier.onnx` and `norway.json` as assets of a release tagged
`models-N`, update `ModelStore.ReleaseUrl` and the sizes and hashes in `ModelStore.Files`.
A new tag per model version: an installed app keeps using the files its version expects.

## Licences and attribution

- **SpeciesNet** (code, classifier and taxonomy/geofence data): Apache-2.0, © Google.
  Gadelha et al., *To crop or not to crop: comparing whole-image and cropped classification on
  a large dataset of camera trap images*, IET Computer Vision, 2024. https://github.com/google/cameratrapai
- **MegaDetector v5a**: MIT, © Microsoft / AI for Good Lab and contributors.
  Beery, Morris & Yang, *Efficient pipeline for camera trap image review*, 2019.
  https://github.com/agentmorris/MegaDetector
