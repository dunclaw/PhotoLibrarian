using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.ML.Services;

public sealed class FaceReviewService
{
    private const int ClusteringBatchSize = 512;

    private readonly FaceRepository _faceRepository;
    private readonly FaceClusteringService _clusteringService;
    private readonly FaceRecognitionService _recognitionService;

    public FaceReviewService(
        FaceRepository faceRepository,
        ImageRepository imageRepository,
        FaceClusteringService clusteringService,
        FaceRecognitionService recognitionService)
    {
        _faceRepository = faceRepository;
        ArgumentNullException.ThrowIfNull(imageRepository);
        _clusteringService = clusteringService;
        _recognitionService = recognitionService;
    }

    public async Task<IReadOnlyList<FaceSuggestionGroup>> GetSuggestionGroupsAsync(
        CancellationToken cancellationToken = default)
    {
        var faces = (await _faceRepository.GetAllFacesWithEmbeddingsAsync(cancellationToken))
            .Where(face => face.Embedding is not null)
            .ToList();
        if (faces.Count == 0) return [];

        var people = await _faceRepository.GetAllPersonsAsync();
        var peopleById = people.ToDictionary(person => person.Id);
        var rejectedPeople = await _faceRepository
            .GetRejectedPersonIdsByFaceIdAsync(cancellationToken);
        var hiddenFaceIds = await _faceRepository
            .GetHiddenFaceSuggestionIdsAsync(cancellationToken);
        var imagePaths = await _faceRepository.GetFaceImagePathsAsync(
            cancellationToken);

        var visiblePersonIds = people
            .Where(person => !person.SuggestionsHidden)
            .Select(person => person.Id)
            .ToHashSet();
        var profiles = _recognitionService.BuildProfiles(faces)
            .Where(profile => visiblePersonIds.Contains(profile.PersonId))
            .ToList();
        var unassignedFaces = faces
            .Where(face => !face.PersonId.HasValue &&
                           !hiddenFaceIds.Contains(face.Id) &&
                           imagePaths.ContainsKey(face.ImageId))
            .ToList();
        var rankedFaces = await Task.Run(
            () => RankFaces(
                unassignedFaces,
                profiles,
                rejectedPeople,
                faces.Where(face => face.PersonId.HasValue)
                    .GroupBy(face => face.ImageId)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Select(face => face.PersonId!.Value).ToHashSet()),
                cancellationToken),
            cancellationToken);

        var groups = new List<FaceSuggestionGroup>();
        foreach (var personGroup in rankedFaces
                     .Where(item => item.Match is not null)
                     .GroupBy(item => item.Match!.PersonId))
        {
            var person = peopleById.GetValueOrDefault(personGroup.Key);
            if (person is null) continue;
            var rankedSuggestions = personGroup
                .OrderByDescending(item => item.Match!.Similarity)
                .ToList();
            AddGroup(
                groups,
                $"person:{person.Id}",
                person,
                rankedSuggestions.Select(item => item.Face).ToList(),
                imagePaths,
                rankedSuggestions.ToDictionary(
                    item => item.Face.Id,
                    item => (float?)item.Match!.Similarity),
                rankedSuggestions.ToDictionary(
                    item => item.Face.Id,
                    item => ToCandidates(item.Matches, peopleById)));
        }

