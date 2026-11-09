// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Data.Sqlite;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.ViewModels;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class HierarchicalTagPortabilityTests : IDisposable
{
    private const string Dog = "Auto/animal/mammal/dog";
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"PhotoLibrarian-tag-hierarchy-{Guid.NewGuid():N}");

    public HierarchicalTagPortabilityTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".raw")]
    public async Task CompositeTag_MovesAndReimportsWithoutPortableAncestors(string extension)
    {
        var path = await CreateMediaAsync("source" + extension);
        using (var database = await OpenDatabaseAsync("original.db"))
        {
            var image = await IndexAsync(database, path);
            var tags = new TagRepository(database);
            Assert.True(await tags.TryReplaceAutoTagsAsync(
                image, [new("animal/mammal/dog", 0.87f)], "test-v1"));
            var stored = await tags.GetTagsAsync(image.Id);
            Assert.Equal(Dog, Assert.Single(stored).Tag);
            await TagWriterService.WriteTagsToSidecarAsync(
                path, stored.Select(tag => tag.Tag), stored, "test-v1",
                sourceIdentity: image.SourceIdentity);
            Assert.Equal([Dog], new MetadataReaderService().ReadTags(path));
            Assert.Equal(Dog, Assert.Single(
                new MetadataReaderService().ReadPortableTagMetadata(path)!.Tags).Tag);
            if (extension == ".jpg")
                Assert.False(File.Exists(Path.ChangeExtension(path, ".xmp")));
        }

        var moved = Path.Combine(_directory, "moved" + extension);
        File.Move(path, moved);
        if (File.Exists(Path.ChangeExtension(path, ".xmp")))
            File.Move(Path.ChangeExtension(path, ".xmp"), Path.ChangeExtension(moved, ".xmp"));

        using (var database = await OpenDatabaseAsync("fresh.db"))
        {
            var image = await IndexAsync(database, moved);
            var tags = new TagRepository(database);
            var stored = Assert.Single(await tags.GetTagsAsync(image.Id));
            Assert.Equal(Dog, stored.Tag);
            Assert.Equal(TagSource.AutoML, stored.Source);
            Assert.Equal(0.87f, stored.Confidence);
            Assert.Equal("test-v1", image.AutoTagScanVersion);
            await AssertHierarchyAsync(database, image);

            var panel = new MetadataPanelViewModel();
            panel.Initialize(tagRepo: tags);
            panel.ShowMetadata(image);
            await panel.ReloadTagsAsync();
            Assert.Equal(Dog, Assert.Single(panel.Tags).Tag);
            await panel.StopVideoMetadataReadsAsync();
        }

        using (var database = await OpenDatabaseAsync("fresh.db"))
        {
            var image = (await new ImageRepository(database).GetByPathAsync(moved))!;
            Assert.Equal(Dog, Assert.Single(await new TagRepository(database).GetTagsAsync(image.Id)).Tag);
            await AssertHierarchyAsync(database, image);
        }
    }

    [Fact]
    public async Task PanelAddAndDelete_PreserveExplicitParentsAndSiblingMembership()
    {
        var firstPath = await CreateMediaAsync("first.raw");
        var secondPath = await CreateMediaAsync("second.raw");
        using var database = await OpenDatabaseAsync("panel.db");
        var first = await IndexAsync(database, firstPath);
        var second = await IndexAsync(database, secondPath);
        var tags = new TagRepository(database);
        var panel = new MetadataPanelViewModel();
        panel.Initialize(tagRepo: tags);
        panel.ShowMetadata(new[] { first, second });
        await panel.AddTagAsync(Dog);
        await panel.ReloadTagsAsync();
        Assert.True(Assert.Single(panel.Tags).IsOnAll);
        Assert.Equal([Dog], new MetadataReaderService().ReadTags(firstPath));
        Assert.Equal([Dog], new MetadataReaderService().ReadTags(secondPath));
        await tags.AddTagAsync(new ImageTag { ImageId = first.Id, Tag = "Auto/animal" });
        await tags.AddTagAsync(new ImageTag { ImageId = first.Id, Tag = "Auto/animal/mammal/cat" });
        await panel.ReloadTagsAsync();
        Assert.Equal(3, panel.Tags.Count);
        Assert.Equal(1, Assert.Single(panel.Tags, tag => tag.Tag == "Auto/animal").PresentCount);

        await panel.RemoveTagAsync(Dog);
        Assert.DoesNotContain(panel.Tags, tag => tag.Tag == Dog);
        Assert.Equal(new[] { "Auto/animal", "Auto/animal/mammal/cat" },
            new MetadataReaderService().ReadTags(firstPath).Order());
        Assert.Empty(new MetadataReaderService().ReadTags(secondPath));
        Assert.Equal([first.Id], await tags.GetImageIdsWithTagAsync("Auto/animal"));
        Assert.Empty(await tags.GetImageIdsWithTagAsync(Dog));
        Assert.Equal(1, await tags.GetNotTaggedCountAsync());

        await panel.RemoveTagAsync("Auto/animal");
        Assert.Equal([first.Id], await tags.GetImageIdsWithTagAsync("Auto/animal"));
        await panel.RemoveTagAsync("Auto/animal/mammal/cat");
        Assert.Empty(await tags.GetImageIdsWithTagAsync("Auto"));
        await panel.StopVideoMetadataReadsAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacySerializedPrefixes_ArePreservedWithoutGuessingIntent(bool provenance)
    {
        var path = await CreateMediaAsync("legacy.raw");
        string[] values = ["Auto", "Auto/animal", "Auto/animal/mammal", Dog, "family"];
        var rows = values.Select(value => new ImageTag
        {
            Tag = value, Source = TagSource.Manual, Confidence = 0.8f
        }).ToArray();
        await TagWriterService.WriteTagsToSidecarAsync(path, values, provenance ? rows : null);
        using var database = await OpenDatabaseAsync("legacy.db");
        var image = await IndexAsync(database, path);
        var tags = new TagRepository(database);
        Assert.Equal(values.Order(), (await tags.GetTagsAsync(image.Id)).Select(tag => tag.Tag).Order());
        await AssertHierarchyAsync(database, image);
        await tags.RemoveTagAsync(image.Id, "Auto/animal");
        Assert.Equal([image.Id], await tags.GetImageIdsWithTagAsync("Auto/animal"));
        var remaining = await tags.GetTagsAsync(image.Id);
        await TagWriterService.WriteTagsToSidecarAsync(path, remaining.Select(tag => tag.Tag), remaining);
        await IndexAsync(database, path);
        Assert.Equal(values.Where(value => value != "Auto/animal").Order(),
            (await tags.GetTagsAsync(image.Id)).Select(tag => tag.Tag).Order());
    }

    [Fact]
    public async Task PortableComposite_RepairsStaleExpandedCacheOnReindex()
    {
        var path = await CreateMediaAsync("repair.raw");
        var portable = new ImageTag { Tag = Dog, Source = TagSource.AutoML, Confidence = 0.9f };
        await TagWriterService.WriteTagsToSidecarAsync(path, [Dog], [portable]);
        using var database = await OpenDatabaseAsync("repair.db");
        var image = await IndexAsync(database, path);
        var tags = new TagRepository(database);
        foreach (var prefix in new[] { "Auto", "Auto/animal", "Auto/animal/mammal" })
            await tags.ImportTagAsync(image.Id, prefix, TagSource.AutoML, 0.9f);
        await IndexAsync(database, path);
        Assert.Equal(Dog, Assert.Single(await tags.GetTagsAsync(image.Id)).Tag);
        Assert.Equal([Dog], new MetadataReaderService().ReadTags(path));
    }

    [Fact]
    public async Task TreeCountsDistinctPhotosAndUpdatesAfterRenameMergeAndDelete()
    {
        using var database = await OpenDatabaseAsync("tree.db");
        var first = await IndexAsync(database, await CreateMediaAsync("tree-first.raw"));
        var second = await IndexAsync(database, await CreateMediaAsync("tree-second.raw"));
        using (var connection = database.CreateConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO watched_folders(path) VALUES ($path)";
            command.Parameters.AddWithValue("$path", _directory);
            await command.ExecuteNonQueryAsync();
        }
        var tags = new TagRepository(database);
        await tags.AddTagAsync(new ImageTag { ImageId = first.Id, Tag = Dog });
        await tags.AddTagAsync(new ImageTag { ImageId = first.Id, Tag = "Auto/animal/mammal/cat" });
        await tags.AddTagAsync(new ImageTag { ImageId = first.Id, Tag = "Auto/animal" });
        await tags.AddTagAsync(new ImageTag { ImageId = second.Id, Tag = Dog });
        var tree = new TagNavigationViewModel(tags);
        await tree.LoadTagsAsync();
        var root = Assert.Single(tree.RootTags);
        Assert.Equal(2, root.Count);
        var auto = Assert.Single(root.Children);
        Assert.Equal(2, auto.Count);
        var animal = Assert.Single(auto.Children);
        Assert.Equal(2, animal.Count);
        var mammal = Assert.Single(animal.Children);
        Assert.Equal(2, mammal.Count);
        Assert.Equal(1, Assert.Single(mammal.Children, node => node.Name == "cat").Count);
        Assert.Equal(2, Assert.Single(mammal.Children, node => node.Name == "dog").Count);

        await tags.RenameTagAsync(Dog, "Pets/dog");
        Assert.Empty(await tags.GetImageIdsWithTagAsync(Dog));
        Assert.Equal(new[] { first.Id, second.Id }, (await tags.GetImageIdsWithTagAsync("Pets")).Order());
        await tags.MergeTagsAsync("Auto/animal/mammal/cat", "Pets/dog");
        Assert.Empty(await tags.GetImageIdsWithTagAsync("Auto/animal/mammal"));
        await tags.RemoveTagAsync(first.Id, "Auto/animal");
        await tree.LoadTagsAsync();
        Assert.Equal("Pets", Assert.Single(Assert.Single(tree.RootTags).Children).Name);
        await new ImageRepository(database).DeleteByPathAsync(first.FilePath);
        Assert.Equal([second.Id], await tags.GetImageIdsWithTagAsync("Pets"));

        using var probeConnection = database.CreateConnection();
        using var probe = probeConnection.CreateCommand();
        probe.CommandText = "EXPLAIN QUERY PLAN SELECT image_id FROM tag_hierarchy WHERE tag = 'Pets'";
        var plan = new List<string>();
        using var reader = await probe.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            plan.Add(reader.GetString(3));
        Assert.Contains(plan, line => line.Contains("idx_tag_paths_tag_image", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OldCache_BackfillsMembershipWithoutCollapsingAmbiguousRows()
    {
        var path = await CreateMediaAsync("old-cache.raw");
        long imageId;
        using (var database = await OpenDatabaseAsync("old.db"))
        {
            imageId = (await IndexAsync(database, path)).Id;
            using var connection = database.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TRIGGER tag_paths_INSERT;
                DROP TRIGGER tag_paths_UPDATE;
                DROP TRIGGER tag_paths_delete;
                DROP VIEW tag_hierarchy;
                DROP TABLE tag_path_memberships;
                """;
            await command.ExecuteNonQueryAsync();
            var tags = new TagRepository(database);
            await tags.ImportTagAsync(imageId, "Auto", TagSource.AutoML, 0.8f);
            await tags.ImportTagAsync(imageId, Dog, TagSource.AutoML, 0.8f);
        }
        using (var database = await OpenDatabaseAsync("old.db"))
        {
            var tags = new TagRepository(database);
            Assert.Equal(2, (await tags.GetTagsAsync(imageId)).Count);
            Assert.Equal([imageId], await tags.GetImageIdsWithTagAsync("Auto/animal"));
            Assert.Equal(1, Assert.Single(await tags.GetAllTagsWithCountAsync(),
                row => row.Tag == "Auto").Count);
        }
    }

    [Theory]
    [InlineData("Places/100%_wild/dog")]
    [InlineData("Places//dog/")]
    public async Task ImportPreservesSerializedValueAndUsesLiteralHierarchyMembership(string value)
    {
        using var database = await OpenDatabaseAsync("literal.db");
        var image = await IndexAsync(database, await CreateMediaAsync("literal.raw"));
        var tags = new TagRepository(database);
        await tags.AddMetadataTagAsync(image.Id, value);
        Assert.Equal(value, Assert.Single(await tags.GetTagsAsync(image.Id)).Tag);
        Assert.Equal([image.Id], await tags.GetImageIdsWithTagAsync(value));
        Assert.Equal([image.Id], await tags.GetImageIdsWithTagAsync("Places"));
        Assert.Empty(await tags.GetImageIdsWithTagAsync("Places/100XXwild"));
        await tags.RemoveTagAsync(image.Id, value);
        Assert.Empty(await tags.GetImageIdsWithTagAsync("Places"));
    }

    private static async Task AssertHierarchyAsync(CacheDatabase database, ImageEntry image)
    {
        var tags = new TagRepository(database);
        var images = new ImageRepository(database);
        foreach (var prefix in new[] { "Auto", "Auto/animal", "Auto/animal/mammal", Dog })
        {
            Assert.Equal([image.Id], await tags.GetImageIdsWithTagAsync(prefix));
            Assert.Equal(image.Id, Assert.Single(await images.GetFilteredAsync(false, [prefix])).Id);
            Assert.Equal(1, Assert.Single(await tags.GetAllTagsWithCountAsync(), row => row.Tag == prefix).Count);
        }
        Assert.Empty(await tags.GetImageIdsWithTagAsync("Auto/anim"));
        var membership = (await tags.GetTagsByImageIdAsync())[image.Id];
        Assert.True(new ImageRefinementFilter { IncludedTags = ["Auto/animal"] }.Matches(image, membership));
        Assert.False(new ImageRefinementFilter { ExcludedTags = ["Auto/animal"] }.Matches(image, membership));
        Assert.Equal(image.Id, Assert.Single(await new ImageSearchRepository(database)
            .FilterAsync("animal", [image])).Id);
    }

    private async Task<CacheDatabase> OpenDatabaseAsync(string name)
    {
        var database = new CacheDatabase(Path.Combine(_directory, name));
        await database.InitializeAsync();
        return database;
    }

    private static async Task<ImageEntry> IndexAsync(CacheDatabase database, string path)
    {
        var images = new ImageRepository(database);
        using var scanner = new FolderScannerService();
        var indexer = new LibraryIndexingService(database, images, new TagRepository(database),
            new FaceRepository(database), scanner, new MetadataReaderService(), new FaceMetadataStore());
        await indexer.IndexFileAsync(path);
        return (await images.GetByPathAsync(path))!;
    }

    private async Task<string> CreateMediaAsync(string name)
    {
        var path = Path.Combine(_directory, name);
        if (Path.GetExtension(name) == ".raw")
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            return path;
        }
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream.AsRandomAccessStream());
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, 2, 2, 96, 96,
            [20, 40, 80, 255, 80, 40, 20, 255, 40, 80, 20, 255, 80, 20, 40, 255]);
        await encoder.FlushAsync();
        return path;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }
}
