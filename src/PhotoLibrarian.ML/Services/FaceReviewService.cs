using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;

namespace PhotoLibrarian.ML.Services;

public sealed class FaceReviewService
{
    private const int ClusteringBatchSize = 512;
    private const int MaxRecentPeople = 4;

    private readonly FaceRepository _faceRepository;
    private readonly ImageRepository _imageRepository;
    private readonly FaceClusteringService _clusteringService;
    private readonly FaceRecognitionService _recognitionService;
    private readonly IFaceMetadataStore _faceMetadataStore;
    private readonly RecentPeopleStore? _recentPeopleStore;
    private readonly List<Person> _recentPeople;

    public FaceReviewService(
        FaceRepository faceRepository,
        ImageRepository imageRepository,
        FaceClusteringService clusteringService,
        FaceRecognitionService recognitionService,
        IFaceMetadataStore? faceMetadataStore = null,
        RecentPeopleStore? recentPeopleStore = null)
    {
        _faceRepository = faceRepository;
        _imageRepository = imageRepository;
        _clusteringService = clusteringService;
        _recognitionService = recognitionService;
        _faceMetadataStore =
            faceMetadataStore ?? NullFaceMetadataStore.Instance;
        _recentPeopleStore = recentPeopleStore;
        _recentPeople = recentPeopleStore?.Load().ToList() ?? [];
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

    /// <summary>
    /// The most recently tagged-as people, newest first, for quick-access in person pickers.
    /// </summary>
    public IReadOnlyList<Person> RecentPeople => _recentPeople;

    private void RecordRecentPerson(long personId, string personName)
    {
        _recentPeople.RemoveAll(person => person.Id == personId);
        _recentPeople.Insert(0, new Person { Id = personId, Name = personName });
        if (_recentPeople.Count > MaxRecentPeople)
        {
            _recentPeople.RemoveRange(MaxRecentPeople, _recentPeople.Count - MaxRecentPeople);
        }
        _recentPeopleStore?.Save(_recentPeople);
    }

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

        var state = await LoadPortableStateAsync(cancellationToken);
        var person = state.GetPerson(personId);
        var affectedImages = state.AssignFaces(
            faceIds,
            personId,
            person.Name);
        await PersistImagesAsync(state, affectedImages, cancellationToken);
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

    public async Task RejectAsync(
        FaceSuggestionGroup group,
        IReadOnlyCollection<long> faceIds,
        CancellationToken cancellationToken = default)
    {
        if (group.SuggestedPersonId is not long personId)
        {
            throw new InvalidOperationException(
                "Only suggestions for a known person can be rejected.");
        }

        var state = await LoadPortableStateAsync(cancellationToken);
        var personName = state.GetPerson(personId).Name;
        var affectedImages = state.RejectFaces(faceIds, personName);
        await PersistImagesAsync(state, affectedImages, cancellationToken);
        await _faceRepository.RejectPersonForFacesAsync(
            faceIds,
            personId,
            cancellationToken);
    }

    public async Task HideFacesAsync(
        IReadOnlyCollection<long> faceIds,
        CancellationToken cancellationToken = default)
    {
        if (faceIds.Count == 0)
        {
            throw new ArgumentException("Select at least one face.", nameof(faceIds));
        }

        var state = await LoadPortableStateAsync(cancellationToken);
        var affectedImages = state.SetFacesHidden(faceIds, true);
        await PersistImagesAsync(state, affectedImages, cancellationToken);
        await _faceRepository.HideFacesFromSuggestionsAsync(
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
            var state = await LoadPortableStateAsync(cancellationToken);
            var affectedImages = state.AssignFaces(
                faceIds,
                null,
                trimmedName);
            await PersistImagesAsync(state, affectedImages, cancellationToken);
            var created = await _faceRepository.CreatePersonAndAssignFacesAsync(
                trimmedName,
                faceIds,
                cancellationToken);
            RecordRecentPerson(created.PersonId, trimmedName);
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
        var existingState = await LoadPortableStateAsync(cancellationToken);
        var existingAffectedImages = existingState.AssignFaces(
            faceIds,
            personId,
            trimmedName);
        await PersistImagesAsync(
            existingState,
            existingAffectedImages,
            cancellationToken);
        var previousStates = await _faceRepository.AssignFacesToPersonWithStateAsync(
            faceIds,
            personId,
            trimmedName,
            cancellationToken);
        RecordRecentPerson(personId, trimmedName);
        return new FaceTagOperation(
            personId,
            trimmedName,
            false,
            previousStates);
    }

    public async Task<FaceTagOperation> AddManualFaceAsync(
        ImageEntry image,
        FaceRegion region,
        long? existingPersonId,
        string personName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(region);
        if (image.Id <= 0)
        {
            throw new ArgumentException("The selected photo has not been indexed.", nameof(image));
        }
        if (region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0 ||
            region.X + region.Width > 1 || region.Y + region.Height > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(region),
                "The face region must be contained within the image.");
        }

        region.ImageId = image.Id;
        region.Confidence = 1;
        // Marks the region as manually managed so a later automatic face
        // rescan of this image can never discard it as an unmatched stale
        // detection (see FaceRepository.TryReplaceFaceRegionsAsync).
        region.IsMetadataManaged = true;
        var faceRegionId = await _faceRepository.AddFaceRegionAsync(region);
        try
        {
            return await TagAsAsync(
                [faceRegionId],
                existingPersonId,
                personName,
                cancellationToken);
        }
        catch
        {
            await _faceRepository.DeleteFaceRegionAsync(faceRegionId, cancellationToken);
            throw;
        }
    }

    public Task<FaceTagOperation> ReassignFacesAsync(
        IReadOnlyCollection<long> faceIds,
        long personId,
        string personName,
        CancellationToken cancellationToken = default) =>
        TagAsAsync(faceIds, personId, personName, cancellationToken);

    public async Task UndoTagAsync(
        FaceTagOperation operation,
        CancellationToken cancellationToken = default)
    {
        var state = await LoadPortableStateAsync(cancellationToken);
        var affectedImages = state.Restore(operation.PreviousStates);
        await PersistImagesAsync(state, affectedImages, cancellationToken);
        await _faceRepository.RestoreFaceTagStatesAsync(
            operation.PreviousStates,
            operation.TargetPersonId,
            operation.TargetPersonCreated,
            cancellationToken);
    }

    public async Task HidePersonAsync(
        FaceSuggestionGroup group,
        CancellationToken cancellationToken = default)
    {
        if (group.SuggestedPersonId is not long personId)
        {
            throw new InvalidOperationException(
                "Only suggestions for a known person can be hidden.");
        }

        var state = await LoadPortableStateAsync(cancellationToken);
        var affectedImages = state.SetPersonHidden(personId, true);
        await PersistImagesAsync(state, affectedImages, cancellationToken);
        await _faceRepository.SetPersonSuggestionsHiddenAsync(
            personId,
            true,
            cancellationToken);
    }

    public async Task RenamePersonAsync(
        long personId,
        string name,
        CancellationToken cancellationToken = default)
    {
        var trimmedName = name.Trim();
        if (trimmedName.Length == 0)
        {
            throw new ArgumentException("A person name is required.", nameof(name));
        }
        var state = await LoadPortableStateAsync(cancellationToken);
        var affectedImages = state.RenamePerson(personId, trimmedName);
        await PersistImagesAsync(state, affectedImages, cancellationToken);
        await _faceRepository.RenamePersonAsync(
            personId,
            trimmedName,
            cancellationToken);
    }

    public async Task MergePersonsAsync(
        long sourcePersonId,
        long targetPersonId,
        CancellationToken cancellationToken = default)
    {
        var state = await LoadPortableStateAsync(cancellationToken);
        var affectedImages = state.MergePeople(
            sourcePersonId,
            targetPersonId);
        await PersistImagesAsync(state, affectedImages, cancellationToken);
        await _faceRepository.MergePersonsAsync(
            sourcePersonId,
            targetPersonId,
            cancellationToken);
    }

    public async Task UnassignFacesAsync(
        IReadOnlyCollection<long> faceIds,
        CancellationToken cancellationToken = default)
    {
        if (faceIds.Count == 0)
        {
            throw new ArgumentException("Select at least one assigned face.", nameof(faceIds));
        }
        var state = await LoadPortableStateAsync(cancellationToken);
        var affectedImages = state.UnassignFaces(faceIds);
        await PersistImagesAsync(state, affectedImages, cancellationToken);
        await _faceRepository.UnassignFacesAsync(faceIds, cancellationToken);
    }

    public async Task DeletePersonAsync(
        long personId,
        CancellationToken cancellationToken = default)
    {
        var state = await LoadPortableStateAsync(cancellationToken);
        var affectedImages = state.DeletePerson(personId);
        await PersistImagesAsync(state, affectedImages, cancellationToken);
        await _faceRepository.DeletePersonAsync(personId, cancellationToken);
    }

    public async Task RestoreHiddenFacesAsync(
        IReadOnlyCollection<long> faceIds,
        CancellationToken cancellationToken = default)
    {
        if (faceIds.Count == 0)
        {
            throw new ArgumentException("Select at least one excluded face.", nameof(faceIds));
        }
        var state = await LoadPortableStateAsync(cancellationToken);
        var affectedImages = state.SetFacesHidden(faceIds, false);
        await PersistImagesAsync(state, affectedImages, cancellationToken);
        await _faceRepository.RestoreFacesToSuggestionsAsync(
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

    private async Task<PortableLibraryState> LoadPortableStateAsync(
        CancellationToken cancellationToken)
    {
        var faces = await _faceRepository.GetFaceRegionsForManagementAsync(
            cancellationToken);
        var people = await _faceRepository.GetAllPersonsAsync();
        var rejections = await _faceRepository
            .GetRejectedPersonIdsByFaceIdAsync(cancellationToken);
        var hiddenFaceIds = await _faceRepository
            .GetHiddenFaceSuggestionIdsAsync(cancellationToken);
        var images = await _imageRepository.GetAllAsync();
        return new PortableLibraryState(
            faces,
            people,
            rejections,
            hiddenFaceIds,
            images);
    }

    private async Task PersistImagesAsync(
        PortableLibraryState state,
        IReadOnlyCollection<long> imageIds,
        CancellationToken cancellationToken)
    {
        var writes = new List<(string ImagePath, PhotoFaceMetadata Metadata)>();
        foreach (var imageId in imageIds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var image = state.Images.GetValueOrDefault(imageId)
                ?? throw new InvalidOperationException(
                    "The photo for one or more selected faces no longer exists.");
            var metadata = new PhotoFaceMetadata(
                image.Width,
                image.Height,
                state.Faces.Values
                    .Where(face => face.Region.ImageId == imageId)
                    .OrderBy(face => face.Region.Id)
                    .Select(face => new PortableFaceMetadata(
                        face.Region.Id,
                        face.Region.X,
                        face.Region.Y,
                        face.Region.Width,
                        face.Region.Height,
                        face.Region.PersonName,
                        face.SuggestionsHidden,
                        face.Region.PersonId is long personId &&
                        state.People.TryGetValue(personId, out var person) &&
                        person.SuggestionsHidden,
                        face.RejectedPersonNames
                            .Order(StringComparer.OrdinalIgnoreCase)
                            .ToArray()))
                    .ToList());
            writes.Add((image.FilePath, metadata));
        }
        await _faceMetadataStore.WriteBatchAsync(writes, cancellationToken);
    }

    private async Task<IReadOnlyList<Person>> GetPeopleCoreAsync() =>
        await _faceRepository.GetAllPersonsAsync();

    private sealed class PortableLibraryState
    {
        public PortableLibraryState(
            IReadOnlyCollection<FaceRegion> faces,
            IReadOnlyCollection<Person> people,
            IReadOnlyDictionary<long, HashSet<long>> rejections,
            IReadOnlySet<long> hiddenFaceIds,
            IReadOnlyCollection<ImageEntry> images)
        {
            People = people.ToDictionary(
                person => person.Id,
                person => new Person
                {
                    Id = person.Id,
                    Name = person.Name,
                    ThumbnailData = person.ThumbnailData,
                    RepresentativeFaceRegionId =
                        person.RepresentativeFaceRegionId,
                    FaceCount = person.FaceCount,
                    SuggestionsHidden = person.SuggestionsHidden
                });
            Images = images.ToDictionary(image => image.Id);
            Faces = faces.ToDictionary(
                face => face.Id,
                face => new ProjectedFace(
                    new FaceRegion
                    {
                        Id = face.Id,
                        ImageId = face.ImageId,
                        X = face.X,
                        Y = face.Y,
                        Width = face.Width,
                        Height = face.Height,
                        PersonId = face.PersonId,
                        PersonName = face.PersonName,
                        Confidence = face.Confidence
                    },
                    hiddenFaceIds.Contains(face.Id),
                    rejections.GetValueOrDefault(face.Id)?
                        .Where(People.ContainsKey)
                        .Select(personId => People[personId].Name)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase) ??
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
        }

        public Dictionary<long, ProjectedFace> Faces { get; }
        public Dictionary<long, Person> People { get; }
        public Dictionary<long, ImageEntry> Images { get; }

        public Person GetPerson(long personId) =>
            People.GetValueOrDefault(personId)
            ?? throw new InvalidOperationException(
                "The selected person no longer exists.");

        public HashSet<long> AssignFaces(
            IReadOnlyCollection<long> faceIds,
            long? personId,
            string personName)
        {
            if (personId.HasValue) GetPerson(personId.Value);
            var selected = GetFaces(faceIds);
            foreach (var face in selected)
            {
                face.Region.PersonId = personId;
                face.Region.PersonName = personName;
                face.SuggestionsHidden = false;
                face.RejectedPersonNames.Clear();
            }
            return selected.Select(face => face.Region.ImageId).ToHashSet();
        }

        public HashSet<long> RejectFaces(
            IReadOnlyCollection<long> faceIds,
            string personName)
        {
            var selected = GetFaces(faceIds);
            foreach (var face in selected)
            {
                face.RejectedPersonNames.Add(personName);
            }
            return selected.Select(face => face.Region.ImageId).ToHashSet();
        }

        public HashSet<long> SetFacesHidden(
            IReadOnlyCollection<long> faceIds,
            bool hidden)
        {
            var selected = GetFaces(faceIds);
            foreach (var face in selected)
            {
                face.SuggestionsHidden = hidden;
            }
            return selected.Select(face => face.Region.ImageId).ToHashSet();
        }

        public HashSet<long> SetPersonHidden(long personId, bool hidden)
        {
            var person = GetPerson(personId);
            person.SuggestionsHidden = hidden;
            return Faces.Values
                .Where(face => face.Region.PersonId == personId)
                .Select(face => face.Region.ImageId)
                .ToHashSet();
        }

        public HashSet<long> RenamePerson(long personId, string newName)
        {
            var person = GetPerson(personId);
            if (People.Values.Any(candidate =>
                    candidate.Id != personId &&
                    string.Equals(
                        candidate.Name,
                        newName,
                        StringComparison.CurrentCultureIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"A person named {newName} already exists. Merge the people instead.");
            }

            var oldName = person.Name;
            person.Name = newName;
            var affected = new HashSet<long>();
            foreach (var face in Faces.Values)
            {
                if (face.Region.PersonId == personId)
                {
                    face.Region.PersonName = newName;
                    affected.Add(face.Region.ImageId);
                }
                if (face.RejectedPersonNames.Remove(oldName))
                {
                    face.RejectedPersonNames.Add(newName);
                    affected.Add(face.Region.ImageId);
                }
            }
            return affected;
        }

        public HashSet<long> MergePeople(
            long sourcePersonId,
            long targetPersonId)
        {
            if (sourcePersonId == targetPersonId)
            {
                throw new ArgumentException(
                    "Choose a different person to merge into.");
            }

            var source = GetPerson(sourcePersonId);
            var target = GetPerson(targetPersonId);
            var affected = new HashSet<long>();
            foreach (var face in Faces.Values)
            {
                if (face.Region.PersonId == sourcePersonId)
                {
                    face.Region.PersonId = targetPersonId;
                    face.Region.PersonName = target.Name;
                    face.SuggestionsHidden = false;
                    face.RejectedPersonNames.Clear();
                    affected.Add(face.Region.ImageId);
                    continue;
                }

                if (face.RejectedPersonNames.Remove(source.Name))
                {
                    face.RejectedPersonNames.Add(target.Name);
                    affected.Add(face.Region.ImageId);
                }
            }
            People.Remove(sourcePersonId);
            return affected;
        }

        public HashSet<long> UnassignFaces(
            IReadOnlyCollection<long> faceIds)
        {
            var selected = GetFaces(faceIds);
            foreach (var face in selected)
            {
                if (!face.Region.PersonId.HasValue)
                {
                    throw new InvalidOperationException(
                        "One or more selected faces are no longer assigned.");
                }
                face.Region.PersonId = null;
                face.Region.PersonName = null;
                face.SuggestionsHidden = false;
                face.RejectedPersonNames.Clear();
            }
            return selected.Select(face => face.Region.ImageId).ToHashSet();
        }

        public HashSet<long> DeletePerson(long personId)
        {
            var person = GetPerson(personId);
            var affected = new HashSet<long>();
            foreach (var face in Faces.Values)
            {
                if (face.Region.PersonId == personId)
                {
                    face.Region.PersonId = null;
                    face.Region.PersonName = null;
                    face.SuggestionsHidden = false;
                    face.RejectedPersonNames.Clear();
                    affected.Add(face.Region.ImageId);
                    continue;
                }
                if (face.RejectedPersonNames.Remove(person.Name))
                {
                    affected.Add(face.Region.ImageId);
                }
            }
            People.Remove(personId);
            return affected;
        }

        public HashSet<long> Restore(
            IReadOnlyCollection<FaceTagState> states)
        {
            var affected = new HashSet<long>();
            foreach (var previous in states)
            {
                var face = GetFace(previous.FaceRegionId);
                face.Region.PersonId = previous.PersonId;
                face.Region.PersonName = previous.PersonName;
                face.SuggestionsHidden = previous.SuggestionsHidden;
                face.RejectedPersonNames.Clear();
                foreach (var personId in previous.RejectedPersonIds)
                {
                    face.RejectedPersonNames.Add(GetPerson(personId).Name);
                }
                affected.Add(face.Region.ImageId);
            }
            return affected;
        }

        private List<ProjectedFace> GetFaces(
            IReadOnlyCollection<long> faceIds)
        {
            var ids = faceIds.Distinct().ToList();
            var selected = ids
                .Where(Faces.ContainsKey)
                .Select(faceId => Faces[faceId])
                .ToList();
            if (selected.Count != ids.Count)
            {
                throw new InvalidOperationException(
                    "One or more selected face suggestions no longer exist.");
            }
            return selected;
        }

        private ProjectedFace GetFace(long faceId) =>
            Faces.GetValueOrDefault(faceId)
            ?? throw new InvalidOperationException(
                "One or more selected face suggestions no longer exist.");
    }

    private sealed record ProjectedFace(
        FaceRegion Region,
        bool InitialSuggestionsHidden,
        HashSet<string> RejectedPersonNames)
    {
        public bool SuggestionsHidden { get; set; } =
            InitialSuggestionsHidden;
    }

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
