# Advanced editing feasibility

Research decision for [#90](https://github.com/dunclaw/PhotoLibrarian/issues/90),
2026-10-19. This is an implementation gate, not a claim that these features
are available. No restoration models, RAW decoder, new controls, or runtime
dependencies are added by this investigation. No candidate was benchmarked
in PhotoLibrarian; sizes below are planning estimates, not measured downloads
or performance results.

## Decision and implementation order

1. Implement the model-free noise-reduction baseline in
   [#26](https://github.com/dunclaw/PhotoLibrarian/issues/26), prototyping a
   bounded edge-aware filter before choosing its slider mapping. Do not call
   ordinary Gaussian blur detail-preserving noise reduction.
2. Use [#78](https://github.com/dunclaw/PhotoLibrarian/issues/78) for selectable
   sharpening and a bounded deconvolution experiment. Separate motion from
   defocus, and distinguish sharpening from actual blur inversion. Do not
   promise automatic recovery of arbitrary camera or subject motion.
3. Evaluate optional local restoration models only after establishing those
   baselines. Start with NAFNet's smaller, task-specific denoising and motion
   models; use Restormer as a quality/defocus comparator. Neither is selected
   for distribution until conversion, licensing, quality, and device gates
   below pass.
4. Track RAW development and SDR tone-mapped export as a separate follow-up,
   specified below. A slider on today's 8-bit decoder is not RAW-aware HDR.
   HDR display output and exposure-bracket merging are separate work again.

These recommendations let #26 and #78 proceed without depending on a large
model download or a replacement RAW pipeline. This document completes the
research deliverable, not the feature implementations in those issues.

## Existing pipeline and constraints

| Surface | Evidence and consequence |
| --- | --- |
| Decode | [`ImageEditRenderer.LoadOrientedAsync`](../src/PhotoLibrarian/Services/ImageEditRenderer.cs) requests premultiplied `Bgra8`, applies EXIF orientation, and uses `DoNotColorManage`. Precision and color interpretation must change before a RAW/HDR claim. Even a 16-bit TIFF is reduced to 8-bit here. |
| Preview/save | [`ImageEditorView`](../src/PhotoLibrarian/Views/ImageEditorView.xaml.cs) and `ImageEditRenderer` both call [`EditEffectGraph.Build`](../src/PhotoLibrarian/Services/EditEffectGraph.cs). Preview loads the full-resolution oriented bitmap and scales the graph output for display. Preserve this shared source-space processing; filtering a downscaled preview separately would change kernel behavior. |
| Current detail effects | The graph has `HighlightsAndShadowsEffect` clarity and `SharpenEffect` with a fixed threshold. It has no denoising, blur inversion, model inference, or RAW development. Existing sharpness must remain the default when adding modes. |
| Save | The renderer accepts JPEG, PNG, TIFF, and BMP extensions only, renders into a default `CanvasRenderTarget`, reads byte pixels, and transcodes over the source. PNG/TIFF preserve alpha; other outputs fill exposed rotation corners white. This is not a high-bit-depth export path. |
| Safety and recipes | [`ImageEditService`](../src/PhotoLibrarian.Core/Services/ImageEditService.cs) backs up before rendering and clears pending edit XMP after success. RAW export must not use this overwrite/clear flow for the RAW original. |
| State | [`EditParameters`](../src/PhotoLibrarian.Core/Models/EditParameters.cs), its serializer, editor history, and viewmodel all need to agree on any new amount/mode. Clone, reset, `HasAdjustments`, missing-XMP defaults, undo/redo, and saved/reopened state are part of each implementation, not just a slider. |
| Analysis | `AutoEnhanceAnalyzer` works on decoded byte pixels; it cannot infer recoverable RAW highlights from clipped RGB. Its unit tests do not establish GPU render parity. Current graph tests cover rotation extent, not pixel parity. |
| Local inference | `Directory.Packages.props` already pins `Microsoft.ML.OnnxRuntime.DirectML` at 1.24.4. Tagging model assets are opt-in and managed locally; see [MODEL-ASSETS.md](../MODEL-ASSETS.md). Reuse the asset-management pattern, not tagging tensors or classification preprocessing. |

### Input/output boundary

| Input | Available now | Proposed boundary |
| --- | --- | --- |
| `.jpg`, `.jpeg`, `.png`, `.tif`, `.tiff`, `.bmp` | Current editor render/save allow-list; WIC must decode the actual file. | First model-free effects target these formats, with unchanged output dimensions except existing rotation. RGB model inputs need a documented color conversion and separate alpha handling. |
| `.heic`, `.heif` | Library scanning recognizes these; current editor renderer does not allow saving them. | Do not expand support merely because a model accepts RGB. A tested decode/export policy is required. |
| `.cr2`, `.cr3`, `.nef`, `.arw`, `.dng`, `.orf`, `.rw2` | Library scanner recognizes these extensions. Recognition or an OS preview codec does not establish sensor-data editing. | RAW follow-up must report support by camera, compression, and pinned decoder version, not extension alone. Unsupported files must produce a visible reason, never a thumbnail presented as a RAW render. |
| Video, dual-pixel sensor pairs, exposure brackets | Not inputs to this still-image editing proposal. | Do not route these through a single-RGB-image restoration model. |

## Model-free detail processing

| Technique | Quality and limitations | Cost and recommendation |
| --- | --- | --- |
| Gaussian smoothing | Fast and deterministic, but removes edges and fine texture with the noise. | Existing Win2D effect, no new runtime/model assets. Useful comparison baseline only, not the recommended standalone noise slider. |
| Small bilateral filter | Spatial and intensity weighting can reduce flat-area noise while retaining edges. Strong settings erase texture; large kernels are expensive. RGB filtering can shift color; luminance/chroma treatment needs comparison. | Prototype a bounded source-space kernel through a Win2D custom pixel shader for #26. The built-in effect list has no bilateral denoiser. Budget shader compilation/packaging and GPU boundary tests; do not assume a ready-made Win2D API. |
| Non-local means / wavelet thresholding | Non-local means can retain repeated texture but is expensive and can smear unusual details. Wavelets offer multiscale denoising but require boundary and threshold design to avoid artifacts. | Additional implementation, buffers, and tuning; defer unless the bounded bilateral baseline fails visual review. Do not import an entire image-processing runtime for one slider without measurements. |
| Existing sharpen / explicit unsharp mask | Boosts edge contrast, including noise and halos; cannot recreate missing detail. An explicit Gaussian/unsharp composition can expose radius and threshold separately. | Existing `SharpenEffect` remains compatible default. Prototype a second explicit mode using Win2D blur/composition, with source-pixel radius and bounded overshoot. Denoise before sharpening. |
| Wiener / Richardson-Lucy deconvolution | Inverts an assumed blur kernel, not arbitrary blur. Wiener needs noise regularization; Richardson-Lucy needs iteration limits and noise/ringing control. A line kernel models uniform camera motion; a disk approximates uniform defocus. Moving subjects and depth-varying focus need different kernels/masks. | #78 should first test known-kernel fixtures and bounded iterations. GPU iteration requires intermediate targets and cancellation; FFT-based Wiener adds implementation/dependency cost. Do not relabel a sharpen slider as deblur or infer a universal kernel from it. |
| Multiscale local contrast | Separate broad illumination from fine detail, then apply bounded gains by scale/tonal range. Can increase halos, noise, and unnatural texture. | Existing clarity is the baseline. A future independent design needs multiple source-space buffers and tonal masks; not equivalent to RAW highlight recovery. No copying of ON1 implementation or UI. |

The intended RGB order for the first implementations is decode/orient,
denoise/deblur, existing tone/color controls, sharpening, then rotation.
New detail stages need explicit neutral bypasses. Existing edits with no new
properties must keep today's graph and appearance.

## Optional AI restoration

The upstream projects provide PyTorch implementations/checkpoints, not a
PhotoLibrarian-compatible ONNX bundle. The following are candidates, not
interchangeable modes of one model.

| Candidate | Intended inputs / evidence | Weight and runtime cost | Limits and disposition |
| --- | --- | --- | --- |
| NAFNet SIDD width32 | Developed RGB denoising; upstream reports SIDD results. Not a sensor-mosaic denoiser or arbitrary-noise guarantee. | Approximately 6.8M parameters: about 27 MB of FP32 parameters before graph/container overhead. Development needs a pinned PyTorch export; deployed app would use the existing ONNX runtime. | First AI denoising candidate. Compare texture retention, color noise, and JPEG artifacts against #26, not just upstream PSNR. |
| NAFNet GoPro width32 | Single RGB motion deblurring; upstream separately provides GoPro/REDS models. | Approximately 17.1M parameters: about 68 MB of FP32 parameters. Denoising and deblurring require separate weights; these costs add if both are installed. | First motion-model candidate, not proven defocus correction. GoPro benchmark motion is not evidence of reliable recovery for every camera/subject combination. Preserve upstream local/global-pooling behavior in conversion and tiling. |
| Restormer | Separate real/Gaussian denoising, motion, and single-image/dual-pixel defocus checkpoints. Only the single-image RGB variants fit the current editor. | Standard configuration is approximately 26.1M parameters, about 104 MB FP32 per model. Attention/intermediate activations and multiple task weights add substantial memory/storage. | Defocus/quality comparator, not default download. Channel attention depends on spatial statistics, so naive independent tiles need not match whole-image inference. |

Parameter estimates are rounded from the linked papers; FP32 estimates use
four bytes per parameter and decimal MB. They exclude temporary tensors,
runtime libraries, file overhead, caches, and any export external-data files.
Measure actual bundle bytes and peak memory after selecting a pinned export.
FP16 can reduce weight storage but needs operator/device and pixel-quality
validation. Do not assume INT8 classification results transfer to restoration.

At 24 megapixels, one BGRA8 frame is 96 MB and one float32 RGB frame is 288 MB.
Several copies plus network activations can exceed an integrated GPU budget
even when weights are small. Bounded tiles/halos, cancellation, serialized
inference, and bounded caches are required experiments, not proof of parity.
Overlap alone does not reproduce models with global statistics.

### Licensing and deployment gates

NAFNet's repository is MIT and includes BasicSR Apache-2.0 notices; Restormer's
repository is MIT. This establishes source-code terms, not an independently
verified redistribution grant for every linked checkpoint or dataset.
Before hosting or bundling weights, record the exact checkpoint source,
revision, hash, applicable weight terms, notices, training-data limitations,
and conversion provenance. Unresolved weight terms block distribution.
This investigation neither downloads nor redistributes weights.

Use explicit, user-initiated local asset import, checksum/shape/opset checks,
and a manifest with RGB order, range, transfer function, padding, task,
tensor contract, and model version. No runtime Python, CUDA requirement,
photo upload, cloud inference, or automatic model download is proposed.
Models consume straight RGB; retain alpha separately and avoid inventing
color from transparent pixels before restoring premultiplied output.

The existing DirectML package is reusable, but a successful tagging session
does not establish restoration operator support. Validate the exported
opset/operators against the pinned runtime on CPU and DirectML, including
x64/ARM64 packaging. DirectML requires sequential session execution, memory
patterns disabled, and no simultaneous `Run` calls on one session. Fixed
tile shapes may improve performance, but changes to pooling are algorithm
changes requiring new validation. Provider changes must be visible; a CPU
retry may use the same validated model only with an explicit status, not a
different algorithm disguised as success.

Missing, corrupt, incompatible, or unlicensed assets must disable the AI
action with a reason and setup guidance. Inference errors, nonfinite output,
and device loss must preserve the original and show failure. Never silently
substitute sharpening, Gaussian blur, or an unprocessed image under an AI
label. Reject stale asynchronous results after changing photo/settings.

## RAW-aware tone mapping

Recommend a separate **RAW development and SDR tone-mapped export** follow-up;
no existing open issue inspected for this investigation owns this pipeline.
Keep the RAW original immutable and create a separate JPEG/PNG/TIFF derivative
only through a validated export path. Retain the development recipe rather
than clearing it as if it were baked into the sensor data.

LibRaw is the practical decoder candidate: sensor samples, black/white levels,
white balance, color matrices, and camera-dependent geometry are available.
It is dual-licensed LGPL-2.1/CDDL-1.0 (choose one compliance path), not MIT.
Pin a stable version and build options, audit optional components and their
terms, ship notices, and budget native binaries/interop for x64 and ARM64.
Native bundle size must be measured from those builds. Upstream explicitly
does not promise a production-quality rendering pipeline; adopting its
decoder does not eliminate camera/color validation work.

The follow-up needs to:

1. Decode actual sensor data, apply black-level correction, documented
   white-balance/highlight handling and demosaic, and convert to a defined
   scene-linear RGB working space. LibRaw can emit 16-bit linear RGB with
   `output_bps=16` and linear gamma settings; disable automatic brightness
   for reproducible exposure. Avoid clipping valuable headroom before
   conversion to floating-point working buffers.
2. Use FP16/FP32 intermediate surfaces with explicit precision/color-space
   settings, not today's byte round trip. Distinguish real unsaturated sensor
   headroom from reconstructed color in clipped channels; fully saturated
   samples cannot be recovered reliably.
3. First compare a deterministic global luminance shoulder (Reinhard-style
   compression with explicit exposure/white point) with an edge-aware
   base/detail local operator. Global mapping is cheaper and avoids local
   halos; local mapping can retain contrast but risks halos and noise.
   Select only after highlight, gradient, color, and runtime comparisons.
4. Define an SDR output reference independent of the current monitor.
   Apply the same tone map/color conversion for preview and derivative save.
   Direct2D's HDR tone-map effect adapts luminance for displays; it does not
   decode RAW or supply missing sensor range. Any display-specific HDR
   transform must not leak into an SDR saved render.
5. Encode a color-tagged derivative with documented bit depth, orientation,
   alpha, metadata, and collision-safe output semantics. Test that reopening
   the result does not apply the recipe again. HDR display/export formats
   and exposure-bracket alignment/ghost removal are explicitly out of scope.

## Quality, parity, and performance acceptance gates

These are proposed gates for follow-up implementations, not tests executed
or performance guarantees in this research change.

| Area | Required evidence before enabling a feature |
| --- | --- |
| Identity / state | Zero strength is an exact bypass; legacy XMP keeps existing output. Test amount/mode validation, serializer round trip, clone/reset, `HasAdjustments`, undo/redo, and saved/reopened behavior. |
| Denoising | Seeded flat noise, color noise, edges, fine texture, small/odd dimensions, transparent borders, and clean controls. Measure flat-patch noise reduction alongside edge contrast and texture retention; review skin/hair/foliage/text at 100%. Reject improvements that merely blur both noise and detail. |
| Deblur / sharpen | Convolve known sharp fixtures with line and disk kernels, then compare recovery against sharpen and no-op baselines using PSNR/SSIM plus edge overshoot/ringing. Include noise, JPEG blocks, mismatched kernels, independent moving subjects, and irrecoverably blurred inputs. Report failures rather than claiming universal correction. |
| Analysis | If automatic noise/kernel/tone estimates are added, test deterministic results, finite bounded outputs, empty/malformed input rejection, and distinct behavior for flat, textured, clipped, and high-noise fixtures. Do not treat texture as noise automatically. |
| Render parity | Off-screen render the same full-resolution oriented source, immutable recipe, color policy, and model version through the preview path and save path. Before encoding, require exact BGRA8 equality on the same device for deterministic model-free effects; for floating-point/provider comparisons, record max/mean channel errors with a reviewed explicit tolerance. A shared graph or matching parameter values alone is not evidence. |
| Encoding / geometry | Use PNG for lossless saved-pixel comparisons; set format-specific JPEG tolerances separately. Cover EXIF orientation, portrait/high-DPI images, rotation extent/corners, alpha, and 16-bit source downconversion. Render at source resolution before viewport scaling; include a 1:1 inspection mode in visual review. |
| Models / tiling | Compare converted outputs against pinned upstream outputs and whole-image against tiled output, including seams and padding. Check every output for expected shape and finite values. Test missing/corrupt assets, unsupported operators, provider failure, cancellation, and stale results. Do not change tile layout silently between preview and save. |
| RAW | Camera-specific fixtures with real unclipped highlight headroom, clipped channels, neutral ramps, color charts, Bayer/X-Trans as supported, orientation/crop metadata, and malformed/unsupported files. Verify monotonic tone mapping, no NaN/Inf, no premature clipping, derivative parity, and byte-identical RAW originals. |
| Performance | Record hardware/OS/runtime/provider, bundle size, cold/warm latency, peak process/GPU memory, and cancellation latency at 1 MP, 12 MP, and 24 MP. Compare CPU, integrated GPU, and discrete GPU; check packaged ARM64 separately. Choose a measured preview budget and maximum image/tile sizes before exposing controls. All heavy work must remain off the UI thread. |

## Primary references

Upstream source snapshots below make the architecture and license review
repeatable. Checkpoint hashes and export artifacts remain unselected.

- [NAFNet source, configurations, and task-specific checkpoint links](https://github.com/megvii-research/NAFNet/tree/2b4af71ebe098a92a75910c233a3965a3e93ede4),
  [license](https://github.com/megvii-research/NAFNet/blob/2b4af71ebe098a92a75910c233a3965a3e93ede4/LICENSE),
  and [Simple Baselines for Image Restoration](https://arxiv.org/abs/2204.04676).
- [Restormer source and task-specific checkpoint links](https://github.com/swz30/Restormer/tree/68dc6ac472db26f16361150cb7a96a1bc87da93f),
  [license](https://github.com/swz30/Restormer/blob/68dc6ac472db26f16361150cb7a96a1bc87da93f/LICENSE.md),
  and [Restormer paper](https://arxiv.org/abs/2111.09881).
- [Direct2D built-in effects](https://learn.microsoft.com/en-us/windows/win32/direct2d/built-in-effects),
  [sharpen](https://learn.microsoft.com/en-us/windows/win32/direct2d/sharpen-effect),
  and [HDR tone mapping and color-management sequence](https://learn.microsoft.com/en-us/windows/win32/direct2d/hdr-tone-map-effect).
- [ONNX Runtime DirectML requirements and session restrictions](https://onnxruntime.ai/docs/execution-providers/DirectML-ExecutionProvider.html).
  The current documentation notes sustained engineering and points new work
  toward WinML; migrating the app's inference runtime is not part of #90.
- [LibRaw scope and dual licensing](https://www.libraw.org/about),
  [processing parameters](https://www.libraw.org/docs/API-datastruct.html#libraw_output_params_t),
  and [camera support](https://www.libraw.org/supported-cameras).
