using CommunityToolkit.Mvvm.ComponentModel;
using PhotoLibrarian.Core.Data;
using System.Collections.ObjectModel;

namespace PhotoLibrarian.ViewModels;

/// <summary>
/// ViewModel for people-based navigation.
/// Lists named people and the number of photos assigned to each person.
/// </summary>
public partial class PeopleNavigationViewModel : ObservableObject
{
    private readonly FaceRepository _faceRepo;

    public ObservableCollection<PersonNode> RootNodes { get; } = [];

    public PeopleNavigationViewModel(FaceRepository faceRepo)
    {
        _faceRepo = faceRepo;
    }

    public async Task LoadPeopleAsync()
    {
        var people = await _faceRepo.GetAllPersonsAsync();
        var personIdsByImageId = await _faceRepo.GetPersonIdsByImageIdAsync();
        var imageCounts = personIdsByImageId
            .SelectMany(pair => pair.Value)
            .GroupBy(personId => personId)
            .ToDictionary(group => group.Key, group => group.Count());

        var root = new PersonNode
        {
            DisplayName = "👥 People",
            Count = personIdsByImageId.Count,
            IsRoot = true
        };

        foreach (var person in people.OrderBy(
            person => person.Name,
            StringComparer.OrdinalIgnoreCase))
        {
            root.Children.Add(new PersonNode
            {
                DisplayName = person.Name,
                PersonId = person.Id,
                Count = imageCounts.GetValueOrDefault(person.Id)
            });
        }

        RootNodes.Clear();
        RootNodes.Add(root);
    }
}

public sealed class PersonNode
{
    public string DisplayName { get; set; } = "";
    public long? PersonId { get; set; }
    public int Count { get; set; }
    public bool IsRoot { get; set; }
    public ObservableCollection<PersonNode> Children { get; } = [];
}
