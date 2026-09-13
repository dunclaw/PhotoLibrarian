using MetadataExtractor;
using MetadataExtractor.Formats.Xmp;
using PhotoLibrarian.Core.Models;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using XmpCore;
using XmpCore.Options;

namespace PhotoLibrarian.Core.Services;

public sealed class FaceMetadataStore : IFaceMetadataStore
{
    private const string MwgRs = CropMetadataRemapper.MwgRs;
    private const string StArea = CropMetadataRemapper.StArea;
    private const string StDim = CropMetadataRemapper.StDim;
    private const string PhotoLibrarian = MetadataWriterService.PhotoLibrarianNamespace;
    private const string ReviewProperty = "plib:FaceReview";
    private const string AuthorityProperty = "plib:FaceMetadataAuthority";
    private const string JpegXmpHeader = "http://ns.adobe.com/xap/1.0/\0";
    private const string JpegExtendedXmpHeader = "http://ns.adobe.com/xmp/extension/\0";
    private const string PngXmpKeyword = "XML:com.adobe.xmp";
    private const double GeometryMatchThreshold = 0.5;

    static FaceMetadataStore()
    {
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(MwgRs, "mwg-rs");
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(StArea, "stArea");
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(StDim, "stDim");
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(PhotoLibrarian, "plib");
    }

    public async Task WriteAsync(
        string imagePath,
        PhotoFaceMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(metadata);

        await Task.Run(
            () => WriteCore(imagePath, metadata, cancellationToken),
            cancellationToken);
    }

