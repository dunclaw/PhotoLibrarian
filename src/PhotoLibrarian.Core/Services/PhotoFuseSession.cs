// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Runtime.InteropServices.WindowsRuntime;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using MetadataExtractor;
using MetadataExtractor.Formats.Xmp;
using Windows.Graphics.Imaging;
using XmpCore;
using XmpCore.Options;
using System.Globalization;
using System.Xml.Linq;

namespace PhotoLibrarian.Core.Services;

/// <summary>Immutable accepted pixels: the export is exactly this preview, never a second render.</summary>
public sealed class PhotoFusePreview
{
    internal PhotoFuseSession Owner { get; }
    internal byte[] Pixels { get; }
    internal PhotoFuseRegion[] Regions { get; }
    internal PhotoFusePreview(PhotoFuseSession owner, byte[] pixels, PhotoFuseRegion[] regions) =>
        (Owner, Pixels, Regions) = (owner, pixels, regions);
    public byte[] CopyPixels() => (byte[])Pixels.Clone();
}

/// <summary>
/// Owns bounded oriented sRGB snapshots and read leases until the dialog closes. Source files and
/// existing sidecars cannot be changed or replaced while a preview is being reviewed.
/// </summary>
public sealed class PhotoFuseSession : IDisposable
{
    public const int MaximumDimension = 1600;
    // Frame count is determined by decoded memory, not the number of nearby photos searched.
    public const long MaximumFrameBytes = 192L * 1024 * 1024;
    internal const string ProvenanceNamespace = "https://photolibrarian.app/ns/photo-fuse/1.0/";
    private readonly List<FileStream> _leases = [];
    private readonly List<string> _absentSidecars = [];
    private readonly List<byte[]> _frames = [];
    private readonly List<PhotoFuseTransform> _transforms = [];
    private readonly List<string> _names = [];
    private readonly List<string> _hashes = [];
    private bool _disposed;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public IReadOnlyList<string> SourceNames => _names.AsReadOnly();
    public IReadOnlyList<string> SourceHashes => _hashes.AsReadOnly();

    public (double X, double Y) DonorPointAt(int frame, double x, double y) =>
        _transforms[frame].SourceAt(x, y, Width, Height);

    private PhotoFuseSession() { }