        var unmatchedFaces = rankedFaces
            .Where(item => item.Match is null)
            .Select(item => item.Face)
            .ToList();
        var clusteredFaces = new List<(FaceRegion Face, int Label)>(unmatchedFaces.Count);
        var nextClusterId = 0;
        foreach (var batch in unmatchedFaces.Chunk(ClusteringBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var clusteringInput = batch
                .Select(face => new FaceWithEmbedding
                {
                    FaceRegionId = face.Id,
                    ImageId = face.ImageId,
                    Embedding = face.Embedding!
                })
                .ToList();
            var labels = await Task.Run(
                () => _clusteringService.ClusterFaces(clusteringInput),
                cancellationToken);
            var clusterOffset = nextClusterId;
            for (var index = 0; index < batch.Length; index++)
            {
                clusteredFaces.Add((batch[index], clusterOffset + labels[index]));
            }
            nextClusterId += labels.DefaultIfEmpty(-1).Max() + 1;
        }

        foreach (var cluster in clusteredFaces.GroupBy(item => item.Label))
        {
            var suggestions = cluster
                .Select(item => item.Face)
                .Where(face => imagePaths.ContainsKey(face.ImageId))
                .ToList();
            if (suggestions.Count == 0) continue;

            AddGroup(
                groups,
                $"unknown:{cluster.Key}",
                null,
                suggestions,
                imagePaths);
        }

        return groups
            .OrderByDescending(group => group.Suggestions.Count)
            .ThenByDescending(group => group.SuggestedPersonId.HasValue)
            .ThenBy(group => group.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public Task<IReadOnlyList<Person>> GetPeopleAsync() =>
        GetPeopleCoreAsync();

    public async Task<IReadOnlyList<PersonDropTarget>> GetPersonDropTargetsAsync(
        CancellationToken cancellationToken = default)
    {
        var people = await _faceRepository.GetAllPersonsAsync();
        var representativeFaces = await _faceRepository
            .GetPersonRepresentativeFacesAsync(cancellationToken);
        return people
            .Select(person =>
            {
                var hasRepresentative = representativeFaces.TryGetValue(
                    person.Id,
                    out var representative);
                return new PersonDropTarget(
                    person,
                    !hasRepresentative
                        ? null
                        : ToSuggestion(
                            representative.Face,
                            representative.ImagePath));
            })
            .ToList();
    }

    public async Task<PeopleManagementSnapshot> GetPeopleManagementAsync(
        CancellationToken cancellationToken = default)
    {
        var people = await _faceRepository.GetAllPersonsAsync();
        var faces = await _faceRepository
            .GetFaceRegionsForManagementAsync(cancellationToken);
        var hiddenFaceIds = await _faceRepository
            .GetHiddenFaceSuggestionIdsAsync(cancellationToken);
        var imagePaths = await _faceRepository.GetFaceImagePathsAsync(
            cancellationToken);
        var facesByPersonId = faces
            .Where(face =>
                face.PersonId.HasValue &&
                imagePaths.ContainsKey(face.ImageId))
            .GroupBy(face => face.PersonId!.Value)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(face => ToSuggestion(face, imagePaths[face.ImageId]))
                    .ToList());

        var personGroups = people
            .Select(person => new PersonFaceGroup(
                person,
                facesByPersonId.GetValueOrDefault(person.Id) ?? []))
            .ToList();
        var hiddenFaces = faces
            .Where(face =>
                hiddenFaceIds.Contains(face.Id) &&
                imagePaths.ContainsKey(face.ImageId))
            .Select(face => ToSuggestion(face, imagePaths[face.ImageId]))
            .ToList();

        return new PeopleManagementSnapshot(personGroups, hiddenFaces);
    }

    public async Task<FaceTagOperation> ConfirmAsync(
        FaceSuggestionGroup group,
        IReadOnlyCollection<long> faceIds,
        CancellationToken cancellationToken = default)
    {
        if (group.SuggestedPersonId is not long personId)
        {
            throw new InvalidOperationException(
                "Choose Tag as to name suggestions from an unnamed cluster.");
        }

        var previousStates = await _faceRepository.AssignFacesToPersonWithStateAsync(
            faceIds,
            personId,
            group.DisplayName,
            cancellationToken);
        return new FaceTagOperation(
            personId,
            group.DisplayName,
            false,
            previousStates);
    }

    public Task RejectAsync(
        FaceSuggestionGroup group,
        IReadOnlyCollection<long> faceIds,
        CancellationToken cancellationToken = default)
    {
        if (group.SuggestedPersonId is not long personId)
        {
            throw new InvalidOperationException(
                "Only suggestions for a known person can be rejected.");
        }

        return _faceRepository.RejectPersonForFacesAsync(
            faceIds,
            personId,
            cancellationToken);
    }

    public Task HideFacesAsync(
        IReadOnlyCollection<long> faceIds,
        CancellationToken cancellationToken = default)
    {
        if (faceIds.Count == 0)
        {
            throw new ArgumentException("Select at least one face.", nameof(faceIds));
        }

        return _faceRepository.HideFacesFromSuggestionsAsync(
            faceIds,
            cancellationToken);
    }

    public async Task<FaceTagOperation> TagAsAsync(
        IReadOnlyCollection<long> faceIds,
        long? existingPersonId,
        string personName,
        CancellationToken cancellationToken = default)
    {
        if (faceIds.Count == 0)
        {
            throw new ArgumentException("Select at least one face.", nameof(faceIds));
        }

        var trimmedName = personName.Trim();
        if (trimmedName.Length == 0)
        {
            throw new ArgumentException("A person name is required.", nameof(personName));
        }

        var people = await _faceRepository.GetAllPersonsAsync();
        var matchingPerson = people.FirstOrDefault(
            person => string.Equals(
                person.Name,
                trimmedName,
                StringComparison.CurrentCultureIgnoreCase));
        if (!existingPersonId.HasValue && matchingPerson is null)
        {
            var created = await _faceRepository.CreatePersonAndAssignFacesAsync(
                trimmedName,
                faceIds,
                cancellationToken);
            return new FaceTagOperation(
                created.PersonId,
                trimmedName,
                true,
                created.PreviousStates);
        }

        var personId = existingPersonId ?? matchingPerson!.Id;
        if (existingPersonId.HasValue)
        {
            var person = people
                .SingleOrDefault(candidate => candidate.Id == existingPersonId.Value)
                ?? throw new InvalidOperationException("The selected person no longer exists.");
            trimmedName = person.Name;
        }
        else
        {
            trimmedName = matchingPerson!.Name;
        }
        var previousStates = await _faceRepository.AssignFacesToPersonWithStateAsync(
            faceIds,
            personId,
            trimmedName,
            cancellationToken);
        return new FaceTagOperation(
            personId,
            trimmedName,
            false,
            previousStates);
    }

    public Task<FaceTagOperation> ReassignFacesAsync(
        IReadOnlyCollection<long> faceIds,
        long personId,
        string personName,
        CancellationToken cancellationToken = default) =>
        TagAsAsync(faceIds, personId, personName, cancellationToken);

    public Task UndoTagAsync(
        FaceTagOperation operation,
        CancellationToken cancellationToken = default) =>
        _faceRepository.RestoreFaceTagStatesAsync(
            operation.PreviousStates,
            operation.TargetPersonId,
            operation.TargetPersonCreated,
            cancellationToken);

    public Task HidePersonAsync(
        FaceSuggestionGroup group,
        CancellationToken cancellationToken = default)
    {
        if (group.SuggestedPersonId is not long personId)
        {
            throw new InvalidOperationException(
                "Only suggestions for a known person can be hidden.");
        }

        return _faceRepository.SetPersonSuggestionsHiddenAsync(
            personId,
            true,
            cancellationToken);
    }

    public Task RenamePersonAsync(
        long personId,
        string name,
        CancellationToken cancellationToken = default)
    {
        var trimmedName = name.Trim();
        if (trimmedName.Length == 0)
        {
            throw new ArgumentException("A person name is required.", nameof(name));
        }
        return _faceRepository.RenamePersonAsync(
            personId,
            trimmedName,
            cancellationToken);
    }

    public Task MergePersonsAsync(
        long sourcePersonId,
        long targetPersonId,
        CancellationToken cancellationToken = default) =>
        _faceRepository.MergePersonsAsync(
            sourcePersonId,
            targetPersonId,
            cancellationToken);

    public Task UnassignFacesAsync(
        IReadOnlyCollection<long> faceIds,
        CancellationToken cancellationToken = default)
    {
        if (faceIds.Count == 0)
        {
            throw new ArgumentException("Select at least one assigned face.", nameof(faceIds));
        }
        return _faceRepository.UnassignFacesAsync(faceIds, cancellationToken);
    }

    public Task DeletePersonAsync(
        long personId,
        CancellationToken cancellationToken = default) =>
        _faceRepository.DeletePersonAsync(personId, cancellationToken);

    public Task RestoreHiddenFacesAsync(
        IReadOnlyCollection<long> faceIds,
        CancellationToken cancellationToken = default)
    {
        if (faceIds.Count == 0)
        {
            throw new ArgumentException("Select at least one excluded face.", nameof(faceIds));
        }
        return _faceRepository.RestoreFacesToSuggestionsAsync(
            faceIds,
            cancellationToken);
    }

    public Task SetPersonRepresentativeFaceAsync(
        long personId,
        long faceRegionId,
        CancellationToken cancellationToken = default) =>
        _faceRepository.SetPersonRepresentativeFaceAsync(
            personId,
            faceRegionId,
            cancellationToken);

    private async Task<IReadOnlyList<Person>> GetPeopleCoreAsync() =>
        await _faceRepository.GetAllPersonsAsync();

    private static void AddGroup(
        ICollection<FaceSuggestionGroup> groups,
        string groupId,
        Person? person,
        IReadOnlyCollection<FaceRegion> faces,
        IReadOnlyDictionary<long, string> imagePaths,
        IReadOnlyDictionary<long, float?>? similarities = null,
        IReadOnlyDictionary<long, IReadOnlyList<FacePersonCandidate>>? candidates = null)
    {
        if (faces.Count == 0) return;

        groups.Add(new FaceSuggestionGroup(
            groupId,
            person?.Id,
            person?.Name ?? "Unnamed person",
            person?.FaceCount ?? 0,
            faces.Select(face => new FaceSuggestion(
                    face.Id,
                    face.ImageId,
                    imagePaths[face.ImageId],
                    face.X,
                    face.Y,
                    face.Width,
                    face.Height,
                    similarities?.GetValueOrDefault(face.Id),
                    candidates?.GetValueOrDefault(face.Id) ?? []))
                .ToList()));
    }

    private static IReadOnlyList<FacePersonCandidate> ToCandidates(
        IReadOnlyList<PersonMatch> matches,
        IReadOnlyDictionary<long, Person> peopleById)
    {
        return matches
            .Where(match => peopleById.ContainsKey(match.PersonId))
            .Select(match =>
            {
                var person = peopleById[match.PersonId];
                return new FacePersonCandidate(
                    person.Id,
                    person.Name,
                    person.FaceCount,
                    match.Similarity);
            })
            .ToList();
    }

    private static FaceSuggestion ToSuggestion(
        FaceRegion face,
        string imagePath) =>
        new(
            face.Id,
            face.ImageId,
            imagePath,
            face.X,
            face.Y,
            face.Width,
            face.Height,
            null,
            []);

    private List<RankedFace> RankFaces(
        IReadOnlyCollection<FaceRegion> faces,
        IReadOnlyCollection<PersonFaceProfile> profiles,
        IReadOnlyDictionary<long, HashSet<long>> rejectedPeople,
        IReadOnlyDictionary<long, HashSet<long>> confirmedPersonIdsByImage,
        CancellationToken cancellationToken)
    {
        var ranked = new List<RankedFace>(faces.Count);
        foreach (var imageGroup in faces.GroupBy(face => face.ImageId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var usedPersonIds = confirmedPersonIdsByImage.TryGetValue(
                imageGroup.Key,
                out var confirmed)
                ? confirmed.ToHashSet()
                : [];
            var candidates = imageGroup
                .Select(face =>
                {
                    var excluded = rejectedPeople.TryGetValue(face.Id, out var rejected)
                        ? rejected.ToHashSet()
                        : [];
                    excluded.UnionWith(usedPersonIds);
                    return new
                    {
                        Face = face,
                        Matches = _recognitionService.FindMatches(
                            face.Embedding!,
                            profiles,
                            excluded)
                    };
                })
                .ToList();
            var assignedFaceIds = new HashSet<long>();

            foreach (var candidate in candidates
                         .SelectMany(item => item.Matches.Select(match => new
                         {
                             item.Face,
                             Match = match,
                             item.Matches
                         }))
                         .OrderByDescending(item => item.Match.Similarity)
                         .ThenBy(item => item.Face.Id)
                         .ThenBy(item => item.Match.PersonId))
            {
                if (assignedFaceIds.Contains(candidate.Face.Id) ||
                    usedPersonIds.Contains(candidate.Match.PersonId))
                {
                    continue;
                }

                ranked.Add(new RankedFace(
                    candidate.Face,
                    candidate.Match,
                    candidate.Matches));
                assignedFaceIds.Add(candidate.Face.Id);
                usedPersonIds.Add(candidate.Match.PersonId);
            }

            ranked.AddRange(candidates
                .Where(candidate => !assignedFaceIds.Contains(candidate.Face.Id))
                .Select(candidate => new RankedFace(
                    candidate.Face,
                    null,
                    candidate.Matches)));
        }
        return ranked;
    }

    private sealed record RankedFace(
        FaceRegion Face,
        PersonMatch? Match,
        IReadOnlyList<PersonMatch> Matches);
}

public sealed record FaceSuggestionGroup(
    string Id,
    long? SuggestedPersonId,
    string DisplayName,
    int ConfirmedCount,
    IReadOnlyList<FaceSuggestion> Suggestions);

public sealed record FaceSuggestion(
    long FaceRegionId,
    long ImageId,
    string ImagePath,
    double X,
    double Y,
    double Width,
    double Height,
    float? Similarity,
    IReadOnlyList<FacePersonCandidate> Candidates);

public sealed record FacePersonCandidate(
    long PersonId,
    string PersonName,
    int ConfirmedCount,
    float Similarity);

public sealed record PeopleManagementSnapshot(
    IReadOnlyList<PersonFaceGroup> People,
    IReadOnlyList<FaceSuggestion> HiddenFaces);

public sealed record PersonFaceGroup(
    Person Person,
    IReadOnlyList<FaceSuggestion> Faces);

public sealed record PersonDropTarget(
    Person Person,
    FaceSuggestion? RepresentativeFace);

public sealed record FaceTagOperation(
    long TargetPersonId,
    string TargetPersonName,
    bool TargetPersonCreated,
    IReadOnlyList<FaceTagState> PreviousStates);