    public async Task WriteBatchAsync(
        IReadOnlyCollection<(string ImagePath, PhotoFaceMetadata Metadata)> writes,
        CancellationToken cancellationToken = default)
    {
        var distinctWrites = writes
            .GroupBy(write => write.ImagePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();
        var backups = new List<FileBackup>();
        try
        {
            foreach (var write in distinctWrites)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (CanWriteEmbedded(write.ImagePath) &&
                    File.Exists(write.ImagePath))
                {
                    backups.Add(CreateBackup(write.ImagePath));
                }
                backups.Add(CreateBackup(
                    GetSidecarPathForImage(write.ImagePath)));
            }

            await Task.Run(
                () =>
                {
                    foreach (var write in distinctWrites)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        WriteCore(
                            write.ImagePath,
                            write.Metadata,
                            cancellationToken);
                    }
                },
                cancellationToken);
        }
        catch
        {
            foreach (var backup in backups)
            {
                backup.Restore();
            }
            throw;
        }
        finally
        {
            foreach (var backup in backups)
            {
                backup.Dispose();
            }
        }
    }

    public PhotoFaceMetadata Read(string imagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        var documents = new List<PhotoFaceMetadata>();
        if (File.Exists(imagePath))
        {
            try
            {
                var embedded = ImageMetadataReader.ReadMetadata(imagePath)
                    .OfType<XmpDirectory>()
                    .Where(directory => directory.XmpMeta is not null)
                    .Select(directory => ReadXmp(directory.XmpMeta!))
                    .Where(metadata => metadata.Faces.Count > 0)
                    .ToList();
                documents.AddRange(embedded);
            }
            catch (ImageProcessingException)
            {
            }
            catch (IOException)
            {
            }
        }

        var sidecarPath = GetSidecarPathForImage(imagePath);
        if (File.Exists(sidecarPath))
        {
            var sidecar = XmpMetaFactory.ParseFromString(File.ReadAllText(sidecarPath));
            var sidecarMetadata = ReadXmp(sidecar);
            if (sidecar.DoesPropertyExist(
                    PhotoLibrarian,
                    AuthorityProperty))
            {
                return sidecarMetadata;
            }
            if (sidecarMetadata.Faces.Count > 0)
            {
                documents.Add(sidecarMetadata);
            }
        }

        return Merge(documents);
    }

    public static void RemapReviewMetadata(
        IXmpMeta xmp,
        uint sourceWidth,
        uint sourceHeight,
        CropRectangle crop)
    {
        if (!xmp.DoesPropertyExist(PhotoLibrarian, ReviewProperty)) return;

        var review = JsonSerializer.Deserialize<FaceReviewDocument>(
            xmp.GetPropertyString(PhotoLibrarian, ReviewProperty));
        if (review?.Version != 1) return;

        var remappedFaces = new List<FaceReviewEntry>();
        foreach (var face in review.Faces)
        {
            var remapped = CropMetadataRemapper.RemapNormalizedRegion(
                new NormalizedRegion(
                    face.X,
                    face.Y,
                    face.Width,
                    face.Height),
                sourceWidth,
                sourceHeight,
                crop);
            if (remapped is null) continue;

            remappedFaces.Add(face with
            {
                X = remapped.Value.X,
                Y = remapped.Value.Y,
                Width = remapped.Value.Width,
                Height = remapped.Value.Height
            });
        }

        if (remappedFaces.Count == 0)
        {
            xmp.DeleteProperty(PhotoLibrarian, ReviewProperty);
            return;
        }

        xmp.SetProperty(
            PhotoLibrarian,
            ReviewProperty,
            JsonSerializer.Serialize(
                new FaceReviewDocument(review.Version, remappedFaces)));
    }

    private static void WriteCore(
        string imagePath,
        PhotoFaceMetadata metadata,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var xmp = LoadEmbeddedXmp(imagePath) ?? XmpMetaFactory.Create();
        ApplyMetadata(xmp, metadata);
        xmp.DeleteProperty(PhotoLibrarian, AuthorityProperty);
        var serialized = XmpMetaFactory.SerializeToString(xmp, new SerializeOptions());

        var embedded = false;
        try
        {
            embedded = TryWriteEmbedded(imagePath, serialized);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }

        if (embedded)
        {
            ClearSidecarFaceMetadata(imagePath);
            return;
        }

        var sidecar = LoadSidecar(imagePath);
        ApplyMetadata(sidecar, metadata);
        sidecar.SetProperty(PhotoLibrarian, AuthorityProperty, "True");
        SaveSidecar(imagePath, sidecar);
    }

    private static IXmpMeta? LoadEmbeddedXmp(string imagePath)
    {
        if (!File.Exists(imagePath)) return null;

        try
        {
            return ImageMetadataReader.ReadMetadata(imagePath)
                .OfType<XmpDirectory>()
                .FirstOrDefault(directory => directory.XmpMeta is not null)
                ?.XmpMeta;
        }
        catch (ImageProcessingException)
        {
            return null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IXmpMeta LoadSidecar(string imagePath)
    {
        var sidecarPath = GetSidecarPathForImage(imagePath);
        return File.Exists(sidecarPath)
            ? XmpMetaFactory.ParseFromString(File.ReadAllText(sidecarPath))
            : XmpMetaFactory.Create();
    }

    private static void ApplyMetadata(IXmpMeta xmp, PhotoFaceMetadata metadata)
    {
        const string regionsPath = "Regions";
        const string regionListPath = "Regions/mwg-rs:RegionList";
        RemoveFaceRegions(xmp);
        xmp.DeleteProperty(PhotoLibrarian, ReviewProperty);

        if (metadata.Faces.Count == 0) return;

        xmp.SetStructField(
            MwgRs,
            regionsPath,
            MwgRs,
            "AppliedToDimensions",
            null,
            new PropertyOptions { IsStruct = true });
        var dimensionsPath = $"{regionsPath}/mwg-rs:AppliedToDimensions";
        xmp.SetStructField(
            MwgRs,
            dimensionsPath,
            StDim,
            "w",
            Math.Max(1, metadata.ImageWidth).ToString(CultureInfo.InvariantCulture));
        xmp.SetStructField(
            MwgRs,
            dimensionsPath,
            StDim,
            "h",
            Math.Max(1, metadata.ImageHeight).ToString(CultureInfo.InvariantCulture));
        xmp.SetStructField(MwgRs, dimensionsPath, StDim, "unit", "pixel");

        var existingRegionCount = xmp.CountArrayItems(MwgRs, regionListPath);
        for (var index = 0; index < metadata.Faces.Count; index++)
        {
            var face = metadata.Faces[index];
            xmp.AppendArrayItem(
                MwgRs,
                regionListPath,
                new PropertyOptions { IsArray = true, IsArrayOrdered = true },
                null,
                new PropertyOptions { IsStruct = true });
            var itemPath =
                $"{regionListPath}[{existingRegionCount + index + 1}]";
            if (!string.IsNullOrWhiteSpace(face.PersonName))
            {
                xmp.SetStructField(MwgRs, itemPath, MwgRs, "Name", face.PersonName);
            }
            xmp.SetStructField(MwgRs, itemPath, MwgRs, "Type", "Face");
            xmp.SetStructField(
                MwgRs,
                itemPath,
                MwgRs,
                "Area",
                null,
                new PropertyOptions { IsStruct = true });
            var areaPath = $"{itemPath}/mwg-rs:Area";
            SetArea(xmp, areaPath, "x", face.X + (face.Width / 2));
            SetArea(xmp, areaPath, "y", face.Y + (face.Height / 2));
            SetArea(xmp, areaPath, "w", face.Width);
            SetArea(xmp, areaPath, "h", face.Height);
            xmp.SetStructField(MwgRs, areaPath, StArea, "unit", "normalized");
        }

        var review = new FaceReviewDocument(
            1,
            metadata.Faces
                .Where(face =>
                    face.SuggestionsHidden ||
                    face.PersonSuggestionsHidden ||
                    face.RejectedPersonNames.Count > 0)
                .Select(face => new FaceReviewEntry(
                    face.X,
                    face.Y,
                    face.Width,
                    face.Height,
                    face.PersonName,
                    face.SuggestionsHidden,
                    face.PersonSuggestionsHidden,
                    face.RejectedPersonNames
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .ToArray()))
                .ToArray());
        if (review.Faces.Count > 0)
        {
            xmp.SetProperty(
                PhotoLibrarian,
                ReviewProperty,
                JsonSerializer.Serialize(review));
        }
    }

    private static PhotoFaceMetadata ReadXmp(IXmpMeta xmp)
    {
        const string regionsPath = "Regions";
        const string regionListPath = "Regions/mwg-rs:RegionList";
        var dimensionsPath = $"{regionsPath}/mwg-rs:AppliedToDimensions";
        var imageWidth = ReadPositiveInt(xmp, dimensionsPath, StDim, "w");
        var imageHeight = ReadPositiveInt(xmp, dimensionsPath, StDim, "h");
        var faces = new List<PortableFaceMetadata>();

        for (var index = 1; index <= xmp.CountArrayItems(MwgRs, regionListPath); index++)
        {
            var itemPath = $"{regionListPath}[{index}]";
            var type = xmp.GetStructField(MwgRs, itemPath, MwgRs, "Type")?.Value;
            if (!string.IsNullOrWhiteSpace(type) &&
                !string.Equals(type, "Face", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var areaPath = $"{itemPath}/mwg-rs:Area";
            if (!TryReadArea(xmp, areaPath, imageWidth, imageHeight, out var area))
            {
                continue;
            }

            faces.Add(new PortableFaceMetadata(
                0,
                area.X,
                area.Y,
                area.Width,
                area.Height,
                NullIfWhiteSpace(
                    xmp.GetStructField(MwgRs, itemPath, MwgRs, "Name")?.Value),
                false,
                false,
                []));
        }

        if (xmp.DoesPropertyExist(PhotoLibrarian, ReviewProperty))
        {
            var json = xmp.GetPropertyString(PhotoLibrarian, ReviewProperty);
            var review = JsonSerializer.Deserialize<FaceReviewDocument>(json);
            if (review?.Version == 1)
            {
                foreach (var decision in review.Faces)
                {
                    var decisionFace = new PortableFaceMetadata(
                        0,
                        decision.X,
                        decision.Y,
                        decision.Width,
                        decision.Height,
                        NullIfWhiteSpace(decision.PersonName),
                        decision.SuggestionsHidden,
                        decision.PersonSuggestionsHidden,
                        decision.RejectedPersonNames ?? []);
                    var matchIndex = FindBestMatch(faces, decisionFace);
                    if (matchIndex < 0)
                    {
                        faces.Add(decisionFace);
                        continue;
                    }

                    var existing = faces[matchIndex];
                    faces[matchIndex] = existing with
                    {
                        PersonName = existing.PersonName ?? decisionFace.PersonName,
                        SuggestionsHidden = decisionFace.SuggestionsHidden,
                        PersonSuggestionsHidden = decisionFace.PersonSuggestionsHidden,
                        RejectedPersonNames = decisionFace.RejectedPersonNames
                    };
                }
            }
        }

        return new PhotoFaceMetadata(imageWidth, imageHeight, faces);
    }

    private static PhotoFaceMetadata Merge(IReadOnlyList<PhotoFaceMetadata> documents)
    {
        if (documents.Count == 0) return new PhotoFaceMetadata(0, 0, []);

        var faces = new List<PortableFaceMetadata>();
        foreach (var document in documents)
        {
            foreach (var face in document.Faces)
            {
                var matchIndex = FindBestMatch(faces, face);
                if (matchIndex < 0)
                {
                    faces.Add(face);
                    continue;
                }

                faces[matchIndex] = face;
            }
        }

        return new PhotoFaceMetadata(
            documents.Select(document => document.ImageWidth).FirstOrDefault(value => value > 0),
            documents.Select(document => document.ImageHeight).FirstOrDefault(value => value > 0),
            faces);
    }

    private static int FindBestMatch(
        IReadOnlyList<PortableFaceMetadata> faces,
        PortableFaceMetadata candidate)
    {
        var bestIndex = -1;
        var bestOverlap = GeometryMatchThreshold;
        for (var index = 0; index < faces.Count; index++)
        {
            var overlap = IntersectionOverUnion(faces[index], candidate);
            if (overlap < bestOverlap) continue;
            bestOverlap = overlap;
            bestIndex = index;
        }
        return bestIndex;
    }

    private static bool TryReadArea(
        IXmpMeta xmp,
        string areaPath,
        int imageWidth,
        int imageHeight,
        out (double X, double Y, double Width, double Height) area)
    {
        area = default;
        if (!TryReadDouble(xmp, areaPath, "x", out var centerX) ||
            !TryReadDouble(xmp, areaPath, "y", out var centerY) ||
            !TryReadDouble(xmp, areaPath, "w", out var width) ||
            !TryReadDouble(xmp, areaPath, "h", out var height))
        {
            return false;
        }

        var unit = xmp.GetStructField(MwgRs, areaPath, StArea, "unit")?.Value;
        if (string.Equals(unit, "pixel", StringComparison.OrdinalIgnoreCase))
        {
            if (imageWidth <= 0 || imageHeight <= 0) return false;
            centerX /= imageWidth;
            width /= imageWidth;
            centerY /= imageHeight;
            height /= imageHeight;
        }
        else if (!string.IsNullOrWhiteSpace(unit) &&
                 !string.Equals(unit, "normalized", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var x = centerX - (width / 2);
        var y = centerY - (height / 2);
        if (!double.IsFinite(x) ||
            !double.IsFinite(y) ||
            !double.IsFinite(width) ||
            !double.IsFinite(height) ||
            width <= 0 ||
            height <= 0)
        {
            return false;
        }

        area = (x, y, width, height);
        return true;
    }

    private static bool TryReadDouble(
        IXmpMeta xmp,
        string areaPath,
        string field,
        out double value) =>
        double.TryParse(
            xmp.GetStructField(MwgRs, areaPath, StArea, field)?.Value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value);

    private static int ReadPositiveInt(
        IXmpMeta xmp,
        string path,
        string fieldNamespace,
        string field) =>
        int.TryParse(
            xmp.GetStructField(MwgRs, path, fieldNamespace, field)?.Value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value) && value > 0
            ? value
            : 0;

    private static void SetArea(
        IXmpMeta xmp,
        string areaPath,
        string field,
        double value) =>
        xmp.SetStructField(
            MwgRs,
            areaPath,
            StArea,
            field,
            value.ToString("F6", CultureInfo.InvariantCulture));

    private static double IntersectionOverUnion(
        PortableFaceMetadata first,
        PortableFaceMetadata second)
    {
        var left = Math.Max(first.X, second.X);
        var top = Math.Max(first.Y, second.Y);
        var right = Math.Min(first.X + first.Width, second.X + second.Width);
        var bottom = Math.Min(first.Y + first.Height, second.Y + second.Height);
        var intersection =
            Math.Max(0, right - left) * Math.Max(0, bottom - top);
        var union =
            first.Width * first.Height +
            second.Width * second.Height -
            intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool TryWriteEmbedded(string imagePath, string xmp)
    {
        if (!File.Exists(imagePath)) return false;

        return Path.GetExtension(imagePath).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".jpe" or ".jfif" =>
                TryWriteJpegXmp(imagePath, xmp),
            ".png" => TryWritePngXmp(imagePath, xmp),
            _ => false
        };
    }

    private static bool CanWriteEmbedded(string imagePath) =>
        Path.GetExtension(imagePath).ToLowerInvariant() is
            ".jpg" or ".jpeg" or ".jpe" or ".jfif" or ".png";

    private static bool TryWriteJpegXmp(string imagePath, string xmp)
    {
        var bytes = File.ReadAllBytes(imagePath);
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8)
        {
            return false;
        }

        var xmpHeader = Encoding.ASCII.GetBytes(JpegXmpHeader);
        var extendedHeader = Encoding.ASCII.GetBytes(JpegExtendedXmpHeader);
        if (Contains(bytes, extendedHeader)) return false;

        var packet = Encoding.UTF8.GetBytes(xmp);
        var payloadLength = xmpHeader.Length + packet.Length;
        if (payloadLength > ushort.MaxValue - 2) return false;

        using var output = new MemoryStream(bytes.Length + payloadLength + 4);
        output.Write(bytes, 0, 2);
        var replaced = false;
        var offset = 2;
        while (offset + 4 <= bytes.Length && bytes[offset] == 0xFF)
        {
            var marker = bytes[offset + 1];
            if (marker is 0xDA or 0xD9) break;
            if (marker is >= 0xD0 and <= 0xD7 or 0x01)
            {
                output.Write(bytes, offset, 2);
                offset += 2;
                continue;
            }

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(
                bytes.AsSpan(offset + 2, 2));
            var totalLength = segmentLength + 2;
            if (segmentLength < 2 || offset + totalLength > bytes.Length)
            {
                return false;
            }

            var isXmp = marker == 0xE1 &&
                        bytes.AsSpan(offset + 4, segmentLength - 2)
                            .StartsWith(xmpHeader);
            if (isXmp)
            {
                if (!replaced)
                {
                    WriteJpegSegment(output, xmpHeader, packet);
                    replaced = true;
                }
            }
            else
            {
                output.Write(bytes, offset, totalLength);
            }
            offset += totalLength;
        }

        if (!replaced)
        {
            WriteJpegSegment(output, xmpHeader, packet);
        }
        output.Write(bytes, offset, bytes.Length - offset);
        ReplaceFile(imagePath, output.ToArray());
        return true;
    }

    private static void WriteJpegSegment(
        Stream output,
        byte[] header,
        byte[] packet)
    {
        output.WriteByte(0xFF);
        output.WriteByte(0xE1);
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(
            length,
            checked((ushort)(header.Length + packet.Length + 2)));
        output.Write(length);
        output.Write(header);
        output.Write(packet);
    }

    private static bool TryWritePngXmp(string imagePath, string xmp)
    {
        var bytes = File.ReadAllBytes(imagePath);
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length < signature.Length ||
            !bytes.AsSpan(0, signature.Length).SequenceEqual(signature))
        {
            return false;
        }

        var xmpData = BuildPngXmpData(xmp);
        using var output = new MemoryStream(bytes.Length + xmpData.Length + 12);
        output.Write(signature);
        var offset = signature.Length;
        var wroteXmp = false;
        while (offset + 12 <= bytes.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(
                bytes.AsSpan(offset, 4));
            if (length < 0 || offset + length + 12 > bytes.Length)
            {
                return false;
            }

            var type = bytes.AsSpan(offset + 4, 4);
            var data = bytes.AsSpan(offset + 8, length);
            var isXmp = type.SequenceEqual("iTXt"u8) &&
                        data.StartsWith(Encoding.UTF8.GetBytes(PngXmpKeyword + "\0"));
            if (isXmp)
            {
                if (!wroteXmp)
                {
                    WritePngChunk(output, "iTXt"u8, xmpData);
                    wroteXmp = true;
                }
            }
            else
            {
                if (!wroteXmp && type.SequenceEqual("IEND"u8))
                {
                    WritePngChunk(output, "iTXt"u8, xmpData);
                    wroteXmp = true;
                }
                output.Write(bytes, offset, length + 12);
            }

            offset += length + 12;
            if (type.SequenceEqual("IEND"u8)) break;
        }

        if (!wroteXmp) return false;
        ReplaceFile(imagePath, output.ToArray());
        return true;
    }

    private static byte[] BuildPngXmpData(string xmp)
    {
        using var data = new MemoryStream();
        data.Write(Encoding.UTF8.GetBytes(PngXmpKeyword));
        data.WriteByte(0);
        data.WriteByte(0);
        data.WriteByte(0);
        data.WriteByte(0);
        data.WriteByte(0);
        data.Write(Encoding.UTF8.GetBytes(xmp));
        return data.ToArray();
    }

    private static void WritePngChunk(
        Stream output,
        ReadOnlySpan<byte> type,
        ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        output.Write(type);
        output.Write(data);

        var crcData = new byte[type.Length + data.Length];
        type.CopyTo(crcData);
        data.CopyTo(crcData.AsSpan(type.Length));
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, ComputeCrc32(crcData));
        output.Write(crc);
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
            }
        }
        return ~crc;
    }

    private static bool Contains(byte[] source, byte[] value) =>
        source.AsSpan().IndexOf(value) >= 0;

    private static void ReplaceFile(string path, byte[] contents)
    {
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporaryPath, contents);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static void ClearSidecarFaceMetadata(string imagePath)
    {
        var sidecarPath = GetSidecarPathForImage(imagePath);
        if (!File.Exists(sidecarPath)) return;

        var sidecar = XmpMetaFactory.ParseFromString(File.ReadAllText(sidecarPath));
        RemoveFaceRegions(sidecar);
        sidecar.DeleteProperty(PhotoLibrarian, ReviewProperty);
        sidecar.DeleteProperty(PhotoLibrarian, AuthorityProperty);
        SaveSidecar(imagePath, sidecar);
    }

    private static void RemoveFaceRegions(IXmpMeta xmp)
    {
        const string regionListPath = "Regions/mwg-rs:RegionList";
        for (var index = xmp.CountArrayItems(MwgRs, regionListPath);
             index >= 1;
             index--)
        {
            var itemPath = $"{regionListPath}[{index}]";
            var type = xmp.GetStructField(MwgRs, itemPath, MwgRs, "Type")?.Value;
            if (string.IsNullOrWhiteSpace(type) ||
                string.Equals(type, "Face", StringComparison.OrdinalIgnoreCase))
            {
                xmp.DeleteArrayItem(MwgRs, regionListPath, index);
            }
        }
    }

    private static void SaveSidecar(string imagePath, IXmpMeta xmp)
    {
        var serialized = XmpMetaFactory.SerializeToString(xmp, new SerializeOptions());
        var sidecarPath = GetSidecarPathForImage(imagePath);
        var temporaryPath = $"{sidecarPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, serialized);
            File.Move(temporaryPath, sidecarPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static string GetSidecarPathForImage(string imagePath) =>
        File.Exists(imagePath) && CanWriteEmbedded(imagePath)
            ? $"{imagePath}.xmp"
            : Path.ChangeExtension(imagePath, ".xmp");

    private static FileBackup CreateBackup(string path)
    {
        if (!File.Exists(path)) return new FileBackup(path, null);

        var backupPath = Path.Combine(
            Path.GetTempPath(),
            $"PhotoLibrarian-{Guid.NewGuid():N}.backup");
        File.Copy(path, backupPath);
        return new FileBackup(path, backupPath);
    }

    private sealed record FaceReviewDocument(
        int Version,
        IReadOnlyList<FaceReviewEntry> Faces);

    private sealed record FaceReviewEntry(
        double X,
        double Y,
        double Width,
        double Height,
        string? PersonName,
        bool SuggestionsHidden,
        bool PersonSuggestionsHidden,
        IReadOnlyList<string>? RejectedPersonNames);

    private sealed class FileBackup(string path, string? backupPath) : IDisposable
    {
        public void Restore()
        {
            if (backupPath is null)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            File.Copy(backupPath, path, true);
        }

        public void Dispose()
        {
            if (backupPath is not null && File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
        }
    }
}
