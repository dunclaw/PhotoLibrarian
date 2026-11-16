// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;

namespace PhotoLibrarian.Core.Services;

internal static class PanoramaStitcher
{
    internal static PanoramaStitchResult Stitch(
        WindowsImagingCodec.DecodedImage first,
        WindowsImagingCodec.DecodedImage second,
        string firstPath,
        string secondPath,
        IProgress<PanoramaStitchProgress>? progress,
        CancellationToken cancellationToken) =>
        Stitch([first, second], [firstPath, secondPath], progress, cancellationToken);

    internal static PanoramaStitchResult Stitch(
        IReadOnlyList<WindowsImagingCodec.DecodedImage> images,
        IReadOnlyList<string> paths,
        IProgress<PanoramaStitchProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (images.Count < 2 || images.Count > PanoramaStitchingService.MaximumSourceCount ||
            images.Count != paths.Count)
            throw new ArgumentException("Supply 2 to 64 photos and a source path for each.");
        long decodedPixels = 0;
        foreach (var image in images)
        {
            decodedPixels += (long)image.Width * image.Height;
            if (image.Width < 32 || image.Height < 32 ||
                image.Width > PanoramaStitchingService.MaximumInputDimension ||
                image.Height > PanoramaStitchingService.MaximumInputDimension ||
                image.Pixels.LongLength != (long)image.Width * image.Height * 4 ||
                decodedPixels > PanoramaStitchingService.MaximumDecodedPixels)
                throw new InvalidDataException("Invalid photo dimensions or decoded memory limit exceeded.");
        }

        var pins = new GCHandle[images.Count];
        var inputs = new NativeInput[images.Count];
        var handle = IntPtr.Zero;
        ExceptionDispatchInfo? callbackFailure = null;
        var lastStage = -1;
        var lastPercent = -1;
        NativeProgress callback = ReportProgress;
        try
        {
            for (var index = 0; index < images.Count; index++)
            {
                pins[index] = GCHandle.Alloc(images[index].Pixels, GCHandleType.Pinned);
                inputs[index] = new()
                {
                    Pixels = pins[index].AddrOfPinnedObject(),
                    Width = (int)images[index].Width,
                    Height = (int)images[index].Height
                };
            }
            var error = new byte[4096];
            var status = NativeStitch(inputs, inputs.Length, callback, out handle, out var output, error, error.Length);
            callbackFailure?.Throw();
            cancellationToken.ThrowIfCancellationRequested();
            if (status != 0)
            {
                var message = Encoding.UTF8.GetString(error).TrimEnd('\0');
                if (output.MissingSources != 0)
                {
                    var missing = Enumerable.Range(0, paths.Count)
                        .Where(index => (output.MissingSources & (1UL << index)) != 0)
                        .Select(index => Path.GetFileName(paths[index]));
                    message += $" Could not connect: {string.Join(", ", missing)}.";
                }
                throw new InvalidDataException(string.IsNullOrWhiteSpace(message)
                    ? $"The native panorama engine failed (status {status})."
                    : message);
            }
            if (handle == IntPtr.Zero || output.Pixels == IntPtr.Zero || output.Sources == IntPtr.Zero ||
                output.Width < 64 || output.Height < 64 ||
                output.Width > PanoramaStitchingService.MaximumOutputDimension ||
                output.Height > PanoramaStitchingService.MaximumOutputDimension ||
                (long)output.Width * output.Height > PanoramaStitchingService.MaximumOutputPixels ||
                output.Inliers < 12 || output.Matches < output.Inliers)
                throw new InvalidDataException("The panorama engine returned invalid output.");
            var pixels = new byte[checked(output.Width * output.Height * 4)];
            Marshal.Copy(output.Pixels, pixels, 0, pixels.Length);
            var placements = new PanoramaSourcePlacement[images.Count];
            var gains = new double[images.Count];
            for (var index = 0; index < images.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = Marshal.PtrToStructure<NativeSource>(
                    IntPtr.Add(output.Sources, checked(index * Marshal.SizeOf<NativeSource>())));
                gains[index] = source.Gains.Average();
                var camera = output.Spherical == 0 ? null : new PanoramaCameraCalibration(
                    Array.AsReadOnly(source.Intrinsic), Array.AsReadOnly(source.Rotation));
                placements[index] = new((int)images[index].Width, (int)images[index].Height,
                    output.Spherical == 0 ? Array.AsReadOnly(source.Transform) : Array.Empty<double>(),
                    Array.AsReadOnly(source.Gains), camera);
            }
            return new(pixels, output.Width, output.Height, Array.AsReadOnly(paths.ToArray()),
                output.Matches, output.Inliers, (double)output.Inliers / output.Matches,
                Array.AsReadOnly(gains), output.OutputScale, output.RetainedArea,
                Array.AsReadOnly(placements), output.Spherical == 0 ? "Projective" : "Spherical",
                output.WarpScale, output.OffsetX, output.OffsetY);
        }
        finally
        {
            if (handle != IntPtr.Zero)
                NativeFree(handle);
            foreach (var pin in pins)
                if (pin.IsAllocated)
                    pin.Free();
            GC.KeepAlive(callback);
        }

        int ReportProgress(int stage, int percent)
        {
            if (cancellationToken.IsCancellationRequested || callbackFailure is not null)
                return 1;
            try
            {
                if (lastStage != stage || lastPercent != percent)
                {
                    var message = stage switch
                    {
                        1 => "Finding image features",
                        2 => images.Count == 2 ? "Checking overlap 1 of 1" : "Checking photo overlap",
                        3 => "Refining camera alignment",
                        4 => "Finding seams and compensating exposure",
                        5 => "Blending and cropping panorama",
                        6 => "Checking the clean crop",
                        7 => "Panorama preview ready",
                        _ => throw new InvalidDataException("The panorama engine reported an invalid stage.")
                    };
                    progress?.Report(new(message, percent));
                    lastStage = stage;
                    lastPercent = percent;
                }
                return cancellationToken.IsCancellationRequested ? 1 : 0;
            }
            catch (Exception exception)
            {
                // Managed exceptions must not unwind through the native callback boundary.
                callbackFailure = ExceptionDispatchInfo.Capture(exception);
                return 1;
            }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NativeProgress(int stage, int percent);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public IntPtr Pixels;
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeOutput
    {
        public IntPtr Pixels;
        public IntPtr Sources;
        public int Width;
        public int Height;
        public int Matches;
        public int Inliers;
        public int Spherical;
        public int OffsetX;
        public int OffsetY;
        public double WarpScale;
        public double OutputScale;
        public double RetainedArea;
        public ulong MissingSources;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSource
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)]
        public double[] Transform;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)]
        public double[] Intrinsic;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)]
        public double[] Rotation;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public double[] Gains;
    }

    [DllImport("PhotoLibrarian.Panorama.dll", EntryPoint = "photolibrarian_panorama_stitch",
        CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    private static extern int NativeStitch(
        [In] NativeInput[] images, int count, NativeProgress progress,
        out IntPtr handle, out NativeOutput output, [Out] byte[] error, int errorCapacity);

    [DllImport("PhotoLibrarian.Panorama.dll", EntryPoint = "photolibrarian_panorama_free",
        CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    private static extern void NativeFree(IntPtr handle);
}
