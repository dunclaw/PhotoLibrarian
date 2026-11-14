// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Xmp;
using PhotoLibrarian.Core.Models;
using XmpCore;
using XmpCore.Options;

namespace PhotoLibrarian.Core.Services;

/// <summary>New ICC/XMP only: no source profile, thumbnail, GPS, faces, or live edit recipe.</summary>
internal static class SdrDerivativeMetadata
{
    internal const string ProvenanceNamespace = "https://photolibrarian.app/ns/raw-derivative/1.0/";
    private static readonly byte[] Profile = CreateSrgbProfile();
    private static readonly byte[] JpegStandardXmpHeader = "http://ns.adobe.com/xap/1.0/\0"u8.ToArray();
    private static readonly byte[] JpegExtendedXmpHeader =
        "http://ns.adobe.com/xmp/extension/\0"u8.ToArray();
    private static readonly byte[] JpegIccHeader = "ICC_PROFILE\0"u8.ToArray();

    internal static string CreateXmp(string path, EditParameters parameters, RawSdrImage image)
    {
        var directories = ImageMetadataReader.ReadMetadata(path);
        var embedded = directories.OfType<XmpDirectory>().FirstOrDefault()?.XmpMeta;
        var sidecar = Path.ChangeExtension(path, ".xmp");
        var source = File.Exists(sidecar)
            ? XmpMetaFactory.ParseFromString(File.ReadAllText(sidecar))
            : embedded;
        var xmp = XmpMetaFactory.Create();
        const string dc = "http://purl.org/dc/elements/1.1/";
        const string xap = "http://ns.adobe.com/xap/1.0/";
        const string exif = "http://ns.adobe.com/exif/1.0/";
        const string tiff = "http://ns.adobe.com/tiff/1.0/";
        // Copy only portable descriptive XMP, preserving arrays and localized text.
        foreach (var (ns, name) in new[]
        {
            (dc, "title"), (dc, "description"), (dc, "subject"), (dc, "creator"), (dc, "rights"),
            (xap, "Rating"), (xap, "CreateDate"), (exif, "DateTimeOriginal"),
            (tiff, "Make"), (tiff, "Model")
        })
        {
            var owner = source?.DoesPropertyExist(ns, name) == true ? source : embedded;
            if (owner?.DoesPropertyExist(ns, name) == true)
                XmpUtils.DuplicateSubtree(owner, xmp, ns, name, ns, name, new PropertyOptions());
        }

        var ifd = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
        var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        SetMissing(xmp, tiff, "Make", ifd?.GetDescription(ExifDirectoryBase.TagMake));
        SetMissing(xmp, tiff, "Model", ifd?.GetDescription(ExifDirectoryBase.TagModel));
        if (!xmp.DoesPropertyExist(dc, "description") &&
            ifd?.GetDescription(ExifDirectoryBase.TagImageDescription) is { Length: > 0 } description)
            xmp.SetLocalizedText(dc, "description", "", "x-default", description);
        if (!xmp.DoesPropertyExist(dc, "rights") &&
            ifd?.GetDescription(ExifDirectoryBase.TagCopyright) is { Length: > 0 } copyright)
            xmp.SetLocalizedText(dc, "rights", "", "x-default", copyright);
        if (!xmp.DoesPropertyExist(dc, "creator") &&
            ifd?.GetDescription(ExifDirectoryBase.TagArtist) is { Length: > 0 } artist)
            xmp.AppendArrayItem(dc, "creator", new PropertyOptions { IsArrayOrdered = true }, artist, null);
        if (subIfd?.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var date) == true)
            SetMissing(xmp, exif, "DateTimeOriginal", date.ToString("s", CultureInfo.InvariantCulture));

