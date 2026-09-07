using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Diagnostics;
using PhotoLibrarian.ML.Services;
using System.Collections.ObjectModel;
using Windows.Storage.Streams;

namespace PhotoLibrarian.ViewModels;

public partial class PeopleReviewViewModel : ObservableObject
{
    private readonly FaceReviewService _service;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _personTargetsGate = new(1, 1);
    private FaceTagOperation? _lastTagOperation;
    private int _refreshRequest;
    private bool _isPeopleManagementLoaded;
    private bool _arePersonTargetsLoaded;

    public bool IsApplyingSuggestionRefresh { get; private set; }

    public ObservableCollection<FaceSuggestionGroupViewModel> Groups { get; } = [];
    public ObservableCollection<PersonManagementItemViewModel> People { get; } = [];
    public ObservableCollection<FaceSuggestionItemViewModel> HiddenFaces { get; } = [];
    public ObservableCollection<PersonDropTargetViewModel> PersonDropTargets { get; } = [];

    [ObservableProperty]
    public partial FaceSuggestionGroupViewModel? SelectedGroup { get; set; }

    [ObservableProperty]
    public partial PersonManagementItemViewModel? SelectedPerson { get; set; }

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool CanUndoLastTag { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "";

    public PeopleReviewViewModel(FaceReviewService service)
    {
        _service = service;
    }

    [RelayCommand]
    public async Task OpenAsync()
    {
        IsOpen = true;
        _isPeopleManagementLoaded = false;
        _arePersonTargetsLoaded = false;
        await Task.WhenAll(
            RefreshAsync(),
            EnsurePersonDropTargetsLoadedAsync());
    }

    [RelayCommand]
    public void Close()
    {
        IsOpen = false;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var request = Interlocked.Increment(ref _refreshRequest);
        IsBusy = true;
        StatusMessage = "Refreshing face suggestions...";
        var entered = false;
        try
        {
            await _refreshGate.WaitAsync(cancellationToken);
            entered = true;
            var groups = await _service.GetSuggestionGroupsAsync(cancellationToken);
            if (request != Volatile.Read(ref _refreshRequest)) return;

            // Capture the latest user choice after the query. The user can select
            // another group while a slow refresh is in flight.
            var selectedGroup = SelectedGroup;
            var selectedGroupId = selectedGroup?.Source.Id;
            var selectedGroupIndex = selectedGroup is null
                ? 0
                : Groups.IndexOf(selectedGroup);
            var selectedFaceIds = selectedGroup?.Suggestions
                .Select(face => face.FaceRegionId)
                .ToHashSet() ?? [];

            IsApplyingSuggestionRefresh = true;
            try
            {
                ReconcileGroups(groups);
                SelectedGroup = ResolveGroupAfterRefresh(
                    selectedGroupId,
                    selectedGroupIndex,
                    selectedFaceIds);
            }
            finally
            {
                IsApplyingSuggestionRefresh = false;
            }
            StatusMessage = Groups.Count == 0
                ? "No face suggestions need review."
                : $"{Groups.Sum(group => group.Suggestions.Count)} suggestions in {Groups.Count} groups";
        }
        finally
        {
            if (entered) _refreshGate.Release();
            if (request == Volatile.Read(ref _refreshRequest))
            {
                IsBusy = false;
            }
        }
    }

    private async Task ReconcilePersonDropTargetsAsync(
        IReadOnlyList<PersonDropTarget> incoming)
    {
        var portraitsToLoad = new List<PersonDropTargetViewModel>();
        var incomingIds = incoming.Select(target => target.Person.Id).ToHashSet();
        for (var index = PersonDropTargets.Count - 1; index >= 0; index--)
        {
            if (!incomingIds.Contains(PersonDropTargets[index].Id))
            {
                PersonDropTargets.RemoveAt(index);
            }
        }

        for (var index = 0; index < incoming.Count; index++)
        {
            var source = incoming[index];
            var existing = PersonDropTargets.FirstOrDefault(
                target => target.Id == source.Person.Id);
            if (existing is null)
            {
                PersonDropTargets.Insert(index, new PersonDropTargetViewModel(source));
                continue;
            }

            if (existing.Update(source))
            {
                portraitsToLoad.Add(existing);
            }
            var currentIndex = PersonDropTargets.IndexOf(existing);
            if (currentIndex != index)
            {
                PersonDropTargets.Move(currentIndex, index);
            }
        }

        await Task.WhenAll(
            portraitsToLoad.Select(target => target.LoadProfilePictureSafelyAsync()));
    }

    public async Task ConfirmAsync(
        IReadOnlyCollection<FaceSuggestionItemViewModel> selected,
        CancellationToken cancellationToken = default)
    {
        var group = RequireSelection(selected);
        FaceTagOperation? operation = null;
        await RunIncrementalAsync(
            async () =>
            {
                operation = await _service.ConfirmAsync(
                    group.Source,
                    selected.Select(item => item.FaceRegionId).ToList(),
                    cancellationToken);
            },
            () => RemoveSuggestions(group, selected),
            refreshPersonTargets: true);
        RememberTagOperation(operation!);
    }

    public async Task RejectAsync(
        IReadOnlyCollection<FaceSuggestionItemViewModel> selected,
        CancellationToken cancellationToken = default)
    {
        var group = RequireSelection(selected);
        var rejectedPersonId = group.Source.SuggestedPersonId!.Value;
        await RunIncrementalAsync(
            () => _service.RejectAsync(
                group.Source,
                selected.Select(item => item.FaceRegionId).ToList(),
                cancellationToken),
            () => RerouteRejectedSuggestions(
                group,
                selected,
                rejectedPersonId));
    }

    public async Task TagAsAsync(
        IReadOnlyCollection<FaceSuggestionItemViewModel> selected,
        Person? existingPerson,
        string? newPersonName,
        CancellationToken cancellationToken = default)
    {
        var group = RequireSelection(selected);
        var name = string.IsNullOrWhiteSpace(newPersonName)
            ? existingPerson?.Name ?? ""
            : newPersonName;
        var existingId = string.IsNullOrWhiteSpace(newPersonName)
            ? existingPerson?.Id
            : null;

        FaceTagOperation? operation = null;
        await RunIncrementalAsync(
            async () =>
            {
                operation = await _service.TagAsAsync(
                    selected.Select(item => item.FaceRegionId).ToList(),
                    existingId,
                    name,
                    cancellationToken);
            },
            () => RemoveSuggestions(group, selected),
            refreshPersonTargets: true);
        RememberTagOperation(operation!);
    }

    public async Task HideFacesAsync(
        IReadOnlyCollection<FaceSuggestionItemViewModel> selected,
        CancellationToken cancellationToken = default)
    {
        var group = RequireSelection(selected);
        await RunIncrementalAsync(
            () => _service.HideFacesAsync(
                selected.Select(item => item.FaceRegionId).ToList(),
                cancellationToken),
            () => RemoveSuggestions(group, selected));
    }

    public async Task HideCurrentPersonAsync(
        CancellationToken cancellationToken = default)
    {
        var group = SelectedGroup
            ?? throw new InvalidOperationException("Select a suggestion group first.");
        var suggestions = group.Suggestions.ToList();
        var hiddenPersonId = group.Source.SuggestedPersonId
            ?? throw new InvalidOperationException(
                "Only suggestions for a known person can be hidden.");
        await RunIncrementalAsync(
            () => _service.HidePersonAsync(group.Source, cancellationToken),
            () => RerouteRejectedSuggestions(
                group,
                suggestions,
                hiddenPersonId));
    }

    public Task<IReadOnlyList<Person>> GetPeopleAsync() => _service.GetPeopleAsync();

    public async Task EnsurePersonDropTargetsLoadedAsync(
        CancellationToken cancellationToken = default)
    {
        if (_arePersonTargetsLoaded) return;

        await _personTargetsGate.WaitAsync(cancellationToken);
        try
        {
            if (_arePersonTargetsLoaded) return;

            var targets = await _service.GetPersonDropTargetsAsync(cancellationToken);
            await ReconcilePersonDropTargetsAsync(targets);
            _arePersonTargetsLoaded = true;
        }
        finally
        {
            _personTargetsGate.Release();
        }
    }

    public async Task EnsurePeopleManagementLoadedAsync(
        CancellationToken cancellationToken = default)
    {
        if (_isPeopleManagementLoaded) return;

        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            if (_isPeopleManagementLoaded) return;

            IsBusy = true;
            StatusMessage = "Loading people...";
            var snapshot = await _service.GetPeopleManagementAsync(cancellationToken);
            // The user can select another person while the refresh is loading.
            // Preserve that latest choice rather than the selection from refresh start.
            var selectedPersonId = SelectedPerson?.Id;
            ReconcilePeople(snapshot.People);
            ReconcileFaces(
                HiddenFaces,
                snapshot.HiddenFaces,
                "Excluded from suggestions");
            SelectedPerson = People.FirstOrDefault(person => person.Id == selectedPersonId)
                             ?? People.FirstOrDefault();
            _isPeopleManagementLoaded = true;
            UpdateManagementStatusMessage();
        }
        finally
        {
            IsBusy = false;
            _refreshGate.Release();
        }
    }

