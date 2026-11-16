// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class PhotoPresetStoreTests
{
    [Fact]
    public void CatalogRoundTripsPresetsAndFavorites()
    {
        using var folder = new TemporaryFolder();
        var store = new PhotoPresetStore(Path.Combine(folder.Path, "presets.json"));
        var preset = NewPreset(EditSettingGroup.Tone, new EditParameters { Exposure = 0.5 });

        Assert.Equal(PhotoPresetCatalogStatus.Missing, store.Load().Status);
        store.Save(preset);
        Assert.Equal(PhotoPresetCatalogStatus.Loaded, store.Load().Status);

        var favorite = store.SetFavorite(preset.Id, true);
        Assert.True(Assert.Single(favorite).IsFavorite);
        var reopened = new PhotoPresetStore(Path.Combine(folder.Path, "presets.json"));
        Assert.True(Assert.Single(reopened.Load().Presets).IsFavorite);
        Assert.Empty(store.Delete(preset.Id));
    }

    [Fact]
    public void UnsupportedCatalogVersionAndUnknownRecipeFieldsAreRejected()
    {
        using var folder = new TemporaryFolder();
        var path = Path.Combine(folder.Path, "presets.json");
        var store = new PhotoPresetStore(path);
        File.WriteAllText(path, """{"Version":2,"Presets":[]}""");
        Assert.Equal(PhotoPresetCatalogStatus.UnsupportedVersion, store.Load().Status);

        File.WriteAllText(path,
            """{"Version":1,"Presets":[{"Id":"00000000000000000000000000000001","Name":"x","Groups":"Tone","Parameters":{"Exposure":0.2,"UnknownFutureValue":1},"SourceIdentity":null,"IsFavorite":false}]}""");
        Assert.Equal(PhotoPresetCatalogStatus.Invalid, store.Load().Status);
        var unsupportedCatalog = File.ReadAllBytes(path);
        Assert.Throws<InvalidDataException>(() =>
            store.Save(NewPreset(EditSettingGroup.Tone, new EditParameters { Exposure = 0.4 })));
        Assert.Equal(unsupportedCatalog, File.ReadAllBytes(path));
    }

    [Fact]
    public void CatalogRejectsValuesOutsideTheSelectedGroupsAndRetouchWithoutIdentity()
    {
        using var folder = new TemporaryFolder();
        var store = new PhotoPresetStore(Path.Combine(folder.Path, "presets.json"));
        var invalidGroupData = NewPreset(EditSettingGroup.Tone, new EditParameters
        {
            Exposure = 0.2,
            Effect = PhotoEffect.Sepia
        });
        Assert.Throws<InvalidDataException>(() => store.Save(invalidGroupData));

        var retouch = NewPreset(EditSettingGroup.Retouch, new EditParameters
        {
            RetouchStrokes = [new RetouchStroke(32, 24, 4, [new RetouchPoint(10, 10)])]
        });
        Assert.Throws<InvalidDataException>(() => store.Save(retouch));
        Assert.Equal(PhotoPresetCatalogStatus.Missing, store.Load().Status);
    }

    private static PhotoPreset NewPreset(EditSettingGroup groups, EditParameters parameters) =>
        new(Guid.NewGuid().ToString("N"), "Test preset", groups, parameters, null, false);

    private sealed class TemporaryFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"photo-presets-{Guid.NewGuid():N}");

        public TemporaryFolder() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles(Path)) File.Delete(file);
            Directory.Delete(Path);
        }
    }
}
