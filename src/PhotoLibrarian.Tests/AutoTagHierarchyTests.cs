using PhotoLibrarian.ML.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class AutoTagHierarchyTests
{
    [Theory]
    [InlineData(AutoTagModelCatalog.RamPlusProfileId)]
    [InlineData(AutoTagModelCatalog.TinyClipProfileId)]
    public void EveryAllowedOutputAndAncestorHasAConsistentPath(string profileId)
    {
        var profile = AutoTagModelCatalog.ForId(profileId);
        Assert.True(profile.UsesTagHierarchy);
        foreach (var label in profile.SafeLabelMappings.Values)
        {
            var path = AutoTagHierarchy.GetPath(label);
            Assert.DoesNotContain("Auto/", path);
            var segments = path.Split('/');
            for (var index = 0; index < segments.Length; index++)
                Assert.Equal(string.Join('/', segments.Take(index + 1)),
                    AutoTagHierarchy.GetPath(segments[index]));
        }
    }

    [Theory]
    [InlineData("animal", "animal")]
    [InlineData("bird", "animal/bird")]
    [InlineData("eagle", "animal/bird/eagle")]
    [InlineData("bald eagle", "animal/bird/eagle/bald eagle")]
    public void BroadAndSpecificBirdLabelsUseTheSameHierarchy(string label, string expected) =>
        Assert.Equal(expected, AutoTagHierarchy.GetPath(label));

    [Fact]
    public void MappingPreservesScoresAndDeduplicatesSynonyms()
    {
        var mapped = AutoTagHierarchy.Apply(AutoTagModelCatalog.RamPlus,
            [new("bunny", 0.6f), new("bird", 0.7f), new("rabbit", 0.9f)]);
        Assert.Equal(2, mapped.Count);
        Assert.Equal(new TagPrediction(AutoTagHierarchy.GetPath("rabbit"), 0.9f), mapped[0]);
        Assert.Equal(new TagPrediction("animal/bird", 0.7f), mapped[1]);
        Assert.Equal(AutoTagHierarchy.GetPath("rabbit"), AutoTagHierarchy.GetPath("bunny"));
    }

    [Fact]
    public void RamHierarchyRunsAfterThresholdingSafetyAndCandidateLimit()
    {
        var profile = AutoTagModelCatalog.RamPlus;
        var raw = AutoTaggingService.SelectPredictions(
            [1f, 1f, 1f, 0f], ["beak", "bald eagle", "eagle", "bird"],
            0.8f, 1, profile.SafeLabelMappings, profile.OutputKind);
        var mapped = Assert.Single(AutoTagHierarchy.Apply(profile, raw));
        Assert.Equal(new TagPrediction("animal/bird/eagle/bald eagle", 1f), mapped);

        var broad = AutoTagHierarchy.Apply(profile, [new TagPrediction("bird", 0.8f)]);
        Assert.Equal(new TagPrediction("animal/bird", 0.8f), Assert.Single(broad));
    }

    [Theory]
    [InlineData(AutoTagModelCatalog.RamPlusProfileId)]
    [InlineData(AutoTagModelCatalog.TinyClipProfileId)]
    public void HierarchyRequiresNewApprovalAndScanVersion(string profileId)
    {
        var profile = AutoTagModelCatalog.ForId(profileId);
        var suffix = "-" + AutoTagHierarchy.Version;
        Assert.EndsWith(suffix, profile.PipelineVersion);
        var oldVersion = profile.PipelineVersion[..^suffix.Length];
        var settings = new AutoTaggingSettings(true, profile.Id,
            ProfileQualityStatuses: new()
            {
                [$"{profile.Id}|{oldVersion}"] = AutoTagQualityStatus.Approved
            });
        Assert.False(settings.CanRun);
        settings.ProfileQualityStatuses![profile.ApprovalKey] = AutoTagQualityStatus.Approved;
        Assert.True(settings.CanRun);
    }

    [Fact]
    public void LegacyProfilesRetainFlatPredictions()
    {
        TagPrediction[] predictions = [new("dog", 0.9f)];
        Assert.Same(predictions, AutoTagHierarchy.Apply(AutoTagModelCatalog.MobileNetV2, predictions));
        Assert.Same(predictions, AutoTagHierarchy.Apply(AutoTagModelCatalog.EfficientNetLite4, predictions));
    }

    [Fact]
    public void MissingOrInconsistentMappingsFailExplicitly()
    {
        Assert.Throws<InvalidDataException>(() => AutoTagHierarchy.GetPath("unreviewed label"));
        Assert.Throws<InvalidDataException>(() => AutoTagHierarchy.Validate(
            new Dictionary<string, string>
            {
                ["bird"] = "animal/bird",
                ["animal"] = "nature/animal",
                ["nature"] = "nature"
            }));
    }

    [Theory]
    [InlineData("animal//bird")]
    [InlineData("animal/bird/animal")]
    [InlineData("animal/ bird")]
    [InlineData("auto/animal/bird")]
    [InlineData("animal\\bird")]
    public void MalformedHierarchyPathsAreRejected(string path) =>
        Assert.Throws<InvalidDataException>(() => AutoTagHierarchy.Validate(
            new Dictionary<string, string> { ["bird"] = path }));
}