        xmp.SetPropertyInteger(tiff, "Orientation", 1);
        xmp.SetPropertyInteger(tiff, "ImageWidth", image.Width);
        xmp.SetPropertyInteger(tiff, "ImageLength", image.Height);
        xmp.SetPropertyInteger(exif, "ColorSpace", 1);
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(ProvenanceNamespace, "plraw");
        xmp.SetProperty(ProvenanceNamespace, "SourceFileName", Path.GetFileName(path));
        xmp.SetProperty(ProvenanceNamespace, "Development", RawDevelopmentService.RecipeVersion);
        xmp.SetProperty(ProvenanceNamespace, "ToneMapping", RawToneMappingService.RecipeVersion);
        xmp.SetPropertyDouble(ProvenanceNamespace, "ExposureEv", parameters.RawExposureEv);
        xmp.SetPropertyDouble(ProvenanceNamespace, "WhitePoint", parameters.RawWhitePoint);
        xmp.SetProperty(ProvenanceNamespace, "ColorPolicy", "Opaque 8-bit IEC 61966-2-1 sRGB");
        xmp.SetProperty(ProvenanceNamespace, "State", "Baked");
        return XmpMetaFactory.SerializeToString(xmp, new SerializeOptions { OmitPacketWrapper = true });
    }

    private static void SetMissing(IXmpMeta xmp, string ns, string name, string? value)
    {
        if (!xmp.DoesPropertyExist(ns, name) && !string.IsNullOrWhiteSpace(value))
            xmp.SetProperty(ns, name, value.Trim());
    }

    internal static async Task TagAsync(Stream input, FileStream output, RawDerivativeFormat format,
        string xmp, CancellationToken token, bool allowExtendedXmp = false)
    {
        var text = Encoding.UTF8.GetBytes(xmp);
        if (format == RawDerivativeFormat.Jpeg)
        {
            byte[] header = new byte[2];
            await input.ReadExactlyAsync(header, token);
            if (!header.AsSpan().SequenceEqual(new byte[] { 0xff, 0xd8 }))
                throw new InvalidDataException("JPEG encoder returned an invalid container.");
            await output.WriteAsync(header, token);
            WriteJpegSegment(output, 0xe2, Combine("ICC_PROFILE\0"u8.ToArray(), [1, 1], Profile));
            byte[] standardHeader = "http://ns.adobe.com/xap/1.0/\0"u8.ToArray();
            if (text.Length + standardHeader.Length <= 65533 || !allowExtendedXmp)
                WriteJpegSegment(output, 0xe1, Combine(standardHeader, text));
            else
                WriteExtendedJpegXmp(output, text, standardHeader, token);
            await CopyJpegSegmentsAsync(input, output, token);
        }
        else
        {
            byte[] header = new byte[33]; // Signature and complete IHDR; profile precedes image data.
            await input.ReadExactlyAsync(header, token);
            if (!header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                !header.AsSpan(12, 4).SequenceEqual("IHDR"u8))
                throw new InvalidDataException("PNG encoder returned an invalid container.");
            await output.WriteAsync(header, token);
            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(Profile);
            WritePngChunk(output, "iCCP", Combine("sRGB\0"u8.ToArray(), [0], compressed.ToArray()));
            WritePngChunk(output, "iTXt", Combine("XML:com.adobe.xmp\0"u8.ToArray(), [0, 0, 0, 0], text));
        }
        token.ThrowIfCancellationRequested();
        if (format == RawDerivativeFormat.Png)
            await CopyPngChunksAsync(input, output, token);
        await output.FlushAsync(token);
        output.Flush(flushToDisk: true);
    }

    private static async Task CopyJpegSegmentsAsync(Stream input, Stream output, CancellationToken token)
    {
        var lengthBytes = new byte[2];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var first = input.ReadByte();
            if (first < 0)
                throw new InvalidDataException("JPEG encoder returned a truncated container.");
            if (first != 0xff)
                throw new InvalidDataException("JPEG encoder returned an invalid marker sequence.");

            var marker = input.ReadByte();
            while (marker == 0xff)
                marker = input.ReadByte();
            if (marker < 0)
                throw new InvalidDataException("JPEG encoder returned a truncated marker.");

            if (marker == 0xda)
            {
                await output.WriteAsync(new byte[] { 0xff, (byte)marker }, token);
                await input.ReadExactlyAsync(lengthBytes, token);
                var scanLength = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);
                if (scanLength < 2)
                    throw new InvalidDataException("JPEG encoder returned an invalid scan header.");
                await output.WriteAsync(lengthBytes, token);
                await CopyExactlyAsync(input, output, scanLength - 2, token);
                await input.CopyToAsync(output, token);
                return;
            }

            if (marker == 0xd9)
            {
                await output.WriteAsync(new byte[] { 0xff, (byte)marker }, token);
                return;
            }

            if (marker is >= 0xd0 and <= 0xd7 or 0x01)
            {
                await output.WriteAsync(new byte[] { 0xff, (byte)marker }, token);
                continue;
            }

            await input.ReadExactlyAsync(lengthBytes, token);
            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);
            if (segmentLength < 2)
                throw new InvalidDataException("JPEG encoder returned an invalid segment length.");

            var data = new byte[segmentLength - 2];
            await input.ReadExactlyAsync(data, token);
            var remove = marker == 0xe1 &&
                    (data.AsSpan().StartsWith(JpegStandardXmpHeader) ||
                     data.AsSpan().StartsWith(JpegExtendedXmpHeader)) ||
                marker == 0xe2 && data.AsSpan().StartsWith(JpegIccHeader);
            if (remove)
                continue;

            await output.WriteAsync(new byte[] { 0xff, (byte)marker }, token);
            await output.WriteAsync(lengthBytes, token);
            await output.WriteAsync(data, token);
        }
    }

    private static async Task CopyExactlyAsync(
        Stream input,
        Stream output,
        int byteCount,
        CancellationToken token)
    {
        var buffer = new byte[Math.Min(byteCount, 64 * 1024)];
        var remaining = byteCount;
        while (remaining > 0)
        {
            var count = Math.Min(remaining, buffer.Length);
            await input.ReadExactlyAsync(buffer.AsMemory(0, count), token);
            await output.WriteAsync(buffer.AsMemory(0, count), token);
            remaining -= count;
        }
    }

    private static async Task CopyPngChunksAsync(Stream input, Stream output, CancellationToken token)
    {
        var header = new byte[8];
        var buffer = new byte[64 * 1024];
        while (input.Position < input.Length)
        {
            await input.ReadExactlyAsync(header, token);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            var remaining = (long)length + 4; // Data plus its original CRC.
            if (remaining > input.Length - input.Position)
                throw new InvalidDataException("PNG encoder returned a truncated chunk.");
            var type = Encoding.ASCII.GetString(header, 4, 4);
            // WIC emits sRGB/gAMA. PNG forbids simultaneous sRGB and iCCP; use only our ICC.
            if (type is "sRGB" or "gAMA" or "cHRM" or "iCCP")
            {
                input.Seek(remaining, SeekOrigin.Current);
                continue;
            }
            await output.WriteAsync(header, token);
            while (remaining > 0)
            {
                var count = (int)Math.Min(remaining, buffer.Length);
                await input.ReadExactlyAsync(buffer.AsMemory(0, count), token);
                await output.WriteAsync(buffer.AsMemory(0, count), token);
                remaining -= count;
            }
        }
    }

    private static byte[] Combine(params byte[][] pieces)
    {
        var result = new byte[pieces.Sum(piece => piece.Length)];
        var offset = 0;
        foreach (var piece in pieces)
        {
            piece.CopyTo(result, offset);
            offset += piece.Length;
        }
        return result;
    }

    private static void WriteExtendedJpegXmp(Stream stream, byte[] text, byte[] standardHeader, CancellationToken token)
    {
        // Adobe's Extended XMP stores the complete packet in ordered APP1 chunks. MD5 is
        // the format's packet identifier, not a security or source-integrity checksum.
        string identifier = Convert.ToHexString(MD5.HashData(text));
        const string noteNamespace = "http://ns.adobe.com/xmp/note/";
        var main = XmpMetaFactory.Create();
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(noteNamespace, "xmpNote");
        main.SetProperty(noteNamespace, "HasExtendedXMP", identifier);
        var packet = Encoding.UTF8.GetBytes(XmpMetaFactory.SerializeToString(main,
            new SerializeOptions { OmitPacketWrapper = true }));
        WriteJpegSegment(stream, 0xe1, Combine(standardHeader, packet));
        byte[] header = Combine("http://ns.adobe.com/xmp/extension/\0"u8.ToArray(),
            Encoding.ASCII.GetBytes(identifier), new byte[8]);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(header.Length - 8), (uint)text.Length);
        int chunkSize = 65533 - header.Length;
        for (int offset = 0; offset < text.Length; offset += chunkSize)
        {
            token.ThrowIfCancellationRequested();
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(header.Length - 4), (uint)offset);
            WriteJpegSegment(stream, 0xe1, Combine(header, text.AsSpan(offset,
                Math.Min(chunkSize, text.Length - offset)).ToArray()));
        }
    }

    private static void WriteJpegSegment(Stream stream, byte marker, byte[] data)
    {
        if (data.Length > 65533)
            throw new InvalidDataException("Descriptive metadata is too large for a JPEG derivative. Choose PNG.");
        stream.Write([0xff, marker, (byte)((data.Length + 2) >> 8), (byte)(data.Length + 2)]);
        stream.Write(data);
    }

    private static void WritePngChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        var chunk = Combine(Encoding.ASCII.GetBytes(type), data);
        stream.Write(chunk);
        uint crc = 0xffffffff;
        foreach (var value in chunk)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320);
        }
        BinaryPrimitives.WriteUInt32BigEndian(length, ~crc);
        stream.Write(length);
    }

    /// <summary>App-owned ICC v2 matrix/shaper profile, built from the sRGB standard, not an OS asset.</summary>
    private static byte[] CreateSrgbProfile()
    {
        static void UInt(byte[] data, int offset, uint value) =>
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset, 4), value);
        static void Signature(byte[] data, int offset, string value) =>
            Encoding.ASCII.GetBytes(value).CopyTo(data, offset);
        static byte[] Xyz(double x, double y, double z)
        {
            var data = new byte[20];
            Signature(data, 0, "XYZ ");
            UInt(data, 8, (uint)Math.Round(x * 65536));
            UInt(data, 12, (uint)Math.Round(y * 65536));
            UInt(data, 16, (uint)Math.Round(z * 65536));
            return data;
        }
        static byte[] Description(string text)
        {
            var ascii = Encoding.ASCII.GetBytes(text + "\0");
            var data = new byte[12 + ascii.Length + 78];
            Signature(data, 0, "desc");
            UInt(data, 8, (uint)ascii.Length);
            ascii.CopyTo(data, 12);
            return data;
        }
        var curve = new byte[12 + 4096 * 2];
        Signature(curve, 0, "curv");
        UInt(curve, 8, 4096);
        for (var i = 0; i < 4096; i++)
        {
            var value = i / 4095.0;
            var linear = value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            BinaryPrimitives.WriteUInt16BigEndian(curve.AsSpan(12 + i * 2, 2), (ushort)Math.Round(linear * 65535));
        }
        // ICC PCS is D50. These are the sRGB D65 primaries Bradford-adapted for profile tagging
        // only; this does NOT adapt the scene pixels a second time.
        var tags = new (string Name, byte[] Data)[]
        {
            ("desc", Description("PhotoLibrarian IEC 61966-2-1 sRGB")),
            ("cprt", Combine("text\0\0\0\0"u8.ToArray(), "Public mathematical sRGB color definition\0"u8.ToArray())),
            ("wtpt", Xyz(0.9642, 1, 0.8249)),
            ("rXYZ", Xyz(0.4360747, 0.2225045, 0.0139322)),
            ("gXYZ", Xyz(0.3850649, 0.7168786, 0.0971045)),
            ("bXYZ", Xyz(0.1430804, 0.0606169, 0.7141733)),
            ("rTRC", curve), ("gTRC", curve), ("bTRC", curve)
        };
        var offset = 132 + tags.Length * 12;
        var profile = new byte[offset + tags.Sum(tag => (tag.Data.Length + 3) & ~3)];
        UInt(profile, 0, (uint)profile.Length);
        UInt(profile, 8, 0x02100000);
        Signature(profile, 12, "mntr");
        Signature(profile, 16, "RGB ");
        Signature(profile, 20, "XYZ ");
        BinaryPrimitives.WriteUInt16BigEndian(profile.AsSpan(24), 2026);
        BinaryPrimitives.WriteUInt16BigEndian(profile.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16BigEndian(profile.AsSpan(28), 1);
        Signature(profile, 36, "acsp");
        Signature(profile, 40, "MSFT");
        UInt(profile, 64, 1); // Relative colorimetric.
        Xyz(0.9642, 1, 0.8249).AsSpan(8, 12).CopyTo(profile.AsSpan(68));
        UInt(profile, 128, (uint)tags.Length);
        for (var i = 0; i < tags.Length; i++)
        {
            Signature(profile, 132 + i * 12, tags[i].Name);
            UInt(profile, 136 + i * 12, (uint)offset);
            UInt(profile, 140 + i * 12, (uint)tags[i].Data.Length);
            tags[i].Data.CopyTo(profile, offset);
            offset += (tags[i].Data.Length + 3) & ~3;
        }
        return profile;
    }
}