    private void ReconcilePeople(IReadOnlyList<PersonFaceGroup> incoming)
    {
        var incomingIds = incoming.Select(group => group.Person.Id).ToHashSet();
        for (var index = People.Count - 1; index >= 0; index--)
        {
            if (!incomingIds.Contains(People[index].Id))
            {
                People.RemoveAt(index);
            }
        }

        for (var index = 0; index < incoming.Count; index++)
        {
            var source = incoming[index];
            var existing = People.FirstOrDefault(
                person => person.Id == source.Person.Id);
            if (existing is null)
            {
                People.Insert(index, new PersonManagementItemViewModel(source));
                continue;
            }

            existing.Update(source);
            var currentIndex = People.IndexOf(existing);
            if (currentIndex != index)
            {
                People.Move(currentIndex, index);
            }
        }
    }

    internal static void ReconcileFaces(
        ObservableCollection<FaceSuggestionItemViewModel> existing,
        IReadOnlyList<FaceSuggestion> incoming,
        string fallbackText)
    {
        var incomingIds = incoming
            .Select(face => face.FaceRegionId)
            .ToHashSet();
        for (var index = existing.Count - 1; index >= 0; index--)
        {
            if (!incomingIds.Contains(existing[index].FaceRegionId))
            {
                existing.RemoveAt(index);
            }
        }

        for (var index = 0; index < incoming.Count; index++)
        {
            var source = incoming[index];
            var item = existing.FirstOrDefault(
                face => face.FaceRegionId == source.FaceRegionId);
            if (item is null)
            {
                existing.Insert(
                    index,
                    new FaceSuggestionItemViewModel(source, fallbackText));
                continue;
            }

            item.Update(source);
            var currentIndex = existing.IndexOf(item);
            if (currentIndex != index)
            {
                existing.Move(currentIndex, index);
            }
        }
    }

