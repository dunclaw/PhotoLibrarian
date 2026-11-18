// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Text.Json;
using System.Text.Json.Nodes;
using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.Core.Services;

internal sealed record PhotoVersionRecord(Guid Id, string Name, string RecipeXmp, bool IsMain);

/// <summary>
/// Parses and updates the portable family envelope without discarding unknown JSON fields.
/// File ownership, recipe validation and publication belong to the metadata store.
/// </summary>
internal sealed class PhotoVersionDocument
{
    private readonly JsonObject _document;

    private PhotoVersionDocument(JsonObject document, Guid sourceId, Guid revision, string pixelBase, Guid mainId)
    {
        _document = document;
        SourceId = sourceId;
        Revision = revision;
        PixelBase = pixelBase;
        MainId = mainId;
    }

    internal Guid SourceId { get; }
    internal Guid Revision { get; }
    internal string PixelBase { get; }
    internal Guid MainId { get; }

    internal IReadOnlyList<PhotoVersionRecord> GetRecords()
    {
        var records = new List<PhotoVersionRecord>();
        AddRecord(records, (JsonObject)_document["main"]!, isMain: true);
        foreach (var item in (JsonArray)_document["copies"]!)
            AddRecord(records, (JsonObject)item!, isMain: false);
        return records;
    }

    private static void AddRecord(List<PhotoVersionRecord> records, JsonObject record, bool isMain)
    {
        _ = TryGuid(record, "id", out var id);
        _ = TryString(record, "name", out var name);
        _ = TryString(record, "recipeXmp", out var recipe);
        records.Add(new PhotoVersionRecord(id, name!, recipe!, isMain));
    }

