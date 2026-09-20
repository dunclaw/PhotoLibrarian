using PhotoLibrarian.Core.Models;
using PhotoLibrarian.ML.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class RecentPeopleStoreTests
{
    [Fact]
    public void SaveAndLoad_PersistsFourDistinctPeopleInOrder()
    {
        var settingsPath = Path.Combine(
            Path.GetTempPath(),
            $"PhotoLibrarian-recent-people-{Guid.NewGuid():N}.json");

        try
        {
            var store = new RecentPeopleStore(settingsPath);
            store.Save(
            [
                new Person { Id = 5, Name = "Newest" },
                new Person { Id = 4, Name = "Second" },
                new Person { Id = 4, Name = "Duplicate" },
                new Person { Id = 3, Name = "Third" },
                new Person { Id = 2, Name = "Fourth" },
                new Person { Id = 1, Name = "Too old" }
            ]);

            var reloaded = new RecentPeopleStore(settingsPath).Load();

            Assert.Equal([5L, 4L, 3L, 2L], reloaded.Select(person => person.Id));
            Assert.Equal(
                ["Newest", "Second", "Third", "Fourth"],
                reloaded.Select(person => person.Name));
        }
        finally
        {
            File.Delete(settingsPath);
            File.Delete(settingsPath + ".tmp");
        }
    }
}
