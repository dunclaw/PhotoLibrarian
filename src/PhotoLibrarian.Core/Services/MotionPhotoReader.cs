// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace PhotoLibrarian.Core.Services;

public sealed record MotionPhotoInfo(long VideoOffset, int VideoLength, long? PresentationTimestampUs);

/// <summary>Reads JPEG Google Motion Photo v1 and legacy Microvideo v1 without changing the source.</summary>
public static class MotionPhotoReader
{
    public const int MaximumVideoBytes = 64 * 1024 * 1024;
    private const int MaximumHeaderBytes = 4 * 1024 * 1024;
    private static readonly byte[] XmpHeader = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
    private static readonly XNamespace Camera = "http://ns.google.com/photos/1.0/camera/";
    private static readonly XNamespace Container = "http://ns.google.com/photos/1.0/container/";
    private static readonly XNamespace Item = "http://ns.google.com/photos/1.0/container/item/";
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";

    public static async Task<MotionPhotoInfo?> ReadAsync(string path, CancellationToken token = default)
    {
        await using var stream = OpenRead(path);
        return await ReadAsync(stream, token);
    }

    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.RandomAccess);

    public static async Task<MotionPhotoInfo?> ReadAsync(Stream stream, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        stream.Position = 0;
        var pair = new byte[2];
        await stream.ReadExactlyAsync(pair, token);
        if (pair[0] != 0xff || pair[1] != 0xd8) return null;

        XDocument? metadata = null;
        while (stream.Position < MaximumHeaderBytes)
        {
            await stream.ReadExactlyAsync(pair, token);
            if (pair[0] != 0xff) throw new InvalidDataException("Invalid JPEG marker.");
            // JPEG permits fill bytes between marker segments.
            while (pair[1] == 0xff)
            {
                if (stream.Position >= MaximumHeaderBytes)
                    throw new InvalidDataException("JPEG metadata exceeds the 4 MiB scan limit.");
                await stream.ReadExactlyAsync(pair.AsMemory(1, 1), token);
            }
            if (pair[1] is 0xda or 0xd9) break;
            if (pair[1] is 0x01 or >= 0xd0 and <= 0xd7) continue;
            var marker = pair[1];
            await stream.ReadExactlyAsync(pair, token);
            int length = BinaryPrimitives.ReadUInt16BigEndian(pair) - 2;
            if (length < 0 || stream.Position + length > stream.Length ||
                stream.Position + length > MaximumHeaderBytes)
                throw new InvalidDataException("JPEG metadata is truncated or exceeds the 4 MiB scan limit.");
            if (marker == 0xe1)
            {
                var bytes = new byte[length];
                await stream.ReadExactlyAsync(bytes, token);
                if (!bytes.AsSpan().StartsWith(XmpHeader)) continue;
                if (metadata is not null) throw new InvalidDataException("Multiple standard XMP packets are unsupported.");
                using var input = new MemoryStream(bytes, XmpHeader.Length, bytes.Length - XmpHeader.Length);
                using var xml = XmlReader.Create(input, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = 65536
                });
                metadata = XDocument.Load(xml);
            }
            else
                stream.Seek(length, SeekOrigin.Current);
        }
        if (stream.Position >= MaximumHeaderBytes)
            throw new InvalidDataException("JPEG metadata exceeds the 4 MiB scan limit.");
        if (metadata is null) return null;

        string? motion = Property(metadata, Camera + "MotionPhoto");
        bool modern = motion is not null;
        if ((modern ? motion : Property(metadata, Camera + "MicroVideo")) != "1") return null;
        if (Property(metadata, Camera + (modern ? "MotionPhotoVersion" : "MicroVideoVersion")) != "1")
            throw new InvalidDataException("Unsupported motion-photo version.");

        long lengthBytes;
        if (modern)
        {
            var directories = metadata.Descendants(Container + "Directory").ToList();
            if (directories.Count != 1) throw new InvalidDataException("Motion-photo directory is missing or ambiguous.");
            var sequences = directories[0].Elements(Rdf + "Seq").ToList();
            if (sequences.Count != 1)
                throw new InvalidDataException("Motion-photo directory must have one ordered sequence.");
            var items = new List<XElement>();
            foreach (var entry in sequences[0].Elements())
            {
                var children = entry.Elements().ToList();
                if (entry.Name != Rdf + "li" || children.Count != 1 || children[0].Name != Container + "Item")
                    throw new InvalidDataException("Invalid motion-photo directory item.");
                items.Add(children[0]);
            }
            if (items.Count < 2 || items.Count > 16)
                throw new InvalidDataException("Unsupported motion-photo directory.");
            if (Value(items[0], "Semantic") != "Primary" || Value(items[0], "Mime") != "image/jpeg")
                throw new InvalidDataException("Only JPEG motion-photo covers are supported.");
            long precedingLength = ParseNonNegative(Value(items[0], "Padding") ?? "0");
            if (ParseNonNegative(Value(items[0], "Length") ?? "0") != 0)
                throw new InvalidDataException("Invalid primary image length.");
            for (int i = 1; i < items.Count - 1; i++)
            {
                // Gain maps may precede the final motion item. Shared/other layouts are not guessed.
                if (Value(items[i], "Semantic") != "GainMap" || Value(items[i], "Mime") != "image/jpeg" ||
                    ParseNonNegative(Value(items[i], "Padding") ?? "0") != 0)
                    throw new InvalidDataException("Unsupported intermediate motion-photo item.");
                precedingLength = checked(precedingLength + ParsePositive(Value(items[i], "Length")));
            }
            var video = items[^1];
            if (Value(video, "Semantic") != "MotionPhoto" || Value(video, "Mime") != "video/mp4" ||
                ParseNonNegative(Value(video, "Padding") ?? "0") != 0)
                throw new InvalidDataException("Only a final embedded MP4 motion item is supported.");
            lengthBytes = ParsePositive(Value(video, "Length"));
            if (precedingLength > stream.Length - lengthBytes - stream.Position)
                throw new InvalidDataException("Motion-photo item lengths exceed the file.");
        }
        else
            lengthBytes = ParsePositive(Property(metadata, Camera + "MicroVideoOffset"));

        long offset = stream.Length - lengthBytes;
        if (lengthBytes > MaximumVideoBytes)
            throw new InvalidDataException("Embedded motion video exceeds the 64 MiB limit.");
        if (offset < stream.Position)
            throw new InvalidDataException("Motion video offset overlaps the JPEG metadata or exceeds the file.");
        string? timestamp = Property(metadata, Camera + (modern
            ? "MotionPhotoPresentationTimestampUs" : "MicroVideoPresentationTimestampUs"));
        long? timestampUs = null;
        if (timestamp is not null && timestamp != "-1")
            timestampUs = ParseNonNegative(timestamp);
        await ValidateMp4Async(stream, offset, lengthBytes, token);
        return new MotionPhotoInfo(offset, (int)lengthBytes, timestampUs);
    }

    /// <summary>Revalidates against the same open handle used for extraction; no stale offsets or disk artifacts.</summary>
    public static async Task<(MotionPhotoInfo Info, MemoryStream Video)> OpenVideoAsync(
        string path, CancellationToken token = default)
    {
        await using var stream = OpenRead(path);
        var info = await ReadAsync(stream, token)
            ?? throw new InvalidDataException("This file no longer contains a supported motion photo.");
        token.ThrowIfCancellationRequested();
        var bytes = new byte[info.VideoLength];
        stream.Position = info.VideoOffset;
        await stream.ReadExactlyAsync(bytes, token);
        token.ThrowIfCancellationRequested();
        return (info, new MemoryStream(bytes, writable: false));
    }

    private static string? Value(XElement element, string name)
    {
        var values = element.Attributes(Item + name).Select(attribute => attribute.Value)
            .Concat(element.Elements(Item + name).Select(property => property.Value)).ToList();
        if (values.Count > 1) throw new InvalidDataException($"Ambiguous motion-photo item property: {name}.");
        return values.SingleOrDefault();
    }

    private static string? Property(XDocument document, XName name)
    {
        var values = document.Descendants().Attributes(name).Select(attribute => attribute.Value)
            .Concat(document.Descendants(name).Select(element => element.Value)).ToList();
        if (values.Count > 1) throw new InvalidDataException($"Ambiguous motion-photo property: {name.LocalName}.");
        return values.SingleOrDefault();
    }

    private static long ParseNonNegative(string? text)
    {
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long value) || value < 0)
            throw new InvalidDataException("Invalid motion-photo length or timestamp.");
        return value;
    }

    private static long ParsePositive(string? text)
    {
        long value = ParseNonNegative(text);
        if (value == 0) throw new InvalidDataException("Motion-photo items must not be empty.");
        return value;
    }

    private static async Task ValidateMp4Async(Stream stream, long offset, long length, CancellationToken token)
    {
        var header = new byte[16];
        long end = offset + length;
        bool first = true, movie = false, media = false;
        stream.Position = offset;
        int boxes = 0;
        while (stream.Position < end)
        {
            if (++boxes > 4096 || end - stream.Position < 8)
                throw new InvalidDataException("Invalid or excessive MP4 boxes.");
            long start = stream.Position;
            await stream.ReadExactlyAsync(header.AsMemory(0, 8), token);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            string type = Encoding.ASCII.GetString(header, 4, 4);
            int headerSize = 8;
            if (size == 1)
            {
                if (end - stream.Position < 8) throw new InvalidDataException("Truncated MP4 extended box.");
                await stream.ReadExactlyAsync(header.AsMemory(8, 8), token);
                ulong extended = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                if (extended > long.MaxValue) throw new InvalidDataException("Invalid MP4 box length.");
                size = (long)extended;
                headerSize = 16;
            }
            if (size == 0) size = end - start;
            if (size < headerSize || size > end - start ||
                (first && (type != "ftyp" || size < headerSize + 8)))
                throw new InvalidDataException("Motion video is not a complete MP4 container.");
            movie |= type == "moov";
            media |= type == "mdat";
            first = false;
            stream.Position = start + size;
        }
        if (!movie || !media) throw new InvalidDataException("Motion video is missing movie or media data.");
    }
}
