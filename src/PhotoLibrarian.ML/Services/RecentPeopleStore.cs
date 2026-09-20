using PhotoLibrarian.Core.Models;
using System.Text.Json;

namespace PhotoLibrarian.ML.Services;

public sealed class RecentPeopleStore
{
    private const int MaximumPeople = 4;
    private readonly string _settingsPath;
    private readonly object _sync = new();

    public RecentPeopleStore(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhotoLibrarian",
            "recent-people.json");
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
            var directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = _settingsPath + ".tmp";
            try
            {
                File.WriteAllText(
                    temporaryPath,
                    JsonSerializer.Serialize(
                        Normalize(people),
                        new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporaryPath, _settingsPath, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
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
