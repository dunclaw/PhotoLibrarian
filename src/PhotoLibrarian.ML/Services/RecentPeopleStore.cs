// Copyright (c) Duncan Lawler. All Rights Reserved.

using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using System.Text.Json;

namespace PhotoLibrarian.ML.Services;

public sealed class RecentPeopleStore
{
    private const int MaximumPeople = 4;
    private readonly string _settingsPath;
    private readonly object _sync = new();

    public RecentPeopleStore(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? AppDataPaths.SettingsFile("recent-people.json");
    }

    public IReadOnlyList<Person> Load()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(_settingsPath)) return [];
                return Normalize(
                    JsonSerializer.Deserialize<List<Person>>(
                        File.ReadAllText(_settingsPath)) ?? []);
            }
            catch (IOException)
            {
                return [];
            }
            catch (UnauthorizedAccessException)
            {
                return [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }

    public void Save(IReadOnlyList<Person> people)
    {
        lock (_sync)
        {
            AppSettingsFile.WriteAllText(
                _settingsPath,
                JsonSerializer.Serialize(
                    Normalize(people),
                    new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static List<Person> Normalize(IEnumerable<Person> people) =>
        people
            .Where(person => person.Id > 0 && !string.IsNullOrWhiteSpace(person.Name))
            .DistinctBy(person => person.Id)
            .Take(MaximumPeople)
            .Select(person => new Person
            {
                Id = person.Id,
                Name = person.Name.Trim()
            })
            .ToList();
}
