# Advanced editing feasibility

Research decision for [#90](https://github.com/dunclaw/PhotoLibrarian/issues/90),
2026-10-19, with the [#119](https://github.com/dunclaw/PhotoLibrarian/issues/119)
model-free comparison updated 2026-10-30. This is an implementation gate, not
a claim that advanced restoration features are available. The separate
pinned RAW decoder foundation in
[#112](https://github.com/dunclaw/PhotoLibrarian/issues/112) does not enable RAW
export. The explicitly experimental SDR preview in
[#113](https://github.com/dunclaw/PhotoLibrarian/issues/113) is documented below.
No restoration models or editor controls are added by the restoration
investigation. The wavelet and non-local-means candidates were evaluated
only in test fixtures; neither passed the initial detail-retention comparison.
No restoration candidate was benchmarked in PhotoLibrarian; sizes below are
planning estimates, not measured downloads or performance results. The later
bounded model-free deblur experiment and its no-ship decision are recorded in
[DEBLUR-EXPERIMENTS.md](DEBLUR-EXPERIMENTS.md).

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
large model download or a replacement raster pipeline. This document records
the research and implementation evidence; it does not claim deconvolution,
production camera coverage, or an enabled RAW editor is available.

## Existing pipeline and constraints

| Surface | Evidence and consequence |
| --- | --- |
| Decode | [`ImageEditRenderer.LoadOrientedAsync`](../src/PhotoLibrarian/Services/ImageEditRenderer.cs) requests premultiplied `Bgra8`, applies EXIF orientation, and uses `DoNotColorManage`. Even a 16-bit TIFF is reduced to 8-bit here. It rejects recognized RAW inputs. The separate [`RawDevelopmentService`](../src/PhotoLibrarian.Core/Services/RawDevelopmentService.cs) feeds the FP32 RAW preview, never this raster decoder. |
| Preview/save | [`ImageEditorView`](../src/PhotoLibrarian/Views/ImageEditorView.xaml.cs) and `ImageEditRenderer` both call [`EditEffectGraph.Build`](../src/PhotoLibrarian/Services/EditEffectGraph.cs). Preview loads the full-resolution oriented bitmap and scales the graph output for display. Preserve this shared source-space processing; filtering a downscaled preview separately would change kernel behavior. |
| Current detail effects | The graph has `HighlightsAndShadowsEffect` clarity, edge-aware noise reduction, and selectable sharpening: the existing `SharpenEffect` remains the default for old XMP, with a source-pixel unsharp-mask alternative. It has no blur inversion, model inference, or RAW editor integration. |
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

### #119 model-free denoising comparison: no-go

The advanced denoiser remains blocked. The existing bilateral filter is
unchanged and remains the only user-facing denoise option. A test-only
overlapping 2×2 Haar soft-threshold shader and a CPU reference implementation
of non-local means (5×5 search, 3×3 patches) were compared with the bilateral
shader. Neither prototype is included in the application pipeline or package.
The experimental wavelet shader adds no runtime package or model asset; the
test project compiles it with the installed Windows SDK `fxc.exe`. No
third-party algorithm implementation or image asset was added.

The reproducible 96×72 fixture uses seed 119 and independently adds uniform
integer RGB noise in [-22, 22] to three clean bands: a flat neutral patch, a
repeating two-color fabric-like pattern, and a 3×2 checker-like fine pattern.
The flat band's input MSE is 165.382 (byte-value squared). For each candidate
setting, the baseline slider was swept in 0.01 increments and the closest
flat-band MSE was selected. A candidate only advances this screen if it
reduces flat-band noise and produces lower clean-reference MSE in both
texture bands at the matched noise-removal level; a lower MSE from simply
removing more noise does not count as a win.

| Candidate | Candidate setting | Matched bilateral | Flat-band MSE (candidate / baseline) | Texture-band MSE (candidate / baseline) |
| --- | ---: | ---: | ---: | ---: |
| Overlapping Haar threshold | 0.6 | 0.49 | 74.811 / 74.807 | 133.900 / 93.055 |
| Overlapping Haar threshold | 0.8 | 0.59 | 56.431 / 56.476 | 133.422 / 75.501 |
| Overlapping Haar threshold | 1.0 | 0.67 | 43.615 / 43.822 | 138.827 / 63.853 |
| Non-local means | 2 | 0.82 | 25.349 / 25.758 | 119.653 / 48.336 |
| Non-local means | 5 | 0.89 | 19.617 / 19.575 | 112.568 / 44.483 |
| Non-local means | 8 | 0.96 | 15.249 / 15.082 | 107.064 / 43.548 |

Texture-band MSE is averaged across the two patterned bands and all RGB
channels. The results are an early quantitative screen, not proof that the
bilateral filter is better on photographs: both tested patterns are repeated
synthetic textures, and no real-photo crops, JPEG artifacts, luminance-only
or chroma-only noise cases, visual 100% reviews, or perceptual metrics were
available. In this fixture, however, both candidates have substantially
higher reconstruction error than the baseline at matched flat-patch noise
reduction. Neither meets the evidence gate, so no advanced mode/control is
added and the baseline is not relabeled.
At NLM setting 8, the matched pattern-band MSE is 47.438 versus 54.845
(better), but the fine-pattern band is 166.691 versus 32.252 (substantially
worse); at wavelet setting 0.8, the corresponding errors are 176.725 versus
76.251 and 90.118 versus 74.751. This is why the gate checks both bands, not
only one favored texture.

The source-resolution benchmark uses Windows 11 Pro x64, .NET 8.0.33, and an
AMD Ryzen 9 9950X3D. The system has an NVIDIA GeForce RTX 5080 (driver
32.0.16.1088) and AMD Radeon integrated graphics (driver 32.0.21042.62); the
specific adapter selected by Win2D was not recorded. Each case warms the
graph once, then reports the median of three draw-plus-pixel-readback passes;
decoding, device/graph/target construction, and encoding are excluded.

| Source size | Wavelet, setting 0.8 | Bilateral, matched setting | Process working set after render |
| ---: | ---: | ---: | ---: |
| 1024 × 1024 (1.05 MP) | 1.6 ms | 1.6 ms (0.49) | 118 / 120 MB |
| 4000 × 3000 (12 MP) | 13.2 ms | 12.8 ms (0.59) | 313 / 313 MB |
| 6000 × 4000 (24 MP) | 38.5 ms | 29.5 ms (0.67) | 606 / 606 MB |

Working set is the test process measurement after each run; it is neither
peak process allocation nor GPU memory, and does not establish a memory
advantage. The wavelet prototype was slower at 12 and 24 MP and offered no
quality improvement on this fixture. Cold latency, cancellation,
integrated-GPU comparison, ARM64, and large-image non-local-means performance
remain unmeasured. NLM's test-only reference evaluates 25 search candidates
against 3×3 patches (225 pixel-pair comparisons per output pixel), so it was
not promoted to a full-resolution renderer without a bounded GPU
implementation and further profiling.

The experiment is repeatable on Windows with:

```powershell
dotnet run --project src\PhotoLibrarian.Tests\PhotoLibrarian.Tests.csproj -c Release -p:Platform=x64 -- -class PhotoLibrarian.Tests.WaveletNoiseReductionExperimentTests -class PhotoLibrarian.Tests.WaveletNoiseReductionPerformanceTests -reporter verbose -showLiveOutput
```

Generated fixtures avoid uploading user photos. A future decision requires
permitted real-photo crops spanning low-light color noise, hair/foliage/fabric/
fine text, smooth gradients, clean controls, and compression artifacts, plus
matched-strength quantitative and visual review. This no-go is limited to the
two tested prototypes and the evidence currently available; it is not a claim
that model-free denoising cannot improve on the bilateral baseline.

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

The evaluation of the three bounded checkpoints found no candidate with
verified weight terms, and did not export or benchmark them. See the
[candidate-by-candidate gate and upstream contracts](RESTORATION-MODEL-GATE.md)
before reopening downstream model integration.

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

The separate **RAW development and SDR tone-mapped export** sequence now has
a pinned local decoder (#112), an explicitly experimental SDR tone map (#113),
and safe, 8-bit SDR JPEG/PNG derivative export (#114). Production camera/color
validation and raster-save expansion remain separate work.
Keep the RAW original immutable; **Export SDR derivative** creates a separate
photo and retains the development recipe rather than clearing it as if it
were baked into sensor data. TIFF and HDR export are not supported.

### Pinned decoder foundation (#112)

The managed [`RawDevelopmentService`](../src/PhotoLibrarian.Core/Services/RawDevelopmentService.cs)
calls an app-owned C ABI DLL that statically links LibRaw 0.22.3 and zlib.
The source archive is pinned by SHA-512
`a9453f8cff55302926c11c47ba7c8d04d81023bbbcad145551d2c0cc601de7eea8218d00a073e6b9ece845d3420897a2272a34fabf55f5b821183eaa7f66b3ef`;
the build helper is pinned to commit
`eb98e4325aef2ce85d2eb031c2ff18640ca616d3` by SHA-512
`63e68a4d30286ec3aa97168d46b7a1199268099ae27b61abcc92e93ec30e48d364086227983a1d724415e5f4da44d905422f30192453b95f31040e5f8469c3f9`.
vcpkg is pinned to commit `d186940b2f0c4e8e5b4ff804af57d2a26fbfa091`; its
2026-09-26 executable is SHA-256-verified as
`2a4ae1467b9e797964c60c919d057b9e5dc1ec03f4b9632c1150026db25201f2`.
zlib is 1.3.2. The measured builds used Visual Studio 2026 18.11.1 and MSVC
14.51.36231. These build-time tools and source downloads require network
access when not cached, but runtime decoding is local and has no separate
decoder installation or cloud dependency.

The selected LibRaw license path is CDDL-1.0. The build ships the exact LibRaw
source archive and its CDDL text, upstream copyright, zlib license, and
embedded DCB/FBDD BSD, X3F BSD, and Adobe DNG SDK MIT notices with the
application output and publish files. The app-owned DLL is statically linked;
LibRaw modifications are not carried as a binary-only patch. OpenMP, LCMS,
Jasper/RedCine, DNG lossy JPEG, RawSpeed, debug output, X3F support, examples,
and Raspberry Pi support are disabled; zlib-backed DNG deflate remains
enabled. The build log reports those compiled features; the LibRaw CMake
helper and vcpkg are not included in the application.

The decoder contract is row-major, three-channel float32 **linear ProPhoto
RGB**, developed from sensor samples, not embedded previews. Its precise white
point is **LibRaw ProPhoto D65**, not standard ICC ProPhoto D50:
`output_color=4` is named "ProPhoto D65" in the pinned
`src/postprocessing/postprocessing_utils_dcrdefs.cpp`; its forward matrix is
in `src/tables/colorconst.cpp`. The source archive shipped with the build
makes that contract inspectable without installing a decoder.
LibRaw is asked
for 16-bit RGB, linear gamma, camera white balance/matrix, AHD demosaic,
disabled auto-brightness/maximum adjustment, and a fixed 0.25 reference
exposure that is inverted into float output. Black/white levels, camera
identity, sensor and active-area dimensions, EXIF orientation, and the
reference exposure are returned separately. ActiveArea crop and orientation
are applied by LibRaw before the pixels are returned. The original file is
read-only. There is no application-imposed megapixel, input-file-size, or
RAW-memory cap. LibRaw opens the Unicode file path directly while the managed
service holds a read-only file lock; it no longer copies/pins the entire input
in a managed byte array. LibRaw's configurable raw-memory budget is set to its
maximum representable value, retaining its allocation and corrupt-file checks.
Work runs off the UI thread, supports cancellation, owns the native
buffer through a safe handle, and reports unsupported/corrupt input as errors
without thumbnail fallback.

Build the native decoder for x64, ARM64, or the app's existing x86 target with
`tools\Build-RawDecoder.ps1 -Architecture <x64|ARM64|x86> -OutputDirectory <path>`.
The managed decoder is covered by:

```powershell
dotnet run --project src\PhotoLibrarian.Tests\PhotoLibrarian.Tests.csproj -c Release -p:Platform=x64 -- -class PhotoLibrarian.Tests.RawDevelopmentTests
```

The synthetic DNG fixtures cover Bayer and X-Trans, active-area crop,
orientation 1 and 6, deterministic repeated output, finite float values,
headroom above 1.0, immutable input bytes, unsupported/corrupt input, a
valid input larger than the former 512-MiB limit, Unicode paths, empty-file
errors, and pre-cancelled work. The X-Trans fixture is a
synthetic 528 × 528 DNG, sized for LibRaw's 512-pixel demosaic tile; it is not
a camera sample. One isolated x64 synthetic Bayer benchmark at 1024 × 768
(1,573,314 input bytes) measured 46.8 ms and 16.6 MiB sampled process
working-set growth. This is a single local run, not a latency or
peak-allocation guarantee. The Release DLL sizes measured in this build were
1,318,400 bytes x64, 1,255,424 bytes ARM64, and 1,175,040 bytes x86.
Real-camera coverage, camera-specific color accuracy, peak memory,
cancellation latency, and broader performance remain unverified. Only the
explicitly experimental development described below is enabled; derivative
export does not remove the missing production camera-accuracy measurements.

### #113 experimental SDR reference preview

**Entry and safety.** Open a recognized RAW through **Adjust**. The editor
shows RAW-only exposure/white-point controls and
**Keep preview recipe**. Raster tone/color/detail/rotation controls and auto
enhance are hidden/disabled; their pending recipe values (including crop)
are retained, not interpreted as scene-linear controls. **Reset** resets only
the RAW controls in this workflow. Undo/redo, clone, dirty state, missing-XMP
defaults, reset, and reopen cover both RAW fields.
**Save is disabled with a reason**: RAW originals cannot be overwritten.
Use **Export SDR derivative** for a separate JPEG/PNG (see #114 below).
The viewmodel and `ImageEditService.ApplyEditsAsync` reject RAW before calling
any supplied overwrite renderer or backup/clear flow; the raster decoder and
renderer reject RAW too. Revert cannot overwrite a RAW original.

Keeping the preview recipe writes the sibling `.xmp`, not the sensor file,
and retains other XMP properties. Existing readable XMP is replaced atomically
with the previous packet backed up as `.xmp.<unique-id>.bak` without replacing
earlier backups. Unreadable XMP, malformed
RAW parameters, and unknown tone-map versions report errors rather than
silently substituting a default recipe. The sidecar uses the existing
same-basename convention, so files sharing a stem share an XMP packet.
Raster baking clears only raster fields, retaining the RAW fields in a
shared same-stem packet.
Close does not automatically keep a recipe. No derivative or sensor bytes
are written by preview rendering.

**Precision and color contract.** `RawToneMappingService` consumes the full
oriented, cropped, camera-white-balanced float32 linear LibRaw ProPhoto D65
buffer. Its intermediates/output are CPU FP32 RGB surfaces with double scalar
arithmetic; FP16/GPU tone-map surfaces are deliberately not used. Exposure
is `2^RawExposureEv`, bounded to -8 through +8 stops, default zero.
`RawWhitePoint` is exposed scene luminance mapped to SDR white, bounded to
1 through 16, default 4. The scene luminance weights
`(0.2683649469, 0.7151089340, 0.0165256105)` are derived from the inverse
of the pinned LibRaw forward matrix and the sRGB D65 luminance weights.
For positive exposed luminance `L`, the versioned `GlobalLuminanceV1`
shoulder is `min(1, L * (1 + L / W^2) / (1 + L))`.
It is increasing up to `W`, then constant at SDR white.
It scales RGB by mapped/source luminance, converts using the inverse
LibRaw matrix, and clips out-of-sRGB channels only at the output boundary.
It does **not** perform another D50-to-D65 adaptation. Signed finite inputs
with nonpositive luminance map to black; nonfinite samples and invalid
geometry/parameters are explicit errors. Extreme finite FP32 inputs stay
finite/bounded. Neutral tolerances of `1e-5` linear accommodate the rounding
of LibRaw's six-decimal forward matrix; independently generated forward-matrix
sRGB patches round-trip through the shoulder within `1e-6`.

`RawSdrImage.LinearRgb` is a reusable, source-resolution, oriented, opaque
FP32 **linear sRGB D65** output for #114. `ToPreviewBgra8` applies the fixed
IEC 61966-2-1 sRGB transfer and rounds to 8-bit only for the final Win2D
preview upload. The production RAW draw pass scales that completed SDR image
for the viewport without running the raster effects. Mapping has no current
monitor/profile/HDR-effect input; display color adaptation is not implemented
or part of the recipe. This is a reproducible SDR reference, not calibrated
appearance on every monitor.

The decoder still uses 16-bit LibRaw development, with a fixed 0.25 reference
scale inverted into float32. Its finite per-channel ceiling is near 4 and its
quantization interval near `4/65535`; converting that output to FP32 does not
create lost precision or unlimited scene range. The native color conversion
and scaling also have 16-bit clamps. This stage preserves **available**
developed values above 1 until the intended shoulder, but cannot undo earlier
decoder/sensor clipping, reconstruct fully saturated samples, or establish
camera-specific highlight/color fidelity. Lower exposure can reveal distinct
unsaturated values; identical clipped values remain identical.

**Operator decision.** A test-only alternative uses a 7x7 bilateral base in
log luminance (spatial sigma 2 source pixels, range sigma 0.75 natural-log
units), the same shoulder on the base, unit-gain log detail, and the same
output conversion/clipping. It is not exposed as a second mode. Generated
128x64 fixtures were measured on 2026-10-31; ranges and deviations below are
linear output RGB, not perceptual/real-camera quality scores.

| Fixture / measurement | Global | Local base/detail |
| --- | ---: | ---: |
| Unclipped 1/2/4 highlight patch range | 0.468752 | 0.516678 |
| Ramp output range | 1.000000 | 1.000000 |
| Color-chart output range | 0.396718 | 0.397980 |
| Fine-texture standard deviation | 0.022087 | 0.025574 |
| Seeded shadow-noise standard deviation | 0.001131 | 0.001141 |
| Dark edge near/far deviation (halo) | 0.000000 | 0.003470 |
| Edge fixture mapping latency | 0.140 ms | 2.102 ms |

Local/global linear RGB RMS differences were 0.012851 (highlights),
0.000292 (ramp), 0.002209 (color chart), 0.003575 (texture), 0.000010
(shadows), and 0.002310 (edge). The local candidate increased texture contrast
but also shadow-noise gain and a dark-side halo; its increased highlight range
includes neighborhood effects, not newly recovered sensor detail.
The global shoulder is selected for boundedness, cheaper cost, spatially
independent mapping, and no local halo. These are generated-fixture comparisons,
not a claim that all local tone mappers are inferior or that real-camera
quality is validated. First-fixture timings include JIT/scheduling overhead;
small timings are single observations, not performance guarantees.

**Cost and cancellation.** One isolated serial Release x64 run on Windows
build 26300 (26H2), AMD Ryzen 9 9950X3D, approximately 61.6 GiB RAM, .NET
8.0.33 / SDK 10.0.402 measured:

| Source resolution | FP32 map | sRGB BGRA8 encoding | Sampled peak process working set | Growth during case | Live frame payload |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1024x768 | 12.4 ms | 59.2 ms | 84.7 MiB | 21.7 MiB | 21.0 MiB |
| 4000x3000 | 162.6 ms | 884.6 ms | 383.4 MiB | 298.7 MiB | 320.4 MiB |
| 6000x4000 | 317.4 ms | 1774.8 ms | 1024.6 MiB | 641.2 MiB | 640.9 MiB |

Sampling is every 2 ms and can miss instantaneous peaks; these are process
working-set observations, not exact peak allocations. The serial process
retains allocator/earlier-test memory. Payload counts input/output RGB arrays
and encoded BGRA8, excluding native decoder buffers, file input, Win2D upload,
GPU allocations, and full editor overhead. Mapping is CPU-only; these are not
whole-photo decode-to-screen times. x64 and ARM64 Release builds pass, but
ARM64 runtime, integrated/discrete GPU memory, camera samples, and interactive
visual review of RAW controls are unverified.

There is no megapixel cutoff in the native sensor/developed geometry checks,
tone mapping, preview, or export. Buffer geometry must still be valid; actual
runtime array/address-space, codec and GPU texture limits and available memory
remain physical constraints, not a replacement camera-resolution policy.
Allocation failures report actionable errors rather than claiming that the
camera exceeds a preset megapixel budget. The current pipeline still uses
full-resolution buffers; removing the artificial cap is not a guarantee of
constant memory, tiled processing, or compatibility with every camera.
Preview requests debounce
for 80 ms, reuse one developed source per photo, serialize decode/map work,
cancel superseded work, and check cancellation before publishing a result.
CPU map/encode checks every 4096 pixels. Closing/unloading or changing photos
invalidates the source and cancels work; even a decoder that ignores its token
cannot publish a stale result. All CPU image processing runs off the UI thread.
At 24 MP the measured map-plus-encode is about 2.1 seconds and memory is
substantial; responsiveness/native cancellation latency on lower-memory
devices remains a validation limitation, not a claimed budget.

Reproduce the focused suites (serial execution avoids existing tests disposing
the shared Win2D device during another graphics test):

```powershell
dotnet run --project src\PhotoLibrarian.Tests\PhotoLibrarian.Tests.csproj -c Release -p:Platform=x64 -- -class PhotoLibrarian.Tests.RawToneMappingTests -class PhotoLibrarian.Tests.RawPreviewWorkflowTests -class PhotoLibrarian.Tests.RawDevelopmentTests -class PhotoLibrarian.Tests.ImageEditorHistoryTests -class PhotoLibrarian.Tests.ImageRenderParityTests -class PhotoLibrarian.Tests.RenderQualityFixtureTests -class PhotoLibrarian.Tests.UnsharpMaskTests -class PhotoLibrarian.Tests.NoiseReductionTests -parallelMode none
dotnet run --no-build --project src\PhotoLibrarian.Tests\PhotoLibrarian.Tests.csproj -c Release -p:Platform=x64 -- -method PhotoLibrarian.Tests.RawToneMappingTests.ReportsFullResolutionLatencyAndSampledPeakWorkingSet -method PhotoLibrarian.Tests.RawToneMappingTests.CompareGlobalWithEdgeAwareLogBaseDetailOnGeneratedAnalysisFixtures -parallelMode none
```

The focused run passes 79 tests with no skips: scalar/matrix contracts,
clipped/unclipped highlights, ramps/charts/texture/shadows, malformed/extreme
inputs, deterministic source-size output, active cancellation and stale decode,
Bayer/X-Trans geometry, byte-identical originals, recipe history/sidecar safety,
overwrite rejection, actual Win2D RAW upload/draw/viewport scaling, and
unchanged raster pixels with dormant RAW fields. #114's derivative
save/reopen evidence is documented separately below.

### #114 safe SDR derivative export

**Entry and output.** In the experimental RAW editor, choose **Export SDR
derivative...**, an existing destination folder, and PNG (lossless) or JPEG
(quality 95, 4:4:4 chroma, lossy). This is one full-resolution photo, not a
general batch exporter. Default naming is `<source> - Developed.png/.jpg`;
collisions add ` (2)`, ` (3)`, etc. Existing files/directories and same-stem
sidecars or photos are skipped. The original RAW is never opened for writing.
Raster Save continues its existing backup/render/clear behavior; export does
not call that path.

The export snapshots the current recipe, reuses the editor's developed
source, and invokes the same `RawToneMappingService` and
`RawSdrImage.ToPreviewBgra8` as preview. No viewport scaling, raster effects,
second shoulder, additional D50 adaptation, model, or tile layout enters
the saved render. The global operator is spatially independent. The
full-resolution output is oriented/cropped by LibRaw before tone mapping;
the sRGB transfer and rounding occur exactly once at the final byte boundary.
Both formats are opaque 8-bit SDR, 96 DPI, with no source alpha to inherit.

**Color and metadata policy.** Both outputs embed an app-owned ICC v2 sRGB
matrix/shaper profile generated from the mathematical sRGB definition, with
4096-entry transfer curves. JPEG uses ICC APP2; PNG uses an `iCCP` chunk.
The profile's D50 PCS/Bradford-adapted primaries describe standard sRGB;
they do **not** transform the already-linear-sRGB scene pixels again.
WIC's generated PNG `sRGB`/`gAMA`/`cHRM` tags are removed before inserting
the sole ICC policy; conflicting `sRGB` and `iCCP` chunks are never combined.
No OS profile file, installed color utility, external runtime dependency,
source ICC, or monitor profile is copied.

Descriptive metadata is embedded as fresh XMP (JPEG APP1 / PNG `iTXt`).
The whitelist is DC title, description, subject/keywords, creator, rights;
XMP rating/create date; EXIF original capture date; TIFF camera make/model.
Same-stem source XMP takes precedence over embedded XMP per property.
EXIF make/model, image description, artist, copyright and capture date fill
missing XMP values. Unsupported native metadata reads and malformed source
XMP fail the export rather than silently dropping metadata. The source XMP
is otherwise retained in full, including dormant raster edits.

Source EXIF blocks, MakerNotes, GPS, face rectangles, flags, thumbnails,
crop/orientation tags and live edit namespaces are not copied to the
derivative. Pixels are already oriented; no binary EXIF orientation is
carried over (absence means identity), and new XMP explicitly records
orientation 1, output dimensions, and EXIF sRGB color-space value 1.
Fresh metadata is intentionally not a byte-for-byte copy of every source
field. JPEG metadata beyond its single APP1 capacity fails with an
instruction to choose PNG; it is not truncated.

Embedded provenance uses a separate `plraw` namespace: source **file name**
(not an absolute private path), pinned development version, tone-map version,
RAW exposure/white point, output color policy, and `State=Baked`. There is
no live `pledit` recipe or derivative sidecar, so reopening does not
double-develop or apply the source's dormant raster edits.
The retained source recipe now pins
`LibRaw0.22.3-AHD-CameraWB-LinearProPhotoD65-Reference0.25` as well as
`GlobalLuminanceV1`. Earlier recipes without a development-version field use
the existing pinned decoder; unknown explicit versions fail on open.

**Commit, cancellation, and library refresh.** Encoding and tagging use
private, uniquely owned staging files in the destination, flush the complete
container to disk, then retain the source XMP snapshot using its existing
atomic replacement/unique backup policy. Recipe writes are serialized within
the app. Only after successful staging cleanup and recipe persistence is the
derivative published by a same-folder, non-overwriting rename.
Failed decode, encode, metadata, permission, recipe, or move operations remove
owned staging files and surface the actual error. Cancellation is available
while recipe controls are frozen; closing/unloading or switching sources
invalidates the request. Even a renderer ignoring cancellation cannot publish
a stale result. Cancellation before the file commit publishes nothing; a
source recipe already committed is retained. Cancellation after the rename
is success, not a misleading cancelled export.

The derivative is explicitly passed to `LibraryIndexingService.IndexFileAsync`
and the existing library refresh, independent of folder-watcher timing. It
gets its own item ID, dimensions, and descriptive metadata; the RAW editor
stays on the original. An indexing failure reports the saved path and
instructs the user to refresh the destination; it does not misreport the
completed file export as a failed encode. Existing filters may exclude the
new item; exporting outside the currently displayed folder does not change
the user's filter.

**Parity and JPEG review.** Source-resolution synthetic Bayer DNG exports
with multiple exposure/white-point settings and orientation 6 are identical
to preview bytes after PNG reopen through both WIC and the actual app
`ImageEditRenderer.LoadOrientedAsync` path (`DoNotColorManage`).
The app's reference PNG parity is exact, including opaque alpha.
Separately invoking WIC ICC-to-system-sRGB color management showed at most
3 byte-code-value differences on these fixtures; that extra color conversion
is not the app's saved-render/reopen path or a calibrated-display guarantee.

JPEG is explicitly not lossless. The initial default chroma-subsampled trial
showed maximum/RMS errors of 10/1.0272 byte code values on a smooth RGB
gradient, and 50/5.1777 on the synthetic Bayer render. Quality 95 with 4:4:4
reduced those to 5/0.7774 and 6/0.9152. Reviewed regression limits are
maximum 8, RMS 1.25 for the 256x128 smooth gradient and maximum 12, RMS 2
for the source-size Bayer render, measured over RGB only. These independent
limits include headroom for codec rounding, not a universal per-pixel bound
for all photos. Choose PNG when exact byte parity is required.

Focused coverage includes existing-destination/foreign-sidecar and concurrent
collisions, denied destination creation, recipe-write permission failure,
invalid XMP after encoding, oversized JPEG metadata, WIC encode rejection,
active encoder cancellation, pre-cancellation, non-cooperative stale results,
versioned source recipe/history, immutable RAW bytes, ICC/XMP policy,
independent indexing, and unchanged raster saves. Physical disk-full,
power-loss/failed-storage recovery, real-camera color, ARM64 runtime,
low-memory/device behavior, screen-reader and interactive dialog/visual
review are not claimed. Cleanup errors caused by a failing/unavailable
filesystem are surfaced, not hidden.

**Measured cost.** A fresh serial x64 Release process on the same machine
described for #113, SDK 10.0.403 / .NET 8.0.33, measured native decode through
map, transfer, PNG encoding/tagging, source-recipe persistence and file
publication on 2026-10-31. The generated DNG's active area removes eight
pixels from each sensor dimension:

| Input sensor size | Export size | End-to-end export | Sampled peak process working set | Growth during case |
| --- | --- | ---: | ---: | ---: |
| 1024x768 | 1016x760 | 226.3 ms | 103.2 MiB | 42.0 MiB |
| 4000x3000 | 3992x2992 | 1681.8 ms | 516.9 MiB | 365.4 MiB |
| 6000x4000 | 5992x3992 | 3367.0 ms | 1257.0 MiB | 648.6 MiB |

These are generated Bayer DNGs, not camera samples. Sampling every 2 ms can
miss instantaneous peaks; the serial process retains allocator/earlier-case
memory. The first case includes JIT/startup costs. The measurement does not
include a live editor's cached preview/Win2D/GPU allocations, interactive
responsiveness, other devices, or slow/full destinations. Memory is
substantial even at the measured 24 MP size; this is evidence, not a low-memory guarantee.
The original combined regression run passed 122 tests without skips, including
index/search integration. x64 and ARM64 Release builds pass; the x64 WinUI
analyzer review reports only existing warnings outside this feature.

**High-resolution regression.** The subsequent removal of the artificial
24 MP ceiling is covered end-to-end with an 8256x5504 synthetic Bayer DNG
(45.44 MP sensor). With EXIF orientation 6 and the fixture's active-area crop,
preview and PNG export are 5496x8248 (45.33 MP), including actual Win2D
source-resolution bitmap upload. Reopened PNG bytes match the
full-resolution preview SHA-256 exactly, and source RAW bytes remain unchanged.
A valid DNG extended beyond 512 MiB also decodes successfully through the
direct-file path. These are synthetic regression fixtures, not evidence of
universal camera support, calibrated color, or interactive performance at
arbitrary sizes. The previous 24 MP cost measurements remain historical
measurements, not a resolution limit. The updated combined regression suite
passes 124 tests without skips.

Reproduce in a serial x64 Release process:

```powershell
dotnet run --project src\PhotoLibrarian.Tests\PhotoLibrarian.Tests.csproj -c Release -p:Platform=x64 -- -class PhotoLibrarian.Tests.RawDerivativeExportTests -class PhotoLibrarian.Tests.RawPreviewWorkflowTests -class PhotoLibrarian.Tests.RawDevelopmentTests -class PhotoLibrarian.Tests.RawToneMappingTests -class PhotoLibrarian.Tests.ImageEditorHistoryTests -class PhotoLibrarian.Tests.ImageRenderParityTests -class PhotoLibrarian.Tests.ImageExportTests -class PhotoLibrarian.Tests.LibraryIndexingServiceTests -class PhotoLibrarian.Tests.ImageSearchRepositoryTests -parallelMode none
dotnet run --no-build --project src\PhotoLibrarian.Tests\PhotoLibrarian.Tests.csproj -c Release -p:Platform=x64 -- -method PhotoLibrarian.Tests.RawDerivativeExportTests.ReportsSyntheticEndToEndExportCost -parallelMode none
```

The remaining RAW follow-up needs to:

1. Before claiming production camera support, validate camera-specific black
   levels, white balance, demosaic, color accuracy, and unclipped/clipped
   highlight behavior with real-camera fixtures. Investigate the native
   16-bit clamps and finite reference headroom; FP32 output cannot undo them.
2. If FP16/GPU intermediates are introduced, validate their precision, memory,
   cancellation, and color behavior against this FP32 reference. Distinguish
   real unsaturated sensor headroom from reconstructed color; fully saturated
   samples cannot be recovered reliably.
3. If a local operator is revisited, compare against the selected global
   shoulder on real highlights, gradients, color, noise, texture, halos,
   and runtime rather than shipping an unvalidated second mode.
4. Keep this monitor-independent SDR reference for derivative export and
   validate any future changes against preview/save/reopen parity. Direct2D's HDR tone-map effect does
   not decode RAW or supply missing sensor range; display adaptation must
   not leak into saved-render parameters.
5. Preserve the documented SDR color/metadata and collision-safe output
   contract when extending formats. Test that reopening
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