    public async Task RenamePersonAsync(
        PersonManagementItemViewModel person,
        string name,
        CancellationToken cancellationToken = default)
    {
        var trimmedName = name.Trim();
        await RunManagementActionAsync(
            () => _service.RenamePersonAsync(person.Id, trimmedName, cancellationToken),
            () => person.Rename(trimmedName));
    }

    public async Task MergePersonAsync(
        PersonManagementItemViewModel source,
        PersonManagementItemViewModel target,
        CancellationToken cancellationToken = default)
    {
        await RunManagementActionAsync(
            () => _service.MergePersonsAsync(source.Id, target.Id, cancellationToken),
            () =>
            {
                target.AddFaces(source.Faces.ToList());
                People.Remove(source);
                SelectedPerson = target;
            });
    }

    public async Task SetRepresentativeFaceAsync(
        PersonManagementItemViewModel person,
        FaceSuggestionItemViewModel face,
        CancellationToken cancellationToken = default)
    {
        await RunManagementActionAsync(
            () => _service.SetPersonRepresentativeFaceAsync(
                person.Id,
                face.FaceRegionId,
                cancellationToken),
            () => person.SetRepresentativeFace(face.FaceRegionId));
    }

    public async Task ReassignFacesAsync(
        IReadOnlyCollection<FaceSuggestionItemViewModel> selected,
        Person target,
        CancellationToken cancellationToken = default)
    {
        var source = SelectedPerson
            ?? throw new InvalidOperationException("Select a person first.");
        if (selected.Count == 0)
        {
            throw new InvalidOperationException("Select at least one assigned face.");
        }
        if (source.Id == target.Id)
        {
            throw new InvalidOperationException(
                "The selected faces are already assigned to this person.");
        }

        var targetPerson = People.SingleOrDefault(person => person.Id == target.Id)
            ?? throw new InvalidOperationException("The target person no longer exists.");
        FaceTagOperation? operation = null;
        await RunManagementActionAsync(
            async () =>
            {
                operation = await _service.ReassignFacesAsync(
                    selected.Select(face => face.FaceRegionId).ToList(),
                    target.Id,
                    target.Name,
                    cancellationToken);
            },
            () =>
            {
                var selectedIds = selected
                    .Select(face => face.FaceRegionId)
                    .ToHashSet();
                source.RemoveFaces(selectedIds);
                targetPerson.AddFaces(selected);
            });
        RememberTagOperation(operation!);
    }

