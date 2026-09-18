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

The upstream
[`recognize-anything-plus-model`](https://huggingface.co/xinyu1205/recognize-anything-plus-model)
checkpoint and source are
[Apache-2.0](https://github.com/xinyu1205/recognize-anything/blob/7cb804a8609e9f4b1a50b7f31436d2df40bb9481/LICENSE).
Apache-2.0 permits redistribution and conversion when its license, attribution,
change notices, and the upstream
[`NOTICE.txt`](https://github.com/xinyu1205/recognize-anything/blob/7cb804a8609e9f4b1a50b7f31436d2df40bb9481/NOTICE.txt)
are retained.

PhotoLibrarian's currently tested ONNX file was obtained through a third-party
conversion, and a reproducible conversion recipe plus authoritative provenance
for the exact companion label file has not yet been established. The upstream
PyTorch checkpoint cannot be imported directly. Do not mirror the current ONNX
bundle as conclusively Apache-cleared; first replace it with a documented export
from the pinned upstream checkpoint. Until a compatible cleared bundle is
hosted, users who already have the exact tested files can import them.

## Developer benchmark

The profile benchmark is a development diagnostic and is hidden from the normal
Settings UI. With Settings open, press **Ctrl+Shift+B** to show or hide it.
Benchmarking never writes generated tags to photos or the library.
