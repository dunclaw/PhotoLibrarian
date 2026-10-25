# Bounded model-free deblur experiment

Local investigation for [#78](https://github.com/dunclaw/PhotoLibrarian/issues/78),
following [#90](https://github.com/dunclaw/PhotoLibrarian/issues/90) /
[#105](https://github.com/dunclaw/PhotoLibrarian/pull/105). Measured 2026-10-25.

## Decision: do not ship this candidate

The bounded Richardson-Lucy (RL) reference improves exact-kernel synthetic
camera-motion and defocus inputs, but fails the safe-use gate under kernel
mismatch, spatially varying subject motion, noise, and clean controls.
Keep it **test-only**. No deblur control, recipe field, XMP, history change,
production processing path, asset, dependency, or network service is added.
Sharpening remains sharpening, not blur recovery. This does not establish
that all model-free approaches fail; it rejects this unregularized candidate
as a general-purpose editor feature.

This work is independent on master, not stacked on the unmerged selectable
sharpening [PR #127](https://github.com/dunclaw/PhotoLibrarian/pull/127) /
[#108](https://github.com/dunclaw/PhotoLibrarian/issues/108). **Direct comparison
against that explicit unsharp-mask implementation must wait for #108 to
merge.** The existing legacy Win2D sharpen is measured here; do not treat
it as a substitute for the pending comparison or declare the whole #78
handoff complete. No Azure or AI work is required or performed.

## Reproducible method and bounds

`src\PhotoLibrarian.Tests\DeblurExperiment.cs` contains the CPU double-precision,
grayscale reference, inaccessible to the app. Samples are normalized encoded grayscale,
not scene-linear sensor values. It uses positive multiplicative
RL updates starting from the observed image, with a 1e-6 denominator/initial
estimate floor. Symmetric, normalized kernels are a horizontal/vertical
7-tap line and a discrete radius-2 disk (13 taps). Periodic convolution is
its own adjoint for these kernels, avoiding a mismatched forward/backprojection
at borders. These **synthetic periodic borders are not a production border
policy** and make the known-kernel case optimistic.

Iterations are capped at 20 (measure 5, 10, 20); inputs at 65,536 pixels.
Invalid dimensions, kernels, nonfinite/out-of-range observations, and iteration
counts throw. Zero iterations returns an exact independent copy; cancellation
is checked before work and each iteration. The reference retains unclamped
intermediates to expose overshoot; clamp/round to grayscale BGRA8 only for
quality comparisons. It never opens or writes a user's photo.

The 96x64 target composes #107's generated edge, fine-texture, and gradient
fixtures, with flat patches included. Degrade by the known line/disk, add
seed-217 uniform noise in [-0.04, 0.04], or encode/decode with Windows WIC JPEG
quality 0.75. For subject motion, replace only the central 48x32 rectangle
with line-blurred pixels, leaving the background sharp. This is a toy
spatially varying kernel test, **not** a physical occlusion/motion model.
Mismatch tests invert horizontal blur with a vertical line, or disk blur
with a horizontal line. A clean control checks damage from unnecessary inversion.

Every case compares no-op, the production `ImageEditRenderPass` with legacy
`Sharpness=0.35` (other settings neutral), and each RL iteration count.
RL is not passed through the production graph. PSNR uses peak 1 against
the original floating-point sharp target after each result's BGRA8
clamp/round trip. The clean no-op's finite PSNR is quantization error.
SSIM is mean non-overlapping 8x8 grayscale block SSIM with population
variance, C1=0.01^2, C2=0.03^2; it is not a standard sliding Gaussian-window
SSIM benchmark. All pixels including borders are measured.

## Measurements

Each cell is **PSNR dB / block SSIM**, not a perceptual quality certification.

| Input / assumed inverse | No-op | Legacy 0.35 | RL 5 | RL 20 |
| --- | --- | --- | --- | --- |
| Camera / exact line | 22.493 / .5392 | 22.039 / .4623 | 24.656 / .5766 | 28.318 / .6749 |
| Defocus / exact disk | 25.802 / .6185 | 25.918 / .6609 | 28.023 / .7100 | 29.605 / .7602 |
| Camera + noise / exact line | 22.135 / .3759 | 21.159 / .2285 | 23.902 / .3828 | 24.092 / .3039 |
| Defocus + noise / exact disk | 24.971 / .4747 | 23.915 / .4126 | 26.350 / .5232 | 24.759 / .3953 |
| Camera + JPEG / exact line | 22.758 / .5937 | 22.433 / .5335 | 24.523 / .5825 | 26.141 / .5584 |
| Defocus + JPEG / exact disk | 25.607 / .6022 | 25.442 / .6336 | 27.522 / .6826 | 27.906 / .6954 |
| Camera / wrong direction | 22.493 / .5392 | 22.039 / .4623 | 21.081 / .4828 | 18.696 / .4003 |
| Defocus / wrong shape | 25.802 / .6185 | 25.918 / .6609 | 23.339 / .5092 | 19.799 / .2419 |
| Moving subject / global line | 28.892 / .8944 | 24.373 / .7367 | 24.343 / .6641 | 17.856 / .3294 |
| Clean / unnecessary line | 63.020 / .9998 | 25.180 / .8000 | 24.260 / .6950 | 16.765 / .2283 |

Clamping can conceal instability. At RL 20, the fraction of raw pixels above
1 is 1.644% (camera noise), 1.497% (defocus noise), 2.539% (wrong direction),
5.794% (wrong shape), and 3.223% (moving subject). The respective largest
excesses above 1 are .1794, .2120, .5444, .6177, and 2.2574.
Exact-disk RL 20 still clips .960% of pixels, with peak excess .0369.
Even where overall PSNR improves, camera-noise/JPEG SSIM regresses as iterations
grow. More iterations are not a safe strength mapping.

A separate flat 0.5 patch with the same noise has **2.040x RMS noise
amplification** after line RL 20. A line-blurred 24/255-to-232/255 step has
**.0367 overshoot and .0302 undershoot** relative to the reference plateaus.
These ringing measurements precede display clamping. An exactly seven-pixel
periodic sinusoid is annihilated by the seven-tap line: RL stays flat after
20 iterations, demonstrating a kernel null rather than recovered lost detail.

## Re-run and remaining gates

```powershell
dotnet run --project src\PhotoLibrarian.Tests\PhotoLibrarian.Tests.csproj -c Release -p:Platform=x64 -- -class PhotoLibrarian.Tests.DeblurExperimentTests -class PhotoLibrarian.Tests.RenderQualityFixtureTests -class PhotoLibrarian.Tests.ImageRenderParityTests -reporter verbose -showliveoutput
```

Production baseline: master commit `07b6f65`. Measured on AMD Ryzen 9
9950X3D, Windows 11 Pro 10.0.26300, .NET SDK
10.0.402 / test runtime .NET 8.0.32, x64 Release. RL is CPU; the legacy
comparison explicitly uses the Win2D software device. WIC JPEG and graphics
initialization failures fail the test, not a skip or a false success.
The command passes 15 tests with no skips: 9 experiment tests, 1 fixture
test, and 5 existing render-parity tests (including the multi-fixture recipe
matrix). Those parity tests verify **unchanged production behavior**, not an
RL preview/save implementation.

Assertions cover exact-kernel improvement, damage from each mismatch/subject/
clean-control case at all three iteration counts, flat-noise amplification,
step ringing, kernel-null loss, constants at small/odd dimensions,
invalid inputs, neutral copy, cancellation, kernel energy/adjoint correctness,
and finite outputs. Full metric
rows, including RL 10, are printed with `-showliveoutput`.

No real-photo visual quality certification, color/alpha restoration,
arbitrary-angle/kernel estimation, spatial masks, depth-varying defocus,
Wiener FFT comparison, damping/regularization, or production-scale
1/12/24-MP performance/memory measurements are established. These are
limitations, not claims of support. Do not extrapolate tiny fixture test
latency to editor throughput.

If reopened for a candidate implementation, first complete the #108 comparison,
then test a bounded regularized method and review 1:1 real-photo texture,
JPEG artifacts, ringing, and noise. Require the shared source-resolution
preview/save path, alpha/color/border policy, exact neutral bypass,
recipe/XMP/clone/reset/history wiring, cancellation/stale-result handling,
explicit visible failures, device/resource bounds, and quality/parity gates
in [ADVANCED-EDITING.md](ADVANCED-EDITING.md) before exposing any mode.
