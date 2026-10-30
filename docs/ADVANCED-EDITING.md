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
2. Add the model-free selectable sharpening baseline in
   [#108](https://github.com/dunclaw/PhotoLibrarian/issues/108): keep the
   existing `SharpenEffect` as the old-edit default and offer an explicit,
   bounded source-pixel unsharp mask. Use
   [#78](https://github.com/dunclaw/PhotoLibrarian/issues/78) for a separate
   bounded deconvolution experiment. Distinguish sharpening from blur
   inversion; do not promise automatic recovery of arbitrary camera or
   subject motion.
3. Evaluate optional local restoration models only after establishing those
   baselines. Start with NAFNet's smaller, task-specific denoising and motion
   models; use Restormer as a quality/defocus comparator. Neither is selected
   for distribution until conversion, licensing, quality, and device gates
   below pass.
4. Track RAW development and SDR tone-mapped export as a separate follow-up,
   specified below. A slider on today's 8-bit decoder is not RAW-aware HDR.
   HDR display output and exposure-bracket merging are separate work again.

These recommendations let #26, #108, and #78 proceed without depending on a
large model download or a replacement RAW pipeline. This document records the
research and implementation evidence; it does not claim deconvolution or RAW
development is available.

## Existing pipeline and constraints

| Surface | Evidence and consequence |
| --- | --- |
| Decode | [`ImageEditRenderer.LoadOrientedAsync`](../src/PhotoLibrarian/Services/ImageEditRenderer.cs) requests premultiplied `Bgra8`, applies EXIF orientation, and uses `DoNotColorManage`. Precision and color interpretation must change before a RAW/HDR claim. Even a 16-bit TIFF is reduced to 8-bit here. |
| Preview/save | [`ImageEditorView`](../src/PhotoLibrarian/Views/ImageEditorView.xaml.cs) and `ImageEditRenderer` both call [`EditEffectGraph.Build`](../src/PhotoLibrarian/Services/EditEffectGraph.cs). Preview loads the full-resolution oriented bitmap and scales the graph output for display. Preserve this shared source-space processing; filtering a downscaled preview separately would change kernel behavior. |
| Current detail effects | The graph has `HighlightsAndShadowsEffect` clarity, edge-aware noise reduction, and selectable sharpening: the existing `SharpenEffect` remains the default for old XMP, with a source-pixel unsharp-mask alternative. It has no blur inversion, model inference, or RAW development. |
| Save | The renderer accepts JPEG, PNG, TIFF, and BMP extensions only, renders into a default `CanvasRenderTarget`, reads byte pixels, and transcodes over the source. PNG/TIFF preserve alpha; other outputs fill exposed rotation corners white. This is not a high-bit-depth export path. |
| Safety and recipes | [`ImageEditService`](../src/PhotoLibrarian.Core/Services/ImageEditService.cs) backs up before rendering and clears pending edit XMP after success. RAW export must not use this overwrite/clear flow for the RAW original. |
| State | [`EditParameters`](../src/PhotoLibrarian.Core/Models/EditParameters.cs), its serializer, editor history, and viewmodel all need to agree on any new amount/mode. Clone, reset, `HasAdjustments`, missing-XMP defaults, undo/redo, and saved/reopened state are part of each implementation, not just a slider. |
| Analysis | `AutoEnhanceAnalyzer` works on decoded byte pixels; it cannot infer recoverable RAW highlights from clipped RGB. Its unit tests do not establish GPU render parity. Source-resolution preview/save parity is exercised by the off-screen graph tests. |
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
| Existing sharpen / explicit unsharp mask | Boosts edge contrast, including noise and halos; cannot recreate missing detail. An explicit Gaussian/unsharp composition can expose radius and threshold separately. | Implemented in [#108](https://github.com/dunclaw/PhotoLibrarian/issues/108): existing `SharpenEffect` remains the compatible default; the alternative uses a source-pixel Win2D Gaussian blur plus a thresholded shader with output clamped to the normalized range. Denoise precedes sharpening. |
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

These are proposed gates for follow-up implementations, not performance
guarantees. The source-resolution render-parity harness now lives in
`src/PhotoLibrarian.Tests` and provides generated flat-noise, color-noise,
edge, fine-texture, gradient, and known 3×3-box-blurred inputs without user
photos or external assets.

On Windows, run the generated fixture and source-resolution parity suites with:

```powershell
dotnet run --project src\PhotoLibrarian.Tests\PhotoLibrarian.Tests.csproj -c Release -p:Platform=x64 -- -class PhotoLibrarian.Tests.RenderQualityFixtureTests -class PhotoLibrarian.Tests.ImageRenderParityTests -class PhotoLibrarian.Tests.UnsharpMaskTests -class PhotoLibrarian.Tests.ImageEditorHistoryTests -class PhotoLibrarian.Tests.NoiseReductionTests
```

`ImageRenderParityTests` requires Win2D and a working Windows graphics device;
device initialization errors fail the run rather than being reported as a
skip or parity success. It compares exact BGRA8 pixels from the production
preview draw pass and the save renderer before encoding, covering neutral,
tone, detail, both sharpening modes, denoise-before-sharpen, and rotation
recipes; EXIF orientation; a portrait 300-DPI
source; small/odd dimensions; alpha; and transparent PNG versus white JPEG
rotation corners. The focused suite passes with no skips, including 44 exact
preview/save pixel comparisons across the six fixtures and geometry cases.
Additional unsharp-mask tests cover bounded parameters/output, neutral
bypass, edge contrast, low-amplitude noise suppression, small/odd alpha
images, legacy XMP defaults, and history/reset/save state. A separate PNG
save/reopen test checks encoded
dimensions, orientation reset, exact alpha, and RGB within one BGRA8 value
for premultiplication roundoff. The suite intentionally does not use JPEG
equality as an encoding-parity check. Pure generated-fixture checks are in
the separate `RenderQualityFixtureTests` class.

### #108 source-pixel sharpening evidence

`SharpeningMode` defaults to `Legacy`, including when older XMP has no mode
property; that path retains the existing `SharpenEffect` amount mapping and
threshold. The editor presents a single **Sharpening** block with an **Amount**
slider by default. Checking **Advanced** switches to the unsharp-mask method
and replaces that slider with **Amount**, **Radius (pixels)**, and **Noise
threshold**. Unchecking it returns to the original method; each method retains
its own settings, including through undo/redo and pending XMP edits.
`UnsharpMask` uses the existing source-resolution graph for both
preview and save. Its amount is bounded to 0–2, its Gaussian blur amount is
0.5–5 source pixels, and its per-channel noise threshold is 0–0.25 in
normalized channel units. The shader clamps output to [0, 1] and preserves
source alpha. Zero amount returns the input image without adding a render
effect. The UI describes the feature as edge-contrast enhancement, not lost
detail recovery.

On Windows, collect source-resolution timing with:

```powershell
dotnet run --project src\PhotoLibrarian.Tests\PhotoLibrarian.Tests.csproj -c Release -p:Platform=x64 -- -class PhotoLibrarian.Tests.UnsharpMaskPerformanceTests
```

Warm draw-plus-readback medians captured 2026-10-23 on Windows 11 Pro
10.0.26300, .NET 8.0.32, Win2D 1.4.0, AMD Ryzen 9 9950X3D, and a system with
NVIDIA GeForce RTX 5080 (driver 32.0.16.1088) plus AMD Radeon integrated
graphics (driver 32.0.21042.62), using `CanvasDevice.GetSharedDevice()`:

| Source pixels | Median render |
| ---: | ---: |
| 1024 × 1024 (1.05 MP) | 1.6 ms |
| 4000 × 3000 (12 MP) | 15.0 ms |
| 6000 × 4000 (24 MP) | 41.7 ms |

Each case creates/uploads the source before timing, warms the graph once, then
times three full-resolution draws followed by pixel readback; the table
reports the median. It does not include decoding, encoding, target/graph
construction, peak memory, or a comparison across adapters, so these numbers
are a focused render-cost observation rather than a whole-save latency
guarantee.

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
