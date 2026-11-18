// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Text.Json.Nodes;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class PhotoVersionDocumentTests
{
    [Fact]
    public void CreatingCopyingRenamingAndRemovingCopiesKeepsMainAndStickyMode()
    {
        var sourceId = Guid.NewGuid();
        var mainId = Guid.NewGuid();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryCreate(
            sourceId, "pixels-v1:generated", mainId, "Original", "main packet",
            firstId, "Copy 1", out var created));
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryRead(created!, out var document));
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            document!.TryAddCopy(firstId, secondId, "Copy 2", document.Revision, out var added));
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryRead(added!, out document));
        var records = document!.GetRecords();
        Assert.Equal(new[] { mainId, firstId, secondId }, records.Select(record => record.Id));
        Assert.True(records[0].IsMain);
        Assert.All(records.Skip(1), record => Assert.False(record.IsMain));
        Assert.Equal("Copy 2", records[2].Name);
        Assert.Equal(PhotoVersionRecipeStatus.Success, document!.TryGetRecipe(secondId, out var recipe));
        Assert.Equal("main packet", recipe);
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            document.TryRenameCopy(secondId, "Alternate", document.Revision, out var renamed));
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryRead(renamed!, out document));
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            document!.TryRemoveCopy(firstId, document.Revision, out var removed));
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryRead(removed!, out document));
        Assert.Equal(PhotoVersionRecipeStatus.VersionMissing,
            document!.TryRemoveCopy(mainId, document.Revision, out _));
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            document.TryRemoveCopy(secondId, document.Revision, out removed));
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryRead(removed!, out document));
        Assert.Equal(sourceId, document!.SourceId);
        Assert.Equal(PhotoVersionRecipeStatus.Success, document.TryGetRecipe(mainId, out recipe));
        Assert.Equal("main packet", recipe);
        var root = JsonNode.Parse(removed!)!;
        Assert.True((bool)root["recipeOnly"]!);
        Assert.Empty(root["copies"]!.AsArray());
    }

    [Fact]
    public void UpdatingOneRecipePreservesOtherRecordsAndUnknownFields()
    {
        var sourceId = Guid.NewGuid();
        var revision = Guid.NewGuid();
        var mainId = Guid.NewGuid();
        var copyId = Guid.NewGuid();
        var json = Document(sourceId, revision, mainId, copyId);
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryRead(json, out var document));

        Assert.Equal(PhotoVersionRecipeStatus.Success,
            document!.TryUpdateRecipe(copyId, revision, "updated packet", out var updated));
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryRead(updated!, out var reread));
        Assert.Equal(sourceId, reread!.SourceId);
        Assert.NotEqual(revision, reread.Revision);
        Assert.Equal(PhotoVersionRecipeStatus.Success, reread.TryGetRecipe(mainId, out var main));
        Assert.Equal("main packet", main);
        Assert.Equal(PhotoVersionRecipeStatus.Success, reread.TryGetRecipe(copyId, out var copy));
        Assert.Equal("updated packet", copy);
        var parsed = JsonNode.Parse(updated!)!;
        Assert.Equal("keep", (string?)parsed["future"]!["nested"]);
        Assert.Equal("copy-extra", (string?)parsed["copies"]![0]!["future"]);
        Assert.Equal("main-extra", (string?)parsed["main"]!["future"]);
        Assert.Equal(PhotoVersionRecipeStatus.Success, document.TryGetRecipe(copyId, out var original));
        Assert.Equal("copy packet", original);
    }

    [Fact]
    public void StaleAndMissingVersionsCannotProduceAnUpdatedDocument()
    {
        var revision = Guid.NewGuid();
        var copyId = Guid.NewGuid();
        var json = Document(Guid.NewGuid(), revision, Guid.NewGuid(), copyId);
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryRead(json, out var document));
        Assert.Equal(PhotoVersionRecipeStatus.Conflict,
            document!.TryUpdateRecipe(copyId, Guid.NewGuid(), "new", out var stale));
        Assert.Null(stale);
        Assert.Equal(PhotoVersionRecipeStatus.VersionMissing,
            document.TryUpdateRecipe(Guid.NewGuid(), revision, "new", out var missing));
        Assert.Null(missing);
        Assert.Equal(PhotoVersionRecipeStatus.InvalidMetadata,
            document.TryUpdateRecipe(copyId, revision, "", out var empty));
        Assert.Null(empty);
    }

    [Fact]
    public void PhysicalCopyRekeysEveryVersionAndPreservesRecipesAndOpaqueData()
    {
        var sourceId = Guid.NewGuid();
        var revision = Guid.NewGuid();
        var mainId = Guid.NewGuid();
        var copyId = Guid.NewGuid();
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            PhotoVersionDocument.TryRead(Document(sourceId, revision, mainId, copyId), out var document));
        Assert.Equal(PhotoVersionRecipeStatus.Conflict,
            document!.TryRekey(Guid.NewGuid(), out var stale, out var staleIds));
        Assert.Null(stale);
        Assert.Null(staleIds);
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            document.TryRekey(revision, out var rekeyed, out var ids));
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryRead(rekeyed!, out var copy));
        Assert.NotEqual(sourceId, copy!.SourceId);
        Assert.NotEqual(revision, copy.Revision);
        Assert.Equal(document.PixelBase, copy.PixelBase);
        Assert.Equal(2, ids!.Count);
        Assert.DoesNotContain(ids.Values, id => id == mainId || id == copyId);
        Assert.Equal(ids[mainId], copy.MainId);
        Assert.Equal(new[] { ids[mainId], ids[copyId] }, copy.GetRecords().Select(record => record.Id));
        Assert.Equal("main packet", copy.GetRecords()[0].RecipeXmp);
        Assert.Equal("copy packet", copy.GetRecords()[1].RecipeXmp);
        var json = JsonNode.Parse(rekeyed!)!;
        Assert.Equal("keep", (string?)json["future"]!["nested"]);
        Assert.Equal("main-extra", (string?)json["main"]!["future"]);
        Assert.Equal("copy-extra", (string?)json["copies"]![0]!["future"]);
        Assert.Equal(sourceId, document.SourceId);
    }

    [Fact]
    public void UnsupportedOrAmbiguousDocumentsAreNotWritable()
    {
        var id = Guid.NewGuid();
        var json = Document(Guid.NewGuid(), Guid.NewGuid(), id, Guid.NewGuid());
        var root = JsonNode.Parse(json)!.AsObject();
        root["schemaVersion"] = 2;
        Assert.Equal(PhotoVersionRecipeStatus.Unsupported,
            PhotoVersionDocument.TryRead(root.ToJsonString(), out var unsupported));
        Assert.Null(unsupported);

        root["schemaVersion"] = 1;
        root["copies"]![0]!["recipeSchemaVersion"] = 2;
        Assert.Equal(PhotoVersionRecipeStatus.Unsupported,
            PhotoVersionDocument.TryRead(root.ToJsonString(), out _));
        root["copies"]![0]!["recipeSchemaVersion"] = 1;
        root["copies"]![0]!["id"] = id.ToString("D");
        Assert.Equal(PhotoVersionRecipeStatus.InvalidMetadata,
            PhotoVersionDocument.TryRead(root.ToJsonString(), out var duplicateId));
        Assert.Null(duplicateId);
        Assert.Equal(PhotoVersionRecipeStatus.InvalidMetadata,
            PhotoVersionDocument.TryRead("""{"schemaVersion":1,"schemaVersion":1}""", out _));
        Assert.Equal(PhotoVersionRecipeStatus.InvalidMetadata,
            PhotoVersionDocument.TryRead("{broken", out _));
    }

    private static string Document(Guid source, Guid revision, Guid main, Guid copy) =>
        new JsonObject
        {
            ["schemaVersion"] = 1,
            ["sourceId"] = source.ToString("D"),
            ["revision"] = revision.ToString("D"),
            ["pixelBase"] = "pixels-v1:generated",
            ["recipeOnly"] = true,
            ["main"] = new JsonObject
            {
                ["id"] = main.ToString("D"),
                ["name"] = "Original",
                ["recipeSchemaVersion"] = 1,
                ["recipeXmp"] = "main packet",
                ["future"] = "main-extra"
            },
            ["copies"] = new JsonArray(new JsonObject
            {
                ["id"] = copy.ToString("D"),
                ["name"] = "Copy 1",
                ["recipeSchemaVersion"] = 1,
                ["recipeXmp"] = "copy packet",
                ["future"] = "copy-extra"
            }),
            ["future"] = new JsonObject { ["nested"] = "keep" }
        }.ToJsonString();
}