    public async Task UndoLastTagAsync(
        CancellationToken cancellationToken = default)
    {
        var operation = _lastTagOperation
            ?? throw new InvalidOperationException("There is no tag action to undo.");
        var selectedGroupId = SelectedGroup?.Source.Id;
        var selectedPersonId = SelectedPerson?.Id;

        IsBusy = true;
        try
        {
            await _service.UndoTagAsync(operation, cancellationToken);
            ClearTagOperation();
            var groupsTask = _service.GetSuggestionGroupsAsync(cancellationToken);
            var targetsTask = _service.GetPersonDropTargetsAsync(cancellationToken);
            var managementTask = _service.GetPeopleManagementAsync(cancellationToken);
            await Task.WhenAll(groupsTask, targetsTask, managementTask);

            ReconcileGroups(await groupsTask);
            await ReconcilePersonDropTargetsAsync(await targetsTask);
            var snapshot = await managementTask;
            ReconcilePeople(snapshot.People);
            ReconcileFaces(
                HiddenFaces,
                snapshot.HiddenFaces,
                "Excluded from suggestions");
            SelectedGroup = Groups.FirstOrDefault(
                                group => group.Source.Id == selectedGroupId)
                            ?? Groups.FirstOrDefault();
            SelectedPerson = People.FirstOrDefault(
                                 person => person.Id == selectedPersonId)
                             ?? People.FirstOrDefault();
            _isPeopleManagementLoaded = true;
            _arePersonTargetsLoaded = true;
            StatusMessage = $"Undid tag as {operation.TargetPersonName}.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RememberTagOperation(FaceTagOperation operation)
    {
        _lastTagOperation = operation;
        CanUndoLastTag = true;
    }

    private void ClearTagOperation()
    {
        _lastTagOperation = null;
        CanUndoLastTag = false;
    }

    public async Task UnassignFacesAsync(
        IReadOnlyCollection<FaceSuggestionItemViewModel> selected,
        CancellationToken cancellationToken = default)
    {
        var person = SelectedPerson
            ?? throw new InvalidOperationException("Select a person first.");
        if (selected.Count == 0)
        {
            throw new InvalidOperationException("Select at least one assigned face.");
        }

        await RunManagementActionAsync(
            () => _service.UnassignFacesAsync(
                selected.Select(item => item.FaceRegionId).ToList(),
                cancellationToken),
            () => person.RemoveFaces(
                selected.Select(item => item.FaceRegionId).ToHashSet()));
    }

    public async Task DeletePersonAsync(
        PersonManagementItemViewModel person,
        CancellationToken cancellationToken = default)
    {
        await RunManagementActionAsync(
            () => _service.DeletePersonAsync(person.Id, cancellationToken),
            () =>
            {
                var index = People.IndexOf(person);
                People.Remove(person);
                SelectedPerson = People.Count == 0
                    ? null
                    : People[Math.Min(index, People.Count - 1)];
            });
    }

    public async Task RestoreHiddenFacesAsync(
        IReadOnlyCollection<FaceSuggestionItemViewModel> selected,
        CancellationToken cancellationToken = default)
    {
        if (selected.Count == 0)
        {
            throw new InvalidOperationException("Select at least one excluded face.");
        }

        await RunManagementActionAsync(
            () => _service.RestoreHiddenFacesAsync(
                selected.Select(item => item.FaceRegionId).ToList(),
                cancellationToken),
            () =>
            {
                var selectedIds = selected
                    .Select(item => item.FaceRegionId)
                    .ToHashSet();
                for (var index = HiddenFaces.Count - 1; index >= 0; index--)
                {
                    if (selectedIds.Contains(HiddenFaces[index].FaceRegionId))
                    {
                        HiddenFaces.RemoveAt(index);
                    }
                }
            });
    }

    private void ReconcileGroups(IReadOnlyList<FaceSuggestionGroup> incoming)
    {
        var incomingIds = incoming.Select(group => group.Id).ToHashSet();
        for (var index = Groups.Count - 1; index >= 0; index--)
        {
            if (!incomingIds.Contains(Groups[index].Source.Id))
            {
                Groups.RemoveAt(index);
            }
        }

        for (var index = 0; index < incoming.Count; index++)
        {
            var source = incoming[index];
            var existing = Groups.FirstOrDefault(group => group.Source.Id == source.Id);
            if (existing is null)
            {
                Groups.Add(new FaceSuggestionGroupViewModel(source));
                continue;
            }

            existing.Update(source);
        }
    }

    private FaceSuggestionGroupViewModel? ResolveGroupAfterRefresh(
        string? selectedGroupId,
        int selectedGroupIndex,
        IReadOnlySet<long> selectedFaceIds)
    {
        var sameGroup = Groups.FirstOrDefault(
            group => group.Source.Id == selectedGroupId);
        if (sameGroup is not null)
        {
            return sameGroup;
        }

        if (selectedFaceIds.Count > 0)
        {
            var overlappingGroup = Groups
                .Select(group => new
                {
                    Group = group,
                    Overlap = group.Suggestions.Count(
                        face => selectedFaceIds.Contains(face.FaceRegionId))
                })
                .OrderByDescending(candidate => candidate.Overlap)
                .FirstOrDefault();
            if (overlappingGroup?.Overlap > 0)
            {
                return overlappingGroup.Group;
            }
        }

        return Groups.Count == 0
            ? null
            : Groups[Math.Clamp(selectedGroupIndex, 0, Groups.Count - 1)];
    }

    private async Task RunIncrementalAsync(
        Func<Task> action,
        Action applyLocally,
        bool refreshPersonTargets = false)
    {
        IsBusy = true;
        try
        {
            await action();
            applyLocally();
            ClearTagOperation();
            _isPeopleManagementLoaded = false;
            if (refreshPersonTargets)
            {
                _arePersonTargetsLoaded = false;
                await EnsurePersonDropTargetsLoadedAsync();
            }
            UpdateStatusMessage();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunManagementActionAsync(
        Func<Task> action,
        Action applyLocally)
    {
        IsBusy = true;
        try
        {
            await action();
            applyLocally();
            ClearTagOperation();
            _arePersonTargetsLoaded = false;
            await EnsurePersonDropTargetsLoadedAsync();
            UpdateManagementStatusMessage();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RerouteRejectedSuggestions(
        FaceSuggestionGroupViewModel sourceGroup,
        IReadOnlyCollection<FaceSuggestionItemViewModel> selected,
        long rejectedPersonId)
    {
        var selectedIds = selected.Select(item => item.FaceRegionId).ToHashSet();
        var usedPersonIdsByImage = Groups
            .Where(group => group.HasSuggestedPerson)
            .SelectMany(group => group.Suggestions
                .Where(item => !selectedIds.Contains(item.FaceRegionId))
                .Select(item => (
                    item.ImageId,
                    PersonId: group.Source.SuggestedPersonId!.Value)))
            .GroupBy(item => item.ImageId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.PersonId).ToHashSet());

        sourceGroup.RemoveSuggestions(selectedIds);
        foreach (var item in selected)
        {
            item.MarkRejected(rejectedPersonId);
            if (!usedPersonIdsByImage.TryGetValue(item.ImageId, out var usedPersonIds))
            {
                usedPersonIds = [];
                usedPersonIdsByImage.Add(item.ImageId, usedPersonIds);
            }

            var candidate = item.GetNextCandidate(usedPersonIds);
            if (candidate is null)
            {
                item.MoveToUnnamed();
                GetOrCreateUnnamedGroup().AddSuggestion(item);
                continue;
            }

            item.MoveToCandidate(candidate);
            var destination = Groups.FirstOrDefault(
                group => group.Source.SuggestedPersonId == candidate.PersonId);
            if (destination is null)
            {
                destination = new FaceSuggestionGroupViewModel(
                    new FaceSuggestionGroup(
                        $"person:{candidate.PersonId}",
                        candidate.PersonId,
                        candidate.PersonName,
                        candidate.ConfirmedCount,
                        []));
                Groups.Add(destination);
            }
            destination.AddSuggestion(item);
            usedPersonIds.Add(candidate.PersonId);
        }

        RemoveGroupIfEmpty(sourceGroup);
    }

    private FaceSuggestionGroupViewModel GetOrCreateUnnamedGroup()
    {
        var group = Groups.FirstOrDefault(candidate => !candidate.HasSuggestedPerson);
        if (group is not null)
        {
            return group;
        }

        group = new FaceSuggestionGroupViewModel(
            new FaceSuggestionGroup(
                "unnamed:incremental",
                null,
                "Similar untagged faces",
                0,
                []));
        Groups.Add(group);
        return group;
    }

    private void RemoveSuggestions(
        FaceSuggestionGroupViewModel group,
        IReadOnlyCollection<FaceSuggestionItemViewModel> selected)
    {
        group.RemoveSuggestions(
            selected.Select(item => item.FaceRegionId).ToHashSet());
        RemoveGroupIfEmpty(group);
    }

    private void RemoveGroupIfEmpty(FaceSuggestionGroupViewModel group)
    {
        if (group.Suggestions.Count != 0) return;
        RemoveGroup(group);
    }

    private void RemoveGroup(FaceSuggestionGroupViewModel group)
    {
        var index = Groups.IndexOf(group);
        if (index < 0) return;

        Groups.RemoveAt(index);
        SelectedGroup = Groups.Count == 0
            ? null
            : Groups[Math.Min(index, Groups.Count - 1)];
    }

    private void UpdateStatusMessage()
    {
        StatusMessage = Groups.Count == 0
            ? "No face suggestions need review."
            : $"{Groups.Sum(group => group.Suggestions.Count)} suggestions in {Groups.Count} groups";
    }

    private void UpdateManagementStatusMessage()
    {
        StatusMessage =
            $"{People.Count} people, " +
            $"{People.Sum(person => person.Faces.Count)} assigned faces, " +
            $"{HiddenFaces.Count} excluded faces";
    }

    private FaceSuggestionGroupViewModel RequireSelection(
        IReadOnlyCollection<FaceSuggestionItemViewModel> selected)
    {
        if (SelectedGroup is null)
        {
            throw new InvalidOperationException("Select a suggestion group first.");
        }
        if (selected.Count == 0)
        {
            throw new InvalidOperationException("Select at least one face suggestion.");
        }
        return SelectedGroup;
    }
}

public sealed partial class PersonManagementItemViewModel : ObservableObject
{
    public PersonManagementItemViewModel(PersonFaceGroup source)
    {
        Source = source.Person;
        Faces = new ObservableCollection<FaceSuggestionItemViewModel>(
            source.Faces.Select(face => new FaceSuggestionItemViewModel(
                face,
                "Assigned face")));
        Source.FaceCount = Faces.Count;
    }

    public Person Source { get; }
    public long Id => Source.Id;
    public string Name => Source.Name;
    public string Summary => Source.SuggestionsHidden
        ? $"{Faces.Count} faces - suggestions hidden"
        : $"{Faces.Count} faces";
    public ObservableCollection<FaceSuggestionItemViewModel> Faces { get; }

    public void Update(PersonFaceGroup source)
    {
        Source.Name = source.Person.Name;
        Source.SuggestionsHidden = source.Person.SuggestionsHidden;
        Source.RepresentativeFaceRegionId =
            source.Person.RepresentativeFaceRegionId;
        PeopleReviewViewModel.ReconcileFaces(
            Faces,
            source.Faces,
            "Assigned face");
        Source.FaceCount = Faces.Count;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Summary));
    }

    public void Rename(string name)
    {
        Source.Name = name;
        OnPropertyChanged(nameof(Name));
    }

    public void SetRepresentativeFace(long faceRegionId)
    {
        Source.RepresentativeFaceRegionId = faceRegionId;
    }

    public void AddFaces(IReadOnlyCollection<FaceSuggestionItemViewModel> faces)
    {
        foreach (var face in faces)
        {
            Faces.Add(face);
        }
        RefreshCount();
    }

    public void RemoveFaces(IReadOnlySet<long> faceIds)
    {
        if (Source.RepresentativeFaceRegionId is long representativeFaceId &&
            faceIds.Contains(representativeFaceId))
        {
            Source.RepresentativeFaceRegionId = null;
        }
        for (var index = Faces.Count - 1; index >= 0; index--)
        {
            if (faceIds.Contains(Faces[index].FaceRegionId))
            {
                Faces.RemoveAt(index);
            }
        }
        RefreshCount();
    }

    private void RefreshCount()
    {
        Source.FaceCount = Faces.Count;
        OnPropertyChanged(nameof(Summary));
    }
}

public sealed partial class PersonDropTargetViewModel : ObservableObject
{
    private FaceSuggestionItemViewModel? _representativeFace;
    private Task? _imageLoadTask;

    public PersonDropTargetViewModel(PersonDropTarget source)
    {
        Source = source.Person;
        SetRepresentativeFace(source.RepresentativeFace);
    }

    public Person Source { get; }
    public long Id => Source.Id;
    public string Name => Source.Name;
    public int FaceCount => Source.FaceCount;
    public string DisplayLabel => $"{Name} ({FaceCount})";
    public string AutomationName => $"{Name}, {FaceCount} assigned faces";

    [ObservableProperty]
    public partial ImageSource? ProfilePicture { get; set; }

    public bool Update(PersonDropTarget source)
    {
        Source.Name = source.Person.Name;
        Source.FaceCount = source.Person.FaceCount;
        Source.RepresentativeFaceRegionId =
            source.Person.RepresentativeFaceRegionId;
        var representativeChanged =
            _representativeFace?.FaceRegionId !=
            source.RepresentativeFace?.FaceRegionId;
        if (representativeChanged)
        {
            SetRepresentativeFace(source.RepresentativeFace);
        }
        else if (_representativeFace is not null &&
                 source.RepresentativeFace is not null)
        {
            _representativeFace.Update(source.RepresentativeFace);
        }

        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(FaceCount));
        OnPropertyChanged(nameof(DisplayLabel));
        OnPropertyChanged(nameof(AutomationName));
        return representativeChanged;
    }

    public Task LoadProfilePictureAsync()
    {
        if (ProfilePicture is not null || _representativeFace is null)
        {
            return Task.CompletedTask;
        }
        return _imageLoadTask ??= LoadProfilePictureCoreAsync();
    }

    public async Task LoadProfilePictureSafelyAsync()
    {
        try
        {
            await LoadProfilePictureAsync();
        }
        catch (Exception ex)
        {
            DebugLog.WriteLine(
                $"PeopleReviewViewModel: Could not load picture for {Name}: {ex.Message}");
        }
    }

    private async Task LoadProfilePictureCoreAsync()
    {
        var representativeFace = _representativeFace!;
        await representativeFace.LoadThumbnailAsync();
        if (ReferenceEquals(_representativeFace, representativeFace))
        {
            ProfilePicture = representativeFace.Thumbnail;
        }
    }

    private void SetRepresentativeFace(FaceSuggestion? source)
    {
        _representativeFace = source is null
            ? null
            : new FaceSuggestionItemViewModel(source, "Representative face");
        _imageLoadTask = null;
        ProfilePicture = null;
    }
}

public sealed partial class FaceSuggestionGroupViewModel : ObservableObject
{
    public FaceSuggestionGroupViewModel(FaceSuggestionGroup source)
    {
        Source = source;
        Suggestions = new ObservableCollection<FaceSuggestionItemViewModel>(
            source.Suggestions.Select(suggestion => new FaceSuggestionItemViewModel(suggestion)));
    }

    public FaceSuggestionGroup Source { get; private set; }
    public string DisplayName => Source.DisplayName;
    public bool HasSuggestedPerson => Source.SuggestedPersonId.HasValue;
    public ObservableCollection<FaceSuggestionItemViewModel> Suggestions { get; }
    public string Summary
    {
        get
        {
            var similarities = Suggestions
                .Where(suggestion => suggestion.Similarity.HasValue)
                .Select(suggestion => suggestion.Similarity!.Value)
                .ToList();
            return similarities.Count == 0
                ? $"{Suggestions.Count} similar untagged faces"
                : $"{Suggestions.Count} suggestions, " +
                  $"{similarities.Average():P0} average similarity, " +
                  $"{Source.ConfirmedCount} confirmed";
        }
    }
    public string Header => HasSuggestedPerson ? $"{DisplayName}?" : DisplayName;

    public void Update(FaceSuggestionGroup source)
    {
        Source = source;
        var incomingIds = source.Suggestions
            .Select(suggestion => suggestion.FaceRegionId)
            .ToHashSet();
        for (var index = Suggestions.Count - 1; index >= 0; index--)
        {
            if (!incomingIds.Contains(Suggestions[index].FaceRegionId))
            {
                Suggestions.RemoveAt(index);
            }
        }

        for (var index = 0; index < source.Suggestions.Count; index++)
        {
            var suggestion = source.Suggestions[index];
            var existing = Suggestions.FirstOrDefault(
                item => item.FaceRegionId == suggestion.FaceRegionId);
            if (existing is null)
            {
                Suggestions.Add(new FaceSuggestionItemViewModel(suggestion));
                continue;
            }

            existing.Update(suggestion);
        }

        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(HasSuggestedPerson));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Header));
    }

    public void RemoveSuggestions(IReadOnlySet<long> faceRegionIds)
    {
        for (var index = Suggestions.Count - 1; index >= 0; index--)
        {
            if (faceRegionIds.Contains(Suggestions[index].FaceRegionId))
            {
                Suggestions.RemoveAt(index);
            }
        }
        RefreshSourceAndSummary();
    }

    public void AddSuggestion(FaceSuggestionItemViewModel suggestion)
    {
        var index = 0;
        while (index < Suggestions.Count &&
               (Suggestions[index].Similarity ?? float.MinValue) >=
               (suggestion.Similarity ?? float.MinValue))
        {
            index++;
        }
        Suggestions.Insert(index, suggestion);
        RefreshSourceAndSummary();
    }

    private void RefreshSourceAndSummary()
    {
        Source = Source with
        {
            Suggestions = Suggestions.Select(item => item.Source).ToList()
        };
        OnPropertyChanged(nameof(Summary));
    }
}

