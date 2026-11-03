// Copyright (c) Duncan Lawler. All Rights Reserved.
namespace PhotoLibrarian.Core.Models;

public sealed record PhotoFaceMetadata(
    int ImageWidth,
    int ImageHeight,
    IReadOnlyList<PortableFaceMetadata> Faces);

public sealed record PortableFaceMetadata(
    long FaceRegionId,
    double X,
    double Y,
    double Width,
    double Height,
    string? PersonName,
    bool SuggestionsHidden,
    bool PersonSuggestionsHidden,
    IReadOnlyList<string> RejectedPersonNames,
    bool? IsPersonRepresentative = null);
