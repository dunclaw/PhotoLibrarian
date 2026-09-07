namespace PhotoLibrarian.Core.Models;

public sealed record FaceTagState(
    long FaceRegionId,
    long? PersonId,
    string? PersonName,
    bool WasRepresentative,
    bool SuggestionsHidden,
    IReadOnlyList<long> RejectedPersonIds);