public partial class FaceSuggestionItemViewModel : ObservableObject
{
    private FaceSuggestion _source;
    private readonly string _fallbackText;
    private Task? _thumbnailLoadTask;
    private Task? _contextImageLoadTask;
    private readonly HashSet<long> _rejectedPersonIds = [];

    public FaceSuggestionItemViewModel(
        FaceSuggestion source,
        string fallbackText = "Similar unnamed face")
    {
        _source = source;
        _fallbackText = fallbackText;
    }

    public long FaceRegionId => _source.FaceRegionId;
    public long ImageId => _source.ImageId;
    public FaceSuggestion Source => _source;
    public string FileName => Path.GetFileName(_source.ImagePath);
    public float? Similarity => _source.Similarity;
    public string SimilarityText => Similarity is float similarity
        ? $"{similarity:P0} similarity"
        : _fallbackText;

    public void Update(FaceSuggestion source)
    {
        _source = source;
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(Similarity));
        OnPropertyChanged(nameof(SimilarityText));
    }

    public void MarkRejected(long personId) => _rejectedPersonIds.Add(personId);

    public FacePersonCandidate? GetNextCandidate(IReadOnlySet<long> usedPersonIds) =>
        _source.Candidates.FirstOrDefault(
            candidate => !_rejectedPersonIds.Contains(candidate.PersonId) &&
                         !usedPersonIds.Contains(candidate.PersonId));

    public void MoveToCandidate(FacePersonCandidate candidate)
    {
        _source = _source with { Similarity = candidate.Similarity };
        OnPropertyChanged(nameof(Similarity));
        OnPropertyChanged(nameof(SimilarityText));
    }

    public void MoveToUnnamed()
    {
        _source = _source with { Similarity = null };
        OnPropertyChanged(nameof(Similarity));
        OnPropertyChanged(nameof(SimilarityText));
    }

    [ObservableProperty]
    public partial ImageSource? Thumbnail { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; } = true;

    [ObservableProperty]
    public partial ImageSource? ContextImage { get; set; }

    [ObservableProperty]
    public partial bool IsContextImageLoading { get; set; }

    public Task LoadThumbnailAsync()
    {
        if (Thumbnail is not null) return Task.CompletedTask;
        return _thumbnailLoadTask ??= LoadThumbnailCoreAsync();
    }

    private async Task LoadThumbnailCoreAsync()
    {
        try
        {
            var bytes = await FaceThumbnailCropper.CreateAsync(
                _source.ImagePath,
                new FaceRegion
                {
                    X = _source.X,
                    Y = _source.Y,
                    Width = _source.Width,
                    Height = _source.Height
                },
                192);
            var bitmap = new BitmapImage();
            using var stream = new MemoryStream(bytes);
            await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            Thumbnail = bitmap;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public Task LoadContextImageAsync()
    {
        if (ContextImage is not null) return Task.CompletedTask;
        return _contextImageLoadTask ??= LoadContextImageCoreAsync();
    }

    private async Task LoadContextImageCoreAsync()
    {
        IsContextImageLoading = true;
        try
        {
            var bytes = await WindowsThumbnailService.GetThumbnailStreamAsync(
                _source.ImagePath,
                960);
            if (bytes is null)
            {
                throw new InvalidDataException(
                    $"Windows could not create a preview for {FileName}.");
            }

            var bitmap = new BitmapImage();
            using var stream = new MemoryStream(bytes);
            await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            ContextImage = bitmap;
        }
        finally
        {
            IsContextImageLoading = false;
        }
    }
}
