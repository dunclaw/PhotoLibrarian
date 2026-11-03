// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Buffers.Binary;
using System.Text;
using System.Diagnostics.CodeAnalysis;
using System.Xml;
using System.Xml.Linq;
using XmpCore;
using XmpCore.Options;

namespace PhotoLibrarian.Core.Services;

/// <summary>Strict, bounded packet inspection; never decodes or re-encodes image pixels.</summary>
internal static class MigrationXmpPacket
{
    private static readonly byte[] JpegHeader = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
    private static readonly byte[] ExtendedHeader = Encoding.ASCII.GetBytes("http://ns.adobe.com/xmp/extension/\0");
    private static readonly byte[] PngKeyword = Encoding.ASCII.GetBytes("XML:com.adobe.xmp\0");
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Xmp = "adobe:ns:meta/";
    internal const int MaximumMediaBytes = 256 * 1024 * 1024;
    internal const int MaximumPacketBytes = 1024 * 1024;

    internal sealed record Contents(byte[] NonXmpBytes, byte[]? Packet);

    internal static Contents Inspect(byte[] bytes, string extension)
    {
        using var unchanged = new MemoryStream();
        byte[]? packet = null;
        if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
            Require(bytes.AsSpan().StartsWith(signature));
            unchanged.Write(signature);
            var offset = 8;
            while (offset + 12 <= bytes.Length)
            {
                var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4));
                Require(length >= 0 && length <= bytes.Length - offset - 12);
                var type = bytes.AsSpan(offset + 4, 4);
                var data = bytes.AsSpan(offset + 8, length);
                Require(Crc32(bytes.AsSpan(offset + 4, length + 4)) ==
                    BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + length + 8, 4)));
                Require(!((type.SequenceEqual("tEXt"u8) || type.SequenceEqual("zTXt"u8)) &&
                    data.StartsWith(PngKeyword)));
                if (type.SequenceEqual("iTXt"u8) && data.StartsWith(PngKeyword))
                {
                    // Compressed text and nonempty language/translated keywords are deliberately not rewritten.
                    Require(data.Length >= PngKeyword.Length + 4 &&
                        data.Slice(PngKeyword.Length, 4).SequenceEqual(new byte[4]));
                    Require(packet is null);
                    packet = data[(PngKeyword.Length + 4)..].ToArray();
                }
                else
                {
                    unchanged.Write(bytes, offset, length + 12);
                }
                offset += length + 12;
                if (type.SequenceEqual("IEND"u8))
                {
                    Require(length == 0 && offset == bytes.Length);
                    return new Contents(unchanged.ToArray(), packet);
                }
            }
            throw new InvalidDataException("MigrationInvalidContainer");
        }

        Require(bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8);
        Require(bytes.AsSpan().IndexOf(ExtendedHeader) < 0);
        unchanged.Write(bytes, 0, 2);
        var position = 2;
        while (position + 4 <= bytes.Length && bytes[position] == 0xFF)
        {
            var marker = bytes[position + 1];
            if (marker == 0xDA)
            {
                // Everything from the start of scan onward, including additional scans, stays byte-exact.
                Require(bytes.AsSpan(position).LastIndexOf(new byte[] { 0xFF, 0xD9 }) >= 0);
                Require(bytes.AsSpan(position).IndexOf(JpegHeader) < 0);
                unchanged.Write(bytes, position, bytes.Length - position);
                return new Contents(unchanged.ToArray(), packet);
            }
            Require(marker is not 0xD8 and not 0xD9 and not 0x00 and not 0xFF);
            if (marker is >= 0xD0 and <= 0xD7 or 0x01)
            {
                unchanged.Write(bytes, position, 2);
                position += 2;
                continue;
            }
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(position + 2, 2));
            Require(length >= 2 && length <= bytes.Length - position - 2);
            var data = bytes.AsSpan(position + 4, length - 2);
            if (marker == 0xE1 && data.StartsWith(JpegHeader))
            {
                Require(packet is null);
                packet = data[JpegHeader.Length..].ToArray();
            }
            else
            {
                unchanged.Write(bytes, position, length + 2);
            }
            position += length + 2;
        }
        throw new InvalidDataException("MigrationInvalidContainer");
    }

    internal static string Merge(byte[]? embedded, byte[] sidecar)
    {
        var source = ReadPacket(sidecar);
        var destination = embedded is null ? null : ReadPacket(embedded);
        var properties = destination?.Properties ?? new Dictionary<XName, XElement>();
        var subject = destination?.Subject ?? source.Subject;
        // Empty sidecar subjects describe the uniquely associated image. An explicit,
        // different subject is an identity conflict, not permission to retarget metadata.
        if (destination is not null && source.Subject.Length > 0 && source.Subject != subject)
            throw new InvalidDataException("MigrationConflict");
        foreach (var (name, value) in source.Properties)
        {
            if (properties.TryGetValue(name, out var existing) &&
                !PropertiesEquivalent(existing, value))
            {
                throw new InvalidDataException("MigrationConflict");
            }
            properties.TryAdd(name, value);
        }
        var document = new XElement(Xmp + "xmpmeta",
            new XAttribute(XNamespace.Xmlns + "x", Xmp),
            new XElement(Rdf + "RDF",
                new XAttribute(XNamespace.Xmlns + "rdf", Rdf),
                new XElement(Rdf + "Description",
                    new XAttribute(Rdf + "about", subject), properties.Values)));
        var serialized = document.ToString(SaveOptions.DisableFormatting);
        // Validate XMP as well as XML, without round-tripping away unknown fields through a model.
        XmpMetaFactory.ParseFromString(serialized, new ParseOptions { StrictAliasing = true });
        return serialized;
    }

    internal static bool Equivalent(byte[] first, byte[] second)
    {
        var a = ReadPacket(first);
        var b = ReadPacket(second);
        return a.Subject == b.Subject && a.Properties.Count == b.Properties.Count && a.Properties.All(pair =>
            b.Properties.TryGetValue(pair.Key, out var other) &&
            PropertiesEquivalent(pair.Value, other));
    }

    internal static void EnsureMigratable(byte[] sidecar)
    {
        var properties = ReadPacket(sidecar).Properties;
        // The current raster editor clears baked recipes from sidecars only. Embedding one
        // would make a later save apply it twice; retain the complete packet instead.
        if (properties.Keys.Any(name => name.NamespaceName == Models.EditParameters.XmpNamespace))
            throw new InvalidDataException("MigrationRecipe");
        // Orientation changes are not part of this metadata-only operation.
        if (properties.ContainsKey(XName.Get("Orientation", "http://ns.adobe.com/tiff/1.0/")))
            throw new InvalidDataException("MigrationOrientation");
    }

    private sealed record PacketData(string Subject, Dictionary<XName, XElement> Properties);

    private static PacketData ReadPacket(byte[] packet)
    {
        Require(packet.Length <= MaximumPacketBytes);
        using var stream = new MemoryStream(packet);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumPacketBytes
        });
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        var root = document.Root!;
        Require(root is not null);
        var rdf = root.Name == Rdf + "RDF" ? root : root.Element(Rdf + "RDF");
        Require(rdf is not null);
        if (root != rdf)
        {
            Require(root.Name == Xmp + "xmpmeta" && root.Elements().Count() == 1);
            Require(root.Attributes().All(a => a.IsNamespaceDeclaration || a.Name == Xmp + "xmptk"));
        }
        Require(rdf!.Attributes().All(a => a.IsNamespaceDeclaration));
        Require(!document.DescendantNodes().Any(n => n is XComment));
        Require(document.Nodes().OfType<XProcessingInstruction>().All(p => p.Target == "xpacket"));
        Require(IsContainer(root) && IsContainer(rdf));
        var properties = new Dictionary<XName, XElement>();
        string? subject = null;
        foreach (var description in rdf.Elements())
        {
            Require(description.Name == Rdf + "Description");
            Require(IsContainer(description));
            var descriptionSubject = description.Attribute(Rdf + "about")?.Value ?? "";
            Require(subject is null || subject == descriptionSubject);
            subject = descriptionSubject;
            foreach (var attribute in description.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name != Rdf + "about"))
            {
                Require(attribute.Name.Namespace != XNamespace.None &&
                    attribute.Name.Namespace != Rdf && attribute.Name.Namespace != XNamespace.Xml);
                Add(new XElement(attribute.Name, attribute.Value), description);
            }
            foreach (var property in description.Elements())
            {
                Require(property.Name.Namespace != XNamespace.None && property.Name.Namespace != Rdf);
                Add(new XElement(property), description);
            }
            Require(!description.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)));
        }
        XmpMetaFactory.ParseFromString(document.ToString(SaveOptions.DisableFormatting),
            new ParseOptions { StrictAliasing = true });
        return new PacketData(subject ?? "", properties);

        void Add(XElement property, XElement context)
        {
            // Retain inherited prefix bindings, including prefixes used inside literal property values.
            foreach (var ancestor in context.AncestorsAndSelf())
            {
                foreach (var binding in ancestor.Attributes().Where(a => a.IsNamespaceDeclaration))
                {
                    if (property.Attribute(binding.Name) is null)
                        property.Add(new XAttribute(binding));
                }
            }
            Require(properties.TryAdd(property.Name, property));
        }
    }

    private static XElement Canonical(XElement element, bool includeNamespaces)
    {
        var copy = new XElement(element.Name);
        foreach (var attribute in element.Attributes().OrderBy(a => a.Name.ToString(), StringComparer.Ordinal))
        {
            if (includeNamespaces || !attribute.IsNamespaceDeclaration)
                copy.Add(new XAttribute(attribute));
        }
        foreach (var node in element.Nodes())
        {
            if (node is XElement child)
                copy.Add(Canonical(child, includeNamespaces));
            else if (node is not XText text || !element.HasElements || !string.IsNullOrWhiteSpace(text.Value))
                copy.Add(node);
        }
        return copy;
    }

    private static bool IsContainer(XElement element) =>
        element.Nodes().All(n => n is XElement || n is XText text && string.IsNullOrWhiteSpace(text.Value));

    private static bool PropertiesEquivalent(XElement first, XElement second)
    {
        // Unknown schemas may store qualified names in strings. Never equate identical
        // lexical values whose prefix bindings mean different things.
        var includeNamespaces = HasQualifiedValues(first) || HasQualifiedValues(second);
        return XNode.DeepEquals(Canonical(first, includeNamespaces), Canonical(second, includeNamespaces));
    }

    private static bool HasQualifiedValues(XElement element) =>
        element.DescendantNodes().OfType<XText>().Any(text => text.Value.Contains(':')) ||
        element.DescendantsAndSelf().Attributes().Any(a => !a.IsNamespaceDeclaration && a.Value.Contains(':'));

    private static void Require([DoesNotReturnIf(false)] bool condition)
    {
        if (!condition) throw new InvalidDataException("MigrationUnsafePacket");
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }
}
