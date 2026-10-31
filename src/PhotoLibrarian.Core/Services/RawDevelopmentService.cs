// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PhotoLibrarian.Core.Services;

public enum RawDecodeFailure
{
    Unsupported,
    Corrupt,
    TooLarge,
    Failed
}

public sealed class RawDecodeException : IOException
{
    public RawDecodeException(RawDecodeFailure failure, string message)
        : base(message) => Failure = failure;

    public RawDecodeFailure Failure { get; }
}

public sealed record RawDevelopmentMetadata(
    string CameraMake,
    string CameraModel,
    uint SensorWidth,
    uint SensorHeight,
    uint VisibleWidth,
    uint VisibleHeight,
    uint OutputWidth,
    uint OutputHeight,
    uint SourceOrientation,
    uint BlackLevelRed,
    uint BlackLevelGreen1,
    uint BlackLevelBlue,
    uint BlackLevelGreen2,
    uint WhiteLevelRed,
    uint WhiteLevelGreen1,
    uint WhiteLevelBlue,
    uint WhiteLevelGreen2,
    float ReferenceExposureScale);

public sealed class RawDevelopedImage
{
    internal RawDevelopedImage(float[] rgbValues, RawDevelopmentMetadata metadata)
    {
        RgbValues = rgbValues;
        Metadata = metadata;
    }

    public ReadOnlyMemory<float> RgbValues { get; }

    public RawDevelopmentMetadata Metadata { get; }
}

public static class RawDevelopmentService
{
    public const string RecipeVersion = "LibRaw0.22.3-AHD-CameraWB-LinearProPhotoD65-Reference0.25";
    public const string WorkingColorSpace = "Linear ProPhoto RGB";
    // output_color=4 uses LibRaw's ProPhoto D65 matrix, not an ICC D50 ProPhoto buffer.
    public const string WorkingWhitePoint = "D65 (LibRaw output_color=4)";

    private const long MaximumInputBytes = 512L * 1024 * 1024;
    private const int ErrorMessageCapacity = 1024;

    public static async Task<RawDevelopedImage> DecodeAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > MaximumInputBytes)
        {
            throw new RawDecodeException(
                RawDecodeFailure.TooLarge,
                $"The RAW input must be between 1 byte and {MaximumInputBytes / (1024 * 1024)} MiB.");
        }

        var input = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(input, cancellationToken);
        return await Task.Run(
            () => Decode(input, cancellationToken),
            cancellationToken);
    }

    private static RawDevelopedImage Decode(byte[] input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pinnedInput = GCHandle.Alloc(input, GCHandleType.Pinned);
        var cancellationFlag = Marshal.AllocHGlobal(sizeof(int));
        var errorMessage = Marshal.AllocHGlobal(ErrorMessageCapacity);
        try
        {
            Marshal.WriteInt32(cancellationFlag, 0);
            Marshal.WriteByte(errorMessage, 0);
            using var registration = cancellationToken.Register(
                static state => Marshal.WriteInt32((IntPtr)state!, 1),
                cancellationFlag);

            var status = NativeMethods.Decode(
                pinnedInput.AddrOfPinnedObject(),
                (ulong)input.LongLength,
                cancellationFlag,
                out var imageHandle,
                out var nativeMetadata,
                errorMessage,
                ErrorMessageCapacity);

            if (status == NativeMethods.Cancelled)
                cancellationToken.ThrowIfCancellationRequested();

            if (status != NativeMethods.Success)
            {
                var message = Marshal.PtrToStringAnsi(errorMessage) ?? "The RAW decoder failed without an error message.";
                var failure = status switch
                {
                    NativeMethods.Unsupported => RawDecodeFailure.Unsupported,
                    NativeMethods.Corrupt => RawDecodeFailure.Corrupt,
                    NativeMethods.TooLarge => RawDecodeFailure.TooLarge,
                    _ => RawDecodeFailure.Failed
                };
                throw new RawDecodeException(failure, message);
            }

            using (imageHandle)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sampleCount = checked((int)((long)nativeMetadata.Width * nativeMetadata.Height * 3));
                var rgbValues = new float[sampleCount];
                Marshal.Copy(NativeMethods.GetPixels(imageHandle), rgbValues, 0, rgbValues.Length);
                var metadata = new RawDevelopmentMetadata(
                    nativeMetadata.Make ?? string.Empty,
                    nativeMetadata.Model ?? string.Empty,
                    nativeMetadata.RawWidth,
                    nativeMetadata.RawHeight,
                    nativeMetadata.VisibleWidth,
                    nativeMetadata.VisibleHeight,
                    nativeMetadata.Width,
                    nativeMetadata.Height,
                    nativeMetadata.Orientation,
                    nativeMetadata.BlackLevels[0],
                    nativeMetadata.BlackLevels[1],
                    nativeMetadata.BlackLevels[2],
                    nativeMetadata.BlackLevels[3],
                    nativeMetadata.WhiteLevels[0],
                    nativeMetadata.WhiteLevels[1],
                    nativeMetadata.WhiteLevels[2],
                    nativeMetadata.WhiteLevels[3],
                    nativeMetadata.ExposureScale);
                return new RawDevelopedImage(rgbValues, metadata);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(errorMessage);
            Marshal.FreeHGlobal(cancellationFlag);
            pinnedInput.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct NativeMetadata
    {
        public uint Width;
        public uint Height;
        public uint RawWidth;
        public uint RawHeight;
        public uint VisibleWidth;
        public uint VisibleHeight;
        public uint Orientation;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public uint[] BlackLevels;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public uint[] WhiteLevels;

        public float ExposureScale;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string? Make;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string? Model;
    }

    private sealed class RawDecodedImageHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private RawDecodedImageHandle() : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle()
        {
            NativeMethods.Destroy(handle);
            return true;
        }
    }

    private static class NativeMethods
    {
        internal const int Success = 0;
        internal const int Unsupported = 1;
        internal const int Corrupt = 2;
        internal const int TooLarge = 3;
        internal const int Cancelled = 4;

        private const string LibraryName = "PhotoLibrarian.RawDecoder.dll";

        [DllImport(LibraryName, EntryPoint = "raw_decoder_decode", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Decode(
            IntPtr input,
            ulong inputLength,
            IntPtr cancellationFlag,
            out RawDecodedImageHandle image,
            out NativeMetadata metadata,
            IntPtr errorMessage,
            uint errorMessageCapacity);

        [DllImport(LibraryName, EntryPoint = "raw_decoder_pixels", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr GetPixels(RawDecodedImageHandle image);

        [DllImport(LibraryName, EntryPoint = "raw_decoder_destroy", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Destroy(IntPtr image);
    }
}