    public static bool IsSupported(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png";

    public static async Task<PhotoFuseSession> PrepareAsync(IReadOnlyList<string> paths,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var snapshot = paths.Select(Path.GetFullPath).ToArray();
        if (snapshot.Length < 1 ||
            snapshot.Distinct(StringComparer.OrdinalIgnoreCase).Count() != snapshot.Length)
            throw new ArgumentException("Choose distinct source photos.");
        var session = new PhotoFuseSession();
        uint originalWidth = 0, originalHeight = 0;
        try
        {
            foreach (var path in snapshot)
            {
                token.ThrowIfCancellationRequested();
                if (!IsSupported(path))
                    throw new NotSupportedException("Photo Fuse supports only opaque JPEG and 8-bit PNG stills.");
                var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                session._leases.Add(lease);
                if (lease.Length > 100 * 1024 * 1024)
                    throw new NotSupportedException("Each compressed source must be at most 100 MiB.");
                if (Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))
                    RejectAnimatedPng(lease);
                else
                {
                    var signature = new byte[3];
                    lease.ReadExactly(signature);
                    lease.Position = 0;
                    if (!signature.AsSpan().SequenceEqual(new byte[] { 255, 216, 255 }))
                        throw new InvalidDataException("A selected JPEG has an invalid signature.");
                }
                foreach (var sidecar in new[] { Path.ChangeExtension(path, ".xmp"), path + ".xmp" })
                {
                    if (File.Exists(sidecar))
                    {
                        var sidecarLease = new FileStream(sidecar, FileMode.Open, FileAccess.Read, FileShare.Read);
                        session._leases.Add(sidecarLease);
                        if (sidecarLease.Length > 4 * 1024 * 1024)
                            throw new InvalidDataException("A source metadata sidecar is too large to inspect safely.");
                        RejectRecipe(XmpMetaFactory.Parse(sidecarLease));
                    }
                    else session._absentSidecars.Add(sidecar);
                }
                var metadata = ImageMetadataReader.ReadMetadata(path);
                foreach (var xmp in metadata.OfType<XmpDirectory>())
                {
                    if (xmp.HasError || xmp.XmpMeta is null)
                        throw new InvalidDataException("Source XMP could not be read. Resolve its metadata before fusing.");
                    RejectRecipe(xmp.XmpMeta);
                }
                // Decode with the same lease used for hashing; replacement races cannot change the image.
                session._hashes.Add(Convert.ToHexString(await SHA256.HashDataAsync(lease, token)));
                lease.Position = 0;
                var decoder = await BitmapDecoder.CreateAsync(lease.AsRandomAccessStream()).AsTask(token);
                if (decoder.FrameCount != 1 || decoder.OrientedPixelWidth < 192 || decoder.OrientedPixelHeight < 192 ||
                    (long)decoder.PixelWidth * decoder.PixelHeight > 100_000_000 ||
                    decoder.BitmapPixelFormat is not (BitmapPixelFormat.Bgra8 or BitmapPixelFormat.Rgba8 or
                        BitmapPixelFormat.Gray8))
                    throw new NotSupportedException("Use single-frame 8-bit stills between 192 pixels and 100 megapixels.");
                if (originalWidth == 0)
                    (originalWidth, originalHeight) = (decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
                else if (originalWidth != decoder.OrientedPixelWidth || originalHeight != decoder.OrientedPixelHeight)
                    throw new NotSupportedException("All shots must have identical oriented source dimensions.");
                double scale = Math.Min(1, MaximumDimension / (double)Math.Max(
                    decoder.OrientedPixelWidth, decoder.OrientedPixelHeight));
                long frameBytes = checked((long)Math.Round(decoder.OrientedPixelWidth * scale) *
                    (long)Math.Round(decoder.OrientedPixelHeight * scale) * 4);
                if (frameBytes * (session._frames.Count + 1) > MaximumFrameBytes)
                    throw new NotSupportedException("The selected replacements exceed Photo Fuse's decoded-memory budget. " +
                        "Use fewer different donor photos; nearby-photo discovery itself has no photo-count limit.");
                var transform = new BitmapTransform
                {
                    ScaledWidth = Math.Max(1, (uint)Math.Round(decoder.PixelWidth * scale)),
                    ScaledHeight = Math.Max(1, (uint)Math.Round(decoder.PixelHeight * scale)),
                    InterpolationMode = BitmapInterpolationMode.Fant
                };
                var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    transform, ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.ColorManageToSRgb).AsTask(token);
                int width = (int)Math.Round(decoder.OrientedPixelWidth * scale);
                int height = (int)Math.Round(decoder.OrientedPixelHeight * scale);
                byte[] pixels = data.DetachPixelData();
                if (pixels.Length != checked(width * height * 4))
                    throw new InvalidDataException("The decoded orientation or dimensions are inconsistent.");
                for (int i = 3; i < pixels.Length; i += 4)
                    if (pixels[i] != 255)
                        throw new NotSupportedException("Transparent sources are not supported.");
                if (session._frames.Count == 0)
                    (session.Width, session.Height) = (width, height);
                else if (session.Width != width || session.Height != height)
                    throw new NotSupportedException("All shots must have identical oriented preview dimensions and aspect ratio.");
                var registration = session._frames.Count == 0 ? default : await Task.Run(
                    () => PhotoFuseRegistration.Register(pixels, session._frames[0], width, height, token), token);
                session._transforms.Add(registration);
                session._frames.Add(pixels);
                session._names.Add(Path.GetFileName(path));
            }
            token.ThrowIfCancellationRequested();
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private static void RejectRecipe(IXmpMeta xmp)
    {
        string xml = XmpMetaFactory.SerializeToString(xmp, new SerializeOptions { OmitPacketWrapper = true });
        var defaults = XmpMetaFactory.Create();
        EditParametersSerializer.WriteToXmp(defaults, new Models.EditParameters());
        var defaultValues = Properties(XmpMetaFactory.SerializeToString(defaults,
            new SerializeOptions { OmitPacketWrapper = true }), Models.EditParameters.XmpNamespace)
            .ToDictionary(property => property.Name, property => property.Value);
        foreach (var property in Properties(xml, Models.EditParameters.XmpNamespace))
        {
            if (!defaultValues.TryGetValue(property.Name, out var value) || !SameValue(value, property.Value))
                throw new NotSupportedException("This photo has unapplied PhotoLibrarian adjustments. " +
                    "Apply them or make an adjusted copy before using Photo Fuse. The original has not been changed.");
        }
        // An Adobe namespace declaration, camera profile or processing history is not an edit.
        // JPEGs commonly carry these even when the user has never edited them.
        foreach (var property in Properties(xml, "http://ns.adobe.com/camera-raw-settings/1.0/"))
        {
            if (property.Name is "HasSettings" or "HasCrop" &&
                property.Value.Equals("True", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("This photo's metadata marks Adobe adjustments as pending. " +
                    "Use a rendered JPEG/PNG copy with those pending settings cleared before fusing.");
        }
    }

    private static IEnumerable<(string Name, string Value)> Properties(string xml, string ns)
    {
        var document = XDocument.Parse(xml.TrimStart('\uFEFF'));
        foreach (var element in document.Descendants())
        {
            if (element.Name.NamespaceName == ns)
                yield return (element.Name.LocalName, element.Value);
            foreach (var attribute in element.Attributes().Where(attribute =>
                !attribute.IsNamespaceDeclaration && attribute.Name.NamespaceName == ns))
                yield return (attribute.Name.LocalName, attribute.Value);
        }
    }

    private static bool SameValue(string left, string right) =>
        left == right || (double.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) &&
            double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out var b) &&
            double.IsFinite(a) && a == b);

    private static void RejectAnimatedPng(FileStream stream)
    {
        Span<byte> header = stackalloc byte[8];
        stream.ReadExactly(header);
        if (!header.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidDataException("The selected PNG has an invalid signature.");
        while (stream.Position < stream.Length)
        {
            stream.ReadExactly(header);
            long length = BinaryPrimitives.ReadUInt32BigEndian(header) + 4L;
            if (length > stream.Length - stream.Position)
                throw new InvalidDataException("The selected PNG is truncated.");
            if (header[4..].SequenceEqual("acTL"u8))
                throw new NotSupportedException("Animated PNG sources are not supported.");
            // APNG's animation control chunk must precede the first image data.
            if (header[4..].SequenceEqual("IDAT"u8)) break;
            stream.Seek(length, SeekOrigin.Current);
        }
        stream.Position = 0;
    }

    public Task<byte[]> GetAlignedFrameAsync(int index, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (index < 0 || index >= _frames.Count) throw new ArgumentOutOfRangeException(nameof(index));
        return Task.Run(() => PhotoFuseComposer.AlignedPreview(_frames[index], _frames[0],
            _transforms[index], Width, Height, token), token);
    }

    public Task<PhotoFusePreview> PreviewAsync(IReadOnlyList<PhotoFuseRegion> regions,
        CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var snapshot = regions.ToArray();
        if (snapshot.Length == 0) throw new ArgumentException("Add at least one donor region before previewing.");
        return Task.Run(() => new PhotoFusePreview(this,
            PhotoFuseComposer.Render(_frames, _transforms, Width, Height, snapshot, token), snapshot), token);
    }

    public async Task<string> ExportAsync(PhotoFusePreview preview, string folder, bool confirmed,
        CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ReferenceEquals(preview.Owner, this) || !confirmed)
            throw new InvalidOperationException("Review and confirm this session's composite preview before saving.");
        if (!Path.IsPathFullyQualified(folder) || !System.IO.Directory.Exists(folder))
            throw new DirectoryNotFoundException("Choose an existing destination folder.");
        token.ThrowIfCancellationRequested();
        var encoded = Path.Combine(folder, $".photolibrarian-fuse-{Guid.NewGuid():N}.tmp");
        var tagged = Path.Combine(folder, $".photolibrarian-fuse-{Guid.NewGuid():N}.tmp");
        bool encodedCreated = false, taggedCreated = false;
        try
        {
            await using (var file = new FileStream(encoded, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                encodedCreated = true;
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,
                    file.AsRandomAccessStream()).AsTask(token);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)Width,
                    (uint)Height, 96, 96, preview.Pixels);
                await encoder.FlushAsync().AsTask(token);
                await file.FlushAsync(token);
                file.Flush(true);
            }
            await using (var input = File.OpenRead(encoded))
            await using (var output = new FileStream(tagged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                taggedCreated = true;
                await SdrDerivativeMetadata.TagAsync(input, output, RawDerivativeFormat.Png,
                    CreateXmp(preview), token);
            }
            File.Delete(encoded);
            encodedCreated = false;
            // Existing files are still leased. A newly appearing edit sidecar invalidates review.
            if (_absentSidecars.Any(File.Exists))
                throw new IOException("Source metadata changed during review. Close Photo Fuse and select the shots again.");
            for (int number = 1; number <= 10000; number++)
            {
                token.ThrowIfCancellationRequested();
                string suffix = number == 1 ? " - Fused" : $" - Fused ({number})";
                string stem = Path.GetFileNameWithoutExtension(_names[0]) + suffix;
                // Includes same-stem recipes, face fallback sidecars, and other image extensions.
                if (System.IO.Directory.EnumerateFileSystemEntries(folder, stem + ".*").Any()) continue;
                var destination = Path.Combine(folder, stem + ".png");
                try
                {
                    File.Move(tagged, destination, overwrite: false);
                    taggedCreated = false;
                    return destination; // Commit point; cancellation after this must report a saved copy.
                }
                catch (IOException) when (File.Exists(destination) || System.IO.Directory.Exists(destination)) { }
            }
            throw new IOException("No unused output name could be reserved.");
        }
        finally
        {
            try { if (encodedCreated) File.Delete(encoded); }
            finally { if (taggedCreated) File.Delete(tagged); }
        }
    }

    private string CreateXmp(PhotoFusePreview preview)
    {
        var xmp = XmpMetaFactory.Create();
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(ProvenanceNamespace, "plfuse");
        xmp.SetProperty(ProvenanceNamespace, "Version", "1");
        xmp.SetProperty(ProvenanceNamespace, "State", "Baked composite; manually selected regions");
        xmp.SetProperty(ProvenanceNamespace, "Sources", JsonSerializer.Serialize(
            _names.Select((name, i) => new { Name = name, Sha256 = _hashes[i], Transform = _transforms[i] })));
        xmp.SetProperty(ProvenanceNamespace, "Regions", JsonSerializer.Serialize(preview.Regions));
        xmp.SetProperty(ProvenanceNamespace, "ColorPolicy", "Opaque 8-bit sRGB; source metadata and face identities not copied");
        xmp.SetPropertyInteger("http://ns.adobe.com/tiff/1.0/", "Orientation", 1);
        xmp.SetPropertyInteger("http://ns.adobe.com/tiff/1.0/", "ImageWidth", Width);
        xmp.SetPropertyInteger("http://ns.adobe.com/tiff/1.0/", "ImageLength", Height);
        xmp.SetPropertyInteger("http://ns.adobe.com/exif/1.0/", "ColorSpace", 1);
        return XmpMetaFactory.SerializeToString(xmp, new SerializeOptions { OmitPacketWrapper = true });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var lease in _leases) lease.Dispose();
        _leases.Clear();
        _frames.Clear();
    }
}
