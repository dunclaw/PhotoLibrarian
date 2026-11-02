// Copyright (c) Duncan Lawler. All Rights Reserved.
namespace PhotoLibrarian.Core.Models;

public sealed record PortableImageTag(
    string Tag,
    string Source,
    float Confidence);

public sealed record PortableImageTagMetadata(
    int SchemaVersion,
    string? AutoTagScanVersion,
    IReadOnlyList<PortableImageTag> Tags);
