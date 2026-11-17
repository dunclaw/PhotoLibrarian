// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace PhotoLibrarian.Core.Services;

public enum RasterColorProfileStatus { Untagged, Embedded, Invalid, Unsupported }

/// <summary>
/// First-frame ICC policy for raster editing: sRGB working pixels, with conversion back to
/// the original profile before a metadata-preserving bake. No monitor or user profile is used.
/// </summary>
public sealed class RasterColorProfile
{
    private readonly byte[]? _data;

    public RasterColorProfileStatus Status { get; }
    public string? Message { get; }
    public bool IsSupported => Status is RasterColorProfileStatus.Untagged or RasterColorProfileStatus.Embedded;
    internal byte[]? Data => _data;

    private RasterColorProfile(RasterColorProfileStatus status, byte[]? data = null, string? message = null)
        => (Status, _data, Message) = (status, data, message);

    public static RasterColorProfile Read(string path)
    {
        // The Windows BMP codec does not implement GetColorContexts. Do not mistake its
        // unsupported-operation HRESULT for an absent profile in a V4/V5 bitmap.
        Span<byte> header = stackalloc byte[74];
        using (var stream = File.OpenRead(path))
        {
            var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            if (read >= 18 && header[0] == 'B' && header[1] == 'M')
            {
                var dibSize = BinaryPrimitives.ReadUInt32LittleEndian(header[14..]);
                if (dibSize >= 108 && read >= 74 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(header[70..]) is 0x4d424544 or 0x4c494e4b)
                    return new(RasterColorProfileStatus.Unsupported,
                        message: "The Windows BMP codec cannot transform embedded or linked color profiles.");
                return new(RasterColorProfileStatus.Untagged);
            }
        }
        using var wic = new WicSession();
        var profiles = wic.ReadProfiles(path, out var unsupportedContext);
        if (unsupportedContext)
            return new(RasterColorProfileStatus.Unsupported, message: "Windows returned an unsupported image color context.");
        if (profiles.Count == 0) return new(RasterColorProfileStatus.Untagged);
        if (profiles.Count != 1)
            return new(RasterColorProfileStatus.Unsupported, message: "The image contains multiple embedded ICC profiles.");
        var data = profiles[0];
        if (data.Length < 132 || !data.AsSpan(36, 4).SequenceEqual("acsp"u8) ||
            BinaryPrimitives.ReadUInt32BigEndian(data) != data.Length)
            return new(RasterColorProfileStatus.Invalid, message: "The embedded ICC profile has an invalid header or length.");
        if (!data.AsSpan(16, 4).SequenceEqual("RGB "u8) ||
            !(data.AsSpan(20, 4).SequenceEqual("XYZ "u8) || data.AsSpan(20, 4).SequenceEqual("Lab "u8)) ||
            data[8] is not (2 or 4))
            return new(RasterColorProfileStatus.Unsupported,
                message: "Raster editing requires an ICC v2/v4 RGB profile with an XYZ or Lab connection space.");
        var tagCount = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(128));
        var tableEnd = 132L + tagCount * 12L;
        if (tagCount == 0 || tableEnd > data.Length)
            return new(RasterColorProfileStatus.Invalid, message: "The embedded ICC profile has an invalid tag table.");
        for (var index = 0; index < tagCount; index++)
        {
            var entry = checked(132 + (int)index * 12);
            var offset = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(entry + 4));
            var size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(entry + 8));
            if (offset < tableEnd || offset % 4 != 0 || size < 8 || (long)offset + size > data.Length)
                return new(RasterColorProfileStatus.Invalid, message: "The embedded ICC profile contains an invalid tag range.");
        }
        var profile = new RasterColorProfile(RasterColorProfileStatus.Embedded, data);
        try
        {
            // WIC can expose profile bytes it cannot actually transform. Check both directions
            // before presenting an editable preview, not for the first time during a save.
            byte[] probe = [96, 128, 160, 255];
            wic.Convert(probe, 1, 1, data, toSrgb: true);
            wic.Convert(probe, 1, 1, data, toSrgb: false);
            return profile;
        }
        catch (COMException ex)
        {
            return new(RasterColorProfileStatus.Unsupported,
                message: $"Windows cannot transform the embedded ICC profile (0x{ex.HResult:X8}).");
        }
    }

    public byte[] ToSrgb(byte[] pixels, uint width, uint height) => Convert(pixels, width, height, true);
    public byte[] FromSrgb(byte[] pixels, uint width, uint height) => Convert(pixels, width, height, false);

    private byte[] Convert(byte[] pixels, uint width, uint height, bool toSrgb)
    {
        if (!IsSupported) throw new InvalidOperationException(Message);
        // Our own sRGB derivatives are already working-space pixels. Avoid an unnecessary
        // CMM round trip (and its one-code-value quantization) for this exact known profile.
        if (_data is null || SdrDerivativeMetadata.IsSrgbProfile(_data)) return pixels;
        using var wic = new WicSession();
        return wic.Convert(pixels, width, height, _data, toSrgb);
    }

    private sealed class WicSession : IDisposable
    {
        private readonly List<object> _objects = [];
        private readonly IWicFactory _factory;
        private static readonly Guid Pbgra = new("6fddc324-4e03-4bfe-b185-3d77768dc910");

        internal WicSession()
        {
            var type = Type.GetTypeFromCLSID(new Guid("cacaf262-9370-4615-a13b-9f5539da4c0a"), throwOnError: true)!;
            _factory = Own((IWicFactory)Activator.CreateInstance(type)!);
        }

        private T Own<T>(T value) where T : class
        {
            _objects.Add(value);
            return value;
        }

        internal List<byte[]> ReadProfiles(string path, out bool unsupportedContext)
        {
            unsupportedContext = false;
            _factory.CreateDecoderFromFilename(path, IntPtr.Zero, 0x80000000, 0, out var decoder);
            Own(decoder);
            decoder.GetFrame(0, out var frame);
            Own(frame);
            frame.GetColorContexts(0, null, out var count);
            if (count == 0) return [];
            var contexts = new IWicColorContext[checked((int)count)];
            for (var i = 0; i < contexts.Length; i++)
            {
                _factory.CreateColorContext(out var context);
                contexts[i] = Own(context);
            }
            frame.GetColorContexts(count, contexts, out var actual);
            if (actual != count)
            {
                unsupportedContext = true;
                return [];
            }
            var result = new List<byte[]>();
            foreach (var context in contexts)
            {
                context.GetType(out var type);
                if (type == 2) continue; // EXIF hints without ICC retain the untagged interpretation.
                if (type != 1)
                {
                    unsupportedContext = true;
                    continue;
                }
                context.GetProfileBytes(0, null, out var size);
                var data = new byte[checked((int)size)];
                context.GetProfileBytes(size, data, out _);
                result.Add(data);
            }
            return result;
        }

        internal byte[] Convert(byte[] pixels, uint width, uint height, byte[] profile, bool toSrgb)
        {
            var stride = checked(width * 4);
            if (width == 0 || height == 0 || pixels.LongLength != (long)stride * height)
                throw new ArgumentException("Color conversion requires a complete premultiplied BGRA8 image.", nameof(pixels));
            _factory.CreateColorContext(out var embedded);
            Own(embedded).InitializeFromMemory(profile, checked((uint)profile.Length));
            _factory.CreateColorContext(out var srgb);
            Own(srgb).InitializeFromExifColorSpace(1);
            _factory.CreateBitmapFromMemory(width, height, Pbgra, stride, checked((uint)pixels.Length), pixels, out var bitmap);
            Own(bitmap);
            _factory.CreateColorTransformer(out var transform);
            Own(transform).Initialize(bitmap, toSrgb ? embedded : srgb, toSrgb ? srgb : embedded, Pbgra);
            var output = new byte[pixels.Length];
            transform.CopyPixels(IntPtr.Zero, stride, checked((uint)output.Length), output);
            return output;
        }

        public void Dispose()
        {
            for (var i = _objects.Count - 1; i >= 0; i--)
                Marshal.ReleaseComObject(_objects[i]);
        }
    }

    // Unused entries preserve the SDK interface's vtable slots. Only declared, typed
    // operations are called; these interfaces never escape a WicSession.
    [ComImport, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWicFactory
    {
        void CreateDecoderFromFilename([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr vendor,
            uint access, uint options, out IWicDecoder decoder);
        void CreateDecoderFromStream();
        void CreateDecoderFromFileHandle();
        void CreateComponentInfo();
        void CreateDecoder();
        void CreateEncoder();
        void CreatePalette();
        void CreateFormatConverter();
        void CreateBitmapScaler();
        void CreateBitmapClipper();
        void CreateBitmapFlipRotator();
        void CreateStream();
        void CreateColorContext(out IWicColorContext context);
        void CreateColorTransformer(out IWicColorTransform transform);
        void CreateBitmap();
        void CreateBitmapFromSource();
        void CreateBitmapFromSourceRect();
        void CreateBitmapFromMemory(uint width, uint height, [In] in Guid format, uint stride,
            uint size, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 4)] byte[] pixels,
            out IWicBitmapSource bitmap);
    }

    [ComImport, Guid("9edde9e7-8dee-47ea-99df-e6faf2ed44bf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWicDecoder
    {
        void QueryCapability();
        void Initialize();
        void GetContainerFormat();
        void GetDecoderInfo();
        void CopyPalette();
        void GetMetadataQueryReader();
        void GetPreview();
        void GetColorContexts();
        void GetThumbnail();
        void GetFrameCount();
        void GetFrame(uint index, out IWicFrame frame);
    }

    [ComImport, Guid("3b16811b-6a43-4ec9-a813-3d930c13b940"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWicFrame
    {
        void GetSize();
        void GetPixelFormat();
        void GetResolution();
        void CopyPalette();
        void CopyPixels();
        void GetMetadataQueryReader();
        void GetColorContexts(uint count,
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IWicColorContext[]? contexts,
            out uint actual);
    }

    [ComImport, Guid("3c613a02-34b2-44ea-9a7c-45aea9c6fd6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWicColorContext
    {
        void InitializeFromFilename();
        void InitializeFromMemory([In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] data, uint size);
        void InitializeFromExifColorSpace(uint value);
        void GetType(out uint type);
        void GetProfileBytes(uint size,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] byte[]? data, out uint actual);
    }

    [ComImport, Guid("00000120-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWicBitmapSource
    {
        void GetSize();
        void GetPixelFormat();
        void GetResolution();
        void CopyPalette();
        void CopyPixels();
    }

    [ComImport, Guid("b66f034f-d0e2-40ab-b436-6de39e321a94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWicColorTransform
    {
        void GetSize();
        void GetPixelFormat();
        void GetResolution();
        void CopyPalette();
        void CopyPixels(IntPtr rectangle, uint stride, uint size,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] pixels);
        void Initialize(IWicBitmapSource source, IWicColorContext sourceContext,
            IWicColorContext destinationContext, [In] in Guid format);
    }
}