    internal static PhotoVersionRecipeStatus TryCreate(
        Guid sourceId, string pixelBase, Guid mainId, string mainName, string mainRecipeXmp,
        Guid copyId, string copyName, out string? json)
    {
        json = null;
        if (sourceId == Guid.Empty || mainId == Guid.Empty || copyId == Guid.Empty ||
            mainId == copyId || string.IsNullOrWhiteSpace(pixelBase) ||
            string.IsNullOrWhiteSpace(mainName) || string.IsNullOrWhiteSpace(copyName) ||
            string.IsNullOrWhiteSpace(mainRecipeXmp))
            return PhotoVersionRecipeStatus.InvalidMetadata;

        var root = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["sourceId"] = sourceId.ToString("D"),
            ["revision"] = Guid.NewGuid().ToString("D"),
            ["pixelBase"] = pixelBase,
            ["recipeOnly"] = true,
            ["main"] = NewRecord(mainId, mainName, mainRecipeXmp),
            ["copies"] = new JsonArray(NewRecord(copyId, copyName, mainRecipeXmp))
        };
        json = root.ToJsonString();
        return PhotoVersionRecipeStatus.Success;
    }

    internal static PhotoVersionRecipeStatus TryRead(
        string json, out PhotoVersionDocument? document)
    {
        document = null;
        JsonNode? node;
        try
        {
            using var parsed = JsonDocument.Parse(json);
            if (HasDuplicateProperties(parsed.RootElement))
                return PhotoVersionRecipeStatus.InvalidMetadata;
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return PhotoVersionRecipeStatus.InvalidMetadata;
        }

        if (node is not JsonObject root ||
            !TryInt(root, "schemaVersion", out var schemaVersion))
            return PhotoVersionRecipeStatus.InvalidMetadata;
        if (schemaVersion != 1)
            return PhotoVersionRecipeStatus.Unsupported;
        if (!TryGuid(root, "sourceId", out var sourceId) ||
            !TryGuid(root, "revision", out var revision) ||
            !TryString(root, "pixelBase", out var pixelBase) ||
            string.IsNullOrWhiteSpace(pixelBase) ||
            !TryBool(root, "recipeOnly", out var recipeOnly) || !recipeOnly ||
            root["main"] is not JsonObject main ||
            root["copies"] is not JsonArray copies)
            return PhotoVersionRecipeStatus.InvalidMetadata;

        var ids = new HashSet<Guid>();
        var status = ValidateRecord(main, ids);
        if (status != PhotoVersionRecipeStatus.Success)
            return status;
        foreach (var copy in copies)
        {
            if (copy is not JsonObject record)
                return PhotoVersionRecipeStatus.InvalidMetadata;
            status = ValidateRecord(record, ids);
            if (status != PhotoVersionRecipeStatus.Success)
                return status;
        }

        _ = TryGuid(main, "id", out var mainId);
        document = new PhotoVersionDocument(root, sourceId, revision, pixelBase!, mainId);
        return PhotoVersionRecipeStatus.Success;
    }

    internal PhotoVersionRecipeStatus TryGetRecipe(Guid variantId, out string? recipeXmp)
    {
        recipeXmp = null;
        var record = FindRecord(variantId);
        if (record is null)
            return PhotoVersionRecipeStatus.VersionMissing;
        _ = TryString(record, "recipeXmp", out recipeXmp);
        return PhotoVersionRecipeStatus.Success;
    }

    internal PhotoVersionRecipeStatus TryUpdateRecipe(
        Guid variantId, Guid expectedRevision, string recipeXmp, out string? updatedJson)
    {
        updatedJson = null;
        if (expectedRevision != Revision)
            return PhotoVersionRecipeStatus.Conflict;
        if (string.IsNullOrWhiteSpace(recipeXmp))
            return PhotoVersionRecipeStatus.InvalidMetadata;
        if (FindRecord(variantId) is null)
            return PhotoVersionRecipeStatus.VersionMissing;

        var updated = (JsonObject)_document.DeepClone();
        var updatedDocument = new PhotoVersionDocument(updated, SourceId, Revision, PixelBase, MainId);
        updatedDocument.FindRecord(variantId)!["recipeXmp"] = recipeXmp;
        updatedJson = FinishUpdate(updated);
        return PhotoVersionRecipeStatus.Success;
    }

    internal PhotoVersionRecipeStatus TryAddCopy(
        Guid sourceVariantId, Guid newVariantId, string name, Guid expectedRevision, out string? updatedJson)
    {
        updatedJson = null;
        if (expectedRevision != Revision)
            return PhotoVersionRecipeStatus.Conflict;
        var source = FindRecord(sourceVariantId);
        if (source is null)
            return PhotoVersionRecipeStatus.VersionMissing;
        if (newVariantId == Guid.Empty || FindRecord(newVariantId) is not null ||
            string.IsNullOrWhiteSpace(name))
            return PhotoVersionRecipeStatus.InvalidMetadata;

        var updated = (JsonObject)_document.DeepClone();
        var copy = (JsonObject)source.DeepClone();
        copy["id"] = newVariantId.ToString("D");
        copy["name"] = name;
        ((JsonArray)updated["copies"]!).Add(copy);
        updatedJson = FinishUpdate(updated);
        return PhotoVersionRecipeStatus.Success;
    }

    internal PhotoVersionRecipeStatus TryRenameCopy(
        Guid variantId, string name, Guid expectedRevision, out string? updatedJson)
    {
        updatedJson = null;
        if (expectedRevision != Revision)
            return PhotoVersionRecipeStatus.Conflict;
        if (string.IsNullOrWhiteSpace(name))
            return PhotoVersionRecipeStatus.InvalidMetadata;
        if (!IsCopy(variantId))
            return PhotoVersionRecipeStatus.VersionMissing;

        var updated = (JsonObject)_document.DeepClone();
        var updatedDocument = new PhotoVersionDocument(updated, SourceId, Revision, PixelBase, MainId);
        updatedDocument.FindRecord(variantId)!["name"] = name;
        updatedJson = FinishUpdate(updated);
        return PhotoVersionRecipeStatus.Success;
    }

    internal PhotoVersionRecipeStatus TryRemoveCopy(
        Guid variantId, Guid expectedRevision, out string? updatedJson)
    {
        updatedJson = null;
        if (expectedRevision != Revision)
            return PhotoVersionRecipeStatus.Conflict;
        if (!IsCopy(variantId))
            return PhotoVersionRecipeStatus.VersionMissing;

        var updated = (JsonObject)_document.DeepClone();
        var copies = (JsonArray)updated["copies"]!;
        for (var index = 0; index < copies.Count; index++)
        {
            if (TryGuid((JsonObject)copies[index]!, "id", out var id) && id == variantId)
            {
                copies.RemoveAt(index);
                break;
            }
        }
        updatedJson = FinishUpdate(updated);
        return PhotoVersionRecipeStatus.Success;
    }

    internal PhotoVersionRecipeStatus TryRekey(
        Guid expectedRevision, out string? updatedJson, out IReadOnlyDictionary<Guid, Guid>? variantIds)
    {
        updatedJson = null;
        variantIds = null;
        if (expectedRevision != Revision)
            return PhotoVersionRecipeStatus.Conflict;

        var updated = (JsonObject)_document.DeepClone();
        var ids = new Dictionary<Guid, Guid>();
        var records = new List<JsonObject> { (JsonObject)updated["main"]! };
        records.AddRange(((JsonArray)updated["copies"]!).Select(item => (JsonObject)item!));
        var usedIds = records.Select(record =>
        {
            _ = TryGuid(record, "id", out var id);
            return id;
        }).ToHashSet();
        usedIds.Add(SourceId);
        Guid NewId()
        {
            Guid id;
            do { id = Guid.NewGuid(); } while (id == Guid.Empty || !usedIds.Add(id));
            return id;
        }
        updated["sourceId"] = NewId().ToString("D");
        foreach (var record in records)
        {
            _ = TryGuid(record, "id", out var previousId);
            var newId = NewId();
            ids.Add(previousId, newId);
            record["id"] = newId.ToString("D");
        }
        updatedJson = FinishUpdate(updated);
        variantIds = ids;
        return PhotoVersionRecipeStatus.Success;
    }

    private bool IsCopy(Guid id)
    {
        foreach (var item in (JsonArray)_document["copies"]!)
            if (TryGuid((JsonObject)item!, "id", out var recordId) && id == recordId)
                return true;
        return false;
    }

    private static JsonObject NewRecord(Guid id, string name, string recipeXmp) => new()
    {
        ["id"] = id.ToString("D"),
        ["name"] = name,
        ["recipeSchemaVersion"] = 1,
        ["recipeXmp"] = recipeXmp
    };

    private static string FinishUpdate(JsonObject updated)
    {
        _ = TryGuid(updated, "revision", out var previous);
        Guid revision;
        do { revision = Guid.NewGuid(); } while (revision == Guid.Empty || revision == previous);
        updated["revision"] = revision.ToString("D");
        return updated.ToJsonString();
    }

    private JsonObject? FindRecord(Guid id)
    {
        var main = (JsonObject)_document["main"]!;
        if (TryGuid(main, "id", out var mainId) && id == mainId)
            return main;
        foreach (var item in (JsonArray)_document["copies"]!)
        {
            var record = (JsonObject)item!;
            if (TryGuid(record, "id", out var recordId) && id == recordId)
                return record;
        }
        return null;
    }

    private static PhotoVersionRecipeStatus ValidateRecord(JsonObject record, HashSet<Guid> ids)
    {
        if (!TryGuid(record, "id", out var id) || !ids.Add(id) ||
            !TryString(record, "name", out var name) || string.IsNullOrWhiteSpace(name) ||
            !TryString(record, "recipeXmp", out var recipe) || string.IsNullOrWhiteSpace(recipe) ||
            !TryInt(record, "recipeSchemaVersion", out var recipeVersion))
            return PhotoVersionRecipeStatus.InvalidMetadata;
        return recipeVersion == 1
            ? PhotoVersionRecipeStatus.Success
            : PhotoVersionRecipeStatus.Unsupported;
    }

    private static bool TryString(JsonObject value, string name, out string? result)
    {
        result = null;
        return value[name] is JsonValue item && item.TryGetValue(out result);
    }

    private static bool TryGuid(JsonObject value, string name, out Guid result)
    {
        result = Guid.Empty;
        return TryString(value, name, out var text) &&
            Guid.TryParseExact(text, "D", out result) && result != Guid.Empty;
    }

    private static bool TryInt(JsonObject value, string name, out int result)
    {
        result = 0;
        return value[name] is JsonValue item && item.TryGetValue(out result);
    }

    private static bool TryBool(JsonObject value, string name, out bool result)
    {
        result = false;
        return value[name] is JsonValue item && item.TryGetValue(out result);
    }

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value))
                    return true;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (HasDuplicateProperties(item))
                    return true;
        }
        return false;
    }
}
