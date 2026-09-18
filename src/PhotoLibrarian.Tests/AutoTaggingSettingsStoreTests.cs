using PhotoLibrarian.ML.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class AutoTaggingSettingsStoreTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsProfileSettings()
    {
        var settingsPath = CreateSettingsPath();
        try
        {
            var store = new AutoTaggingSettingsStore(settingsPath);
            store.Save(new AutoTaggingSettings(
                true,
                AutoTagModelCatalog.EfficientNetLite4ProfileId,
                2,
                0.42f,
                @"C:\Models",
                new Dictionary<string, AutoTagQualityStatus>
                {
                    [AutoTagModelCatalog.EfficientNetLite4.ApprovalKey] =
                        AutoTagQualityStatus.Approved
                }));

            var loaded = store.Load();

            Assert.True(loaded.IsEnabled);
            Assert.True(loaded.CanRun);
            Assert.Equal(
                AutoTagModelCatalog.EfficientNetLite4ProfileId,
                loaded.ProfileId);
            Assert.Equal(2, loaded.MaximumTags);
            Assert.Equal(0.42f, loaded.ConfidenceThreshold);
            Assert.Equal(
                AutoTagQualityStatus.Approved,
                loaded.GetQualityStatus(loaded.ProfileId));
        }
        finally
        {
            DeleteSettingsFiles(settingsPath);
        }
    }

    [Fact]
    public void TinyClipCutoffIsBoundedToTheLabeledSliderRange()
    {
        var settingsPath = CreateSettingsPath();
        try
        {
            var store = new AutoTaggingSettingsStore(settingsPath);
            store.Save(new AutoTaggingSettings(
                false, AutoTagModelCatalog.TinyClipProfileId,
                ConfidenceThreshold: 0.01f));
            Assert.Equal(AutoTagModelCatalog.TinyClipOpenThreshold,
                store.Load().ConfidenceThreshold);
            store.Save(new AutoTaggingSettings(
                false, AutoTagModelCatalog.TinyClipProfileId,
                ConfidenceThreshold: 0.99f));
            Assert.Equal(AutoTagModelCatalog.TinyClipConservativeThreshold,
                store.Load().ConfidenceThreshold);
        }
        finally
        {
            DeleteSettingsFiles(settingsPath);
        }
    }

    [Theory]
    [InlineData(0, 34)]
    [InlineData(100, 27)]
    public void TinyClipOpennessMapsToLabeledConfidenceBounds(
        double openness,
        double expectedConfidence)
    {
        Assert.Equal(
            expectedConfidence,
            AutoTagModelCatalog.TinyClipOpennessToConfidence(openness),
            precision: 5);
    }

    [Fact]
    public void TinyClipDefaultConfidenceRoundTripsThroughOpenness()
    {
        var confidence =
            AutoTagModelCatalog.TinyClip.DefaultConfidenceThreshold * 100;

        var openness =
            AutoTagModelCatalog.TinyClipConfidenceToOpenness(confidence);
        var roundTripped =
            AutoTagModelCatalog.TinyClipOpennessToConfidence(openness);

        Assert.Equal(confidence, roundTripped, precision: 5);
    }

    [Fact]
    public async Task Load_ApprovalForOldPipelineVersionIsIgnored()
    {
        var settingsPath = CreateSettingsPath();
        try
        {
            var oldSettings = new AutoTaggingSettings(
                true,
                AutoTagModelCatalog.MobileNetV2ProfileId,
                ProfileQualityStatuses:
                    new Dictionary<string, AutoTagQualityStatus>
                    {
                        [$"{AutoTagModelCatalog.MobileNetV2ProfileId}|old-version"] =
                            AutoTagQualityStatus.Approved
                    });
            await File.WriteAllTextAsync(
                settingsPath,
                System.Text.Json.JsonSerializer.Serialize(oldSettings),
                TestContext.Current.CancellationToken);

            var loaded =
                new AutoTaggingSettingsStore(settingsPath).Load();

            Assert.False(loaded.CanRun);
            Assert.Equal(
                AutoTagQualityStatus.Untested,
                loaded.GetQualityStatus(loaded.ProfileId));
        }
        finally
        {
            DeleteSettingsFiles(settingsPath);
        }
    }

    [Fact]
    public void Load_MissingFileDefaultsToDisabledAndUntested()
    {
        var settingsPath = CreateSettingsPath();
        var settings =
            new AutoTaggingSettingsStore(settingsPath).Load();

        Assert.False(settings.IsEnabled);
        Assert.False(settings.CanRun);
        Assert.Equal(
            AutoTagModelCatalog.RamPlusProfileId,
            settings.ProfileId);
        Assert.Equal(8, settings.MaximumTags);
        Assert.Equal(0.40f, settings.ConfidenceThreshold);
        Assert.Equal(
            AutoTagQualityStatus.Untested,
            settings.GetQualityStatus(settings.ProfileId));
    }

    [Theory]
    [InlineData(0, AutoTagModelCatalog.MobileNetV2ProfileId)]
    [InlineData(1, AutoTagModelCatalog.EfficientNetLite4ProfileId)]
    public async Task Load_LegacySettingsMigratesButRequiresApproval(
        int mode,
        string expectedProfileId)
    {
        var settingsPath = CreateSettingsPath();
        try
        {
            await File.WriteAllTextAsync(
                settingsPath,
                $$"""{"IsEnabled":true,"Mode":{{mode}}}""",
                TestContext.Current.CancellationToken);

            var settings =
                new AutoTaggingSettingsStore(settingsPath).Load();

            Assert.True(settings.IsEnabled);
            Assert.Equal(expectedProfileId, settings.ProfileId);
            Assert.False(settings.CanRun);
            Assert.Equal(
                AutoTagQualityStatus.Untested,
                settings.GetQualityStatus(expectedProfileId));
        }
        finally
        {
            DeleteSettingsFiles(settingsPath);
        }
    }

    [Fact]
    public async Task Load_InvalidJsonFallsBackToSafeDefaults()
    {
        var settingsPath = CreateSettingsPath();
        try
        {
            await File.WriteAllTextAsync(
                settingsPath,
                "not json",
                TestContext.Current.CancellationToken);

            var settings =
                new AutoTaggingSettingsStore(settingsPath).Load();

            Assert.False(settings.IsEnabled);
            Assert.False(settings.CanRun);
        }
        finally
        {
            DeleteSettingsFiles(settingsPath);
        }
    }

    [Fact]
    public async Task Load_UnknownProfileFallsBackToSafeDefaults()
    {
        var settingsPath = CreateSettingsPath();
        try
        {
            await File.WriteAllTextAsync(
                settingsPath,
                """{"IsEnabled":true,"ProfileId":"unknown"}""",
                TestContext.Current.CancellationToken);

            var settings =
                new AutoTaggingSettingsStore(settingsPath).Load();

            Assert.False(settings.IsEnabled);
            Assert.False(settings.CanRun);
            Assert.Equal(
                AutoTagModelCatalog.RamPlusProfileId,
                settings.ProfileId);
        }
        finally
        {
            DeleteSettingsFiles(settingsPath);
        }
    }

    private static string CreateSettingsPath() => Path.Combine(
        Path.GetTempPath(),
        $"PhotoLibrarian-{Guid.NewGuid():N}.json");

    private static void DeleteSettingsFiles(string settingsPath)
    {
        File.Delete(settingsPath);
        File.Delete(settingsPath + ".tmp");
    }
}
