// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Text.Json;
using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.Core.Services;

internal static class PortableImageTagMetadataSerializer
{
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string Serialize(
        IEnumerable<ImageTag> tags,
        string? autoTagScanVersion,
        string? sourceIdentity = null)
    {
        var records = tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag.Tag))
            .GroupBy(tag => tag.Tag, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(tag => tag.Tag, StringComparer.OrdinalIgnoreCase)
            .Select(tag => new PortableImageTag(
                tag.Tag,
                tag.Source.ToString(),
                tag.Confidence))
            .ToArray();

        return JsonSerializer.Serialize(
            new PortableImageTagMetadata(
                CurrentSchemaVersion,
                autoTagScanVersion,
                records,
                sourceIdentity),
            SerializerOptions);
    }

    public static bool TryDeserialize(
        string? json,
        out PortableImageTagMetadata? metadata)
    {
        metadata = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            var value = JsonSerializer.Deserialize<PortableImageTagMetadata>(
                json,
                SerializerOptions);
            if (value is null ||
                value.SchemaVersion != CurrentSchemaVersion ||
                value.Tags is null)
                return false;

            var tags = value.Tags
                .Where(tag =>
                    tag is not null &&
                    !string.IsNullOrWhiteSpace(tag.Tag) &&
                    Enum.TryParse<TagSource>(
                        tag.Source,
                        ignoreCase: false,
                        out var source) &&
                    Enum.IsDefined(source) &&
                    float.IsFinite(tag.Confidence) &&
                    tag.Confidence is >= 0 and <= 1)
                .Select(tag => tag with { Tag = tag.Tag.Trim() })
                .ToArray();
            metadata = value with
            {
                AutoTagScanVersion = string.IsNullOrWhiteSpace(value.AutoTagScanVersion)
                    ? null
                    : value.AutoTagScanVersion,
                Tags = tags
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
