# Local restoration model gate

Evaluated 2026-10-30 against the pinned upstream source revisions below. This
is a technical and provenance gate, not legal advice. **No candidate is
approved for export, bundling, or application integration.** The source
licenses do not independently establish redistribution terms for the linked
checkpoint files. No weights were downloaded or copied.

## Candidate outcomes

| Candidate | Task evidence | Pinned source and checkpoint pointer | Weight terms and identity | Outcome |
| --- | --- | --- | --- | --- |
| NAFNet SIDD width32 | RGB developed-image denoising; the upstream SIDD test config uses width 32, encoder blocks `[2,2,4,8]`, 12 middle blocks, and decoder blocks `[2,2,2,2]`. It is not a sensor-mosaic or arbitrary-noise model. | [Source/config at `2b4af71`](https://github.com/megvii-research/NAFNet/tree/2b4af71ebe098a92a75910c233a3965a3e93ede4); [official SIDD model link](https://drive.google.com/file/d/1lsByk21Xw-6aW7epCwOQxvm6HYCQZPHZ/view?usp=sharing). | Source tree is MIT and carries BasicSR Apache-2.0 notices. The checkpoint's terms are not stated in the pinned model docs. [NAFNet issue #194](https://github.com/megvii-research/NAFNet/issues/194) asks the authors whether this exact checkpoint can be commercially used, redistributed, or converted; it remains open without an author answer. That issue identifies 116,861,841 bytes and SHA-256 `89c70e808d1783b6c07911306e106aaf0d4f7f3da8c61078b99ff7f8929a26f4`, but this checkout did not download or independently verify those bytes. | **NO-GO.** Terms unresolved; no export or quality/device test. |
| NAFNet GoPro width32 | RGB camera-motion deblurring trained/evaluated on GoPro; not evidence of arbitrary subject-motion or defocus recovery. The test config uses `NAFNetLocal`, width 32, encoder blocks `[1,1,1,28]`, one middle block, and decoder blocks `[1,1,1,1]`. | [Source/config at `2b4af71`](https://github.com/megvii-research/NAFNet/tree/2b4af71ebe098a92a75910c233a3965a3e93ede4); [official GoPro model link](https://drive.google.com/file/d/1Fr2QadtDCEXg6iwWX8OzeZLbHOx2t5Bj/view?usp=sharing). | The source license is not an explicit grant for this externally linked checkpoint. No weight-specific permission, independently verified file size, or checkpoint hash was found. The local-pooling GoPro checkpoint is distinct from the SIDD weights. | **NO-GO.** Terms and content identity unresolved; no export or quality/device test. |
| Restormer single-image defocus | Single-image RGB defocus restoration; dual-pixel input is excluded. The pinned config uses 3 input/output channels, dimension 48, blocks `[4,6,6,8]`, four refinement blocks, heads `[1,2,4,8]`, and `dual_pixel_task: false`. | [Source/config at `68dc6ac`](https://github.com/swz30/Restormer/tree/68dc6ac472db26f16361150cb7a96a1bc87da93f); [official defocus model folder](https://drive.google.com/drive/folders/1bRBG8DG_72AGA6-eRePvChlT5ZO4cwJ4?usp=sharing). The pinned test script expects `pretrained_models/single_image_defocus_deblurring.pth`. | The repository's MIT license covers the software and documentation; the README links the separate Drive model folder but does not state that the checkpoint has the same license. No weight-specific grant, exact Drive file revision, size, or hash was found. | **NO-GO.** Weight terms and exact content identity unresolved; no export or quality/device test. |

These are separate task-specific checkpoints. No result for one task is
evidence for another. In particular, GoPro motion results do not validate
defocus performance, and SIDD scores do not establish preservation of
texture, JPEG detail, or arbitrary camera noise in this editor.

## Upstream tensor and image contracts

The following records the pinned upstream inference behavior, **not an ONNX
export contract**:

| Candidate | Input and preprocessing | Spatial behavior and output |
| --- | --- | --- |
| NAFNet SIDD width32 | `float32` NCHW RGB, batch 1, 3 channels, values in `[0,1]`; the upstream paired-image loader decodes BGR, converts to RGB/CHW, and applies no mean/std normalization in this config. | The network pads only the right and bottom with zeros to a multiple of 8, then crops its residual output back to the original height/width. Output shape matches input. Upstream image saving clamps to `[0,1]`. |
| NAFNet GoPro width32 | Same RGB, range, dtype, and layout as SIDD. | Same inherited multiple-of-8 padding/crop. `NAFNetLocal` converts global adaptive pooling into input-size-dependent local pooling using a 256×256 train size and a 1.5× base size; changing image/tile dimensions can change those pooling statistics. Output shape matches input; upstream saving clamps to `[0,1]`. |
| Restormer single-image defocus | `float32` NCHW RGB, batch 1, 3 channels, values divided by 255 to `[0,1]`; no mean/std normalization. | Three pixel-unshuffle downsampling stages require height and width divisible by 8. The pinned test script does not pad arbitrary inputs. The network predicts a residual and adds the input; the test script clamps to `[0,1]`. |

No candidate has an exported graph, named ONNX input/output, selected opset,
external-data inventory, or tested padding adapter. Therefore actual ONNX
parity tolerances, operator support, and bundle bytes are **not applicable /
not measured**, not successful. Do not invent a fixed-shape or tiled export
contract from these upstream PyTorch behaviors.

## Quality, cost, and device results

| Gate | Result |
| --- | --- |
| ONNX export and PyTorch parity | Not run. No candidate passed the checkpoint-terms gate; PyTorch/ONNX export dependencies are also absent from this environment. |
| Quality against #26, #108, and #78 | Not run. Keep #26's noise reduction and #108's sharpening as the shipped model-free baselines. #78 and PR #129 record why the tested global Richardson–Lucy candidate was rejected; that evidence does not validate or invalidate these neural candidates. |
| Tiling, seams, and whole-image comparison | Not run. Overlap is not a parity guarantee: NAFNet GoPro uses local pooling dependent on image dimensions, while Restormer attention uses spatial statistics. |
| CPU and DirectML at 1, 12, and 24 MP | Not run; no model bundle exists to measure. No CPU, integrated-GPU, or discrete-GPU restoration run was executed. |
| Provider/operator placement and resource/cancellation behavior | Not run. No restoration model was loaded, and `torch`, `onnx`, `onnxruntime`, and `onnxruntime_directml` are not importable in the local Python environment. No provider/operator result is claimed. |
| ARM64 | Untested. No ARM64 hardware or packaged restoration runtime was evaluated. |

Costs below are planning bounds, not measurements. Existing estimates are about
6.8M parameters / 27 MB FP32 for NAFNet SIDD, 17.1M / 68 MB for NAFNet GoPro,
and 26.1M / 104 MB for Restormer. They exclude graph overhead and activations.
A single float32 RGB input (or output) is 12 MB per megapixel: 12, 144, and
288 MB at 1, 12, and 24 MP respectively. Input plus output alone doubles that
working-set estimate; model activations, temporary tensors, runtime buffers,
and GPU copies are additional and unmeasured.

## Reopening this gate

Do not host, bundle, or integrate any of these checkpoints until the exact
weight file has a content hash and written applicable terms covering the
intended use, conversion, and redistribution. After that gate passes, pin the
checkpoint and exporter environment, export outside the repository, record
the graph/opset/external-data hashes and actual bytes, and compare finite
outputs and exact shapes against the pinned PyTorch model using reviewed
max/mean-error tolerances. Then measure CPU and available DirectML on
documented 1/12/24 MP inputs, profile provider placement and process/GPU
memory, compare quality against the existing baselines, and compare any
bounded tile output/seams with whole-image inference. Report unsupported
providers/operators and untested hardware, including ARM64.

Until that evidence exists, keep downstream denoising, motion, and defocus
model integration blocked. Do not substitute another model or infer a pass
from the repository's MIT code licenses.

## Primary sources

- [NAFNet SIDD config and checkpoint link](https://github.com/megvii-research/NAFNet/blob/2b4af71ebe098a92a75910c233a3965a3e93ede4/options/test/SIDD/NAFNet-width32.yml)
- [NAFNet GoPro config and checkpoint link](https://github.com/megvii-research/NAFNet/blob/2b4af71ebe098a92a75910c233a3965a3e93ede4/options/test/GoPro/NAFNet-width32.yml)
- [NAFNet SIDD model documentation](https://github.com/megvii-research/NAFNet/blob/2b4af71ebe098a92a75910c233a3965a3e93ede4/docs/SIDD.md)
- [NAFNet GoPro model documentation](https://github.com/megvii-research/NAFNet/blob/2b4af71ebe098a92a75910c233a3965a3e93ede4/docs/GoPro.md)
- [NAFNet license](https://github.com/megvii-research/NAFNet/blob/2b4af71ebe098a92a75910c233a3965a3e93ede4/LICENSE)
- [NAFNet weight-license clarification request #194](https://github.com/megvii-research/NAFNet/issues/194)
- [Restormer single-image defocus config](https://github.com/swz30/Restormer/blob/68dc6ac472db26f16361150cb7a96a1bc87da93f/Defocus_Deblurring/Options/DefocusDeblur_Single_8bit_Restormer.yml)
- [Restormer single-image test script](https://github.com/swz30/Restormer/blob/68dc6ac472db26f16361150cb7a96a1bc87da93f/Defocus_Deblurring/test_single_image_defocus_deblur.py)
- [Restormer README](https://github.com/swz30/Restormer/blob/68dc6ac472db26f16361150cb7a96a1bc87da93f/README.md)
- [Restormer license](https://github.com/swz30/Restormer/blob/68dc6ac472db26f16361150cb7a96a1bc87da93f/LICENSE.md)
- [Model-free deblur experiment results in PR #129](https://github.com/dunclaw/PhotoLibrarian/pull/129)
