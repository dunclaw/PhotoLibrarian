# Automatic tagging model assets

PhotoLibrarian runs automatic-tagging models locally. Model files are not
currently bundled with the application. In **Settings**, select a profile,
follow its source link if desired, then choose **Import model assets** and
select a folder containing the exact compatible files below. Imports verify
the files before copying them into managed local storage.

## TinyCLIP

Required files:

- `tinyclip-vision-int8.onnx`
- `tinyclip.json`

The compatible bundle is generated from
[`wkcn/TinyCLIP-ViT-40M-32-Text-19M-LAION400M`](https://huggingface.co/wkcn/TinyCLIP-ViT-40M-32-Text-19M-LAION400M)
and its pinned
[`onnx-community` conversion](https://huggingface.co/onnx-community/TinyCLIP-ViT-40M-32-Text-19M-LAION400M-ONNX).
Run the repository preparation tool outside the source tree:

```powershell
python tools\PhotoLibrarian.ModelBench\prepare_zero_shot.py `
  --output D:\Models\TinyCLIP `
  --vocabulary tools\PhotoLibrarian.ModelBench\home-photo-vocabulary.general-v2.json `
  --models tinyclip --precision int8
```

TinyCLIP is published under the
[MIT license](https://github.com/microsoft/Cream/blob/4a13c4091e78f9abd2160e7e01c02e48c1cf8fb9/TinyCLIP/LICENSE).
Its weights and PhotoLibrarian's derived ONNX/embedding bundle can be hosted or
redistributed with the Microsoft copyright and MIT permission notice. The model
was trained on LAION-400M; its provenance and content caveats still apply.

## RAM++

Required files:

- `ram_plus_swin_large_14m.onnx`
- `ram_plus_swin_large_14m_labels.txt`

PhotoLibrarian owns the conversion because upstream publishes PyTorch, not ONNX.
The reproducible exporter is
[`tools/PhotoLibrarian.ModelBench/export_ram_plus.py`](tools/PhotoLibrarian.ModelBench/export_ram_plus.py).
It pins upstream source commit
`7cb804a8609e9f4b1a50b7f31436d2df40bb9481`, checkpoint repository revision
`84d4aee3a0265c4e0df1f714f0572011d1bf2ec3`, verifies both 3 GB checkpoint
inputs, verifies the authoritative 4,585-label vocabulary, and emits the
application-compatible `targets` output plus raw logits and probabilities. The
fixed contract is `image` float32 `[1,3,384,384]` (direct resize with ImageNet
normalization) and three float32 `[1,4585]` outputs. Keep
`ram_plus_manifest.json` and the generated `recognize-anything-LICENSE` and
`recognize-anything-NOTICE.txt` with any hosted bundle. If a future export
requires ONNX external data, retain every `.onnx.data*` file beside the model.

Prepare the export outside the repository:

```powershell
python -m pip install -r tools\PhotoLibrarian.ModelBench\requirements-ram-plus-export.txt
python tools\PhotoLibrarian.ModelBench\export_ram_plus.py `
  --output D:\Models\RAM-Plus-PhotoLibrarian
```

The manifest records every source revision, input/output contract, generated
hash, and external-data hash. Before hosting a generated bundle, run the
parity validation against the pinned upstream PyTorch implementation:

```powershell
python tools\PhotoLibrarian.ModelBench\validate_ram_plus.py `
  --bundle D:\Models\RAM-Plus-PhotoLibrarian\ram_plus_swin_large_14m.onnx `
  --source-dir D:\Models\RAM-Plus-PhotoLibrarian\source-cache\source\recognize-anything-7cb804a8609e9f4b1a50b7f31436d2df40bb9481 `
  --checkpoint D:\Models\RAM-Plus-PhotoLibrarian\source-cache\checkpoint\ram_plus_swin_large_14m.pth `
  --images D:\Models\RAM-Plus-Validation\*.jpg `
  --output D:\Models\RAM-Plus-PhotoLibrarian\parity-report.json
```

The report requires finite outputs, exact `targets` parity, and review of raw
logit/probability errors against the documented tolerance before hosting.
Record CPU/DirectML benchmark results separately; the old X-AnyLabeling-derived
ONNX must not be used as provenance or as the quality reference.

The upstream
[`recognize-anything-plus-model`](https://huggingface.co/xinyu1205/recognize-anything-plus-model)
checkpoint and
[`recognize-anything`](https://github.com/xinyu1205/recognize-anything) source are
[Apache-2.0](https://github.com/xinyu1205/recognize-anything/blob/7cb804a8609e9f4b1a50b7f31436d2df40bb9481/LICENSE).
Hosting or redistribution must retain the upstream
[`LICENSE`](https://github.com/xinyu1205/recognize-anything/blob/7cb804a8609e9f4b1a50b7f31436d2df40bb9481/LICENSE)
and
[`NOTICE.txt`](https://github.com/xinyu1205/recognize-anything/blob/7cb804a8609e9f4b1a50b7f31436d2df40bb9481/NOTICE.txt),
and identify the ONNX as a PhotoLibrarian conversion.

## Developer benchmark

The profile benchmark is a development diagnostic and is hidden from the normal
Settings UI. With Settings open, press **Ctrl+Shift+B** to show or hide it.
Benchmarking never writes generated tags to photos or the library.
