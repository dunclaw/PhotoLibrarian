using PhotoLibrarian.ML.Services;
using PhotoLibrarian.ModelBench;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class GeneralPhotoVocabularyTests
{
    [Fact]
    public void HierarchyCoversEveryModelOutputIncludingExcludedLabels()
    {
        var vocabulary = GeneralPhotoVocabulary.Current;
        Assert.Equal(4585, vocabulary.RamLabels.Length);
        Assert.Equal(3632, vocabulary.Labels.Length);
        Assert.Equal(3661, vocabulary.AllowedRamLabels.Count);
        foreach (var label in vocabulary.RamLabels.Concat(vocabulary.Labels))
        {
            var parts = AutoTagHierarchy.GetPath(label).Split('/');
            for (var index = 0; index < parts.Length; index++)
                Assert.Equal(string.Join('/', parts.Take(index + 1)), AutoTagHierarchy.GetPath(parts[index]));
        }
        Assert.All(vocabulary.AllowedRamLabels.Values, label => Assert.Contains(label, vocabulary.Labels));
    }

    [Theory]
    [InlineData("sax")]
    [InlineData("snowmobile")]
    [InlineData("excavator")]
    [InlineData("wheelchair")]
    [InlineData("boat")]
    public void GeneralSubjectsAreAvailableWithoutSampleSetEvidence(string label)
    {
        Assert.Contains(label, GeneralPhotoVocabulary.Current.Labels);
        Assert.Contains(label, AutoTagModelCatalog.RamPlus.SafeLabelMappings.Values);
        Assert.Contains(label, AutoTagModelCatalog.TinyClip.SafeLabelMappings.Keys);
    }

    [Theory]
    [InlineData("beak")]
    [InlineData("claw")]
    [InlineData("paw")]
    [InlineData("tail")]
    [InlineData("3d glasses")]
    [InlineData("abacus")]
    [InlineData("eiffel tower")]
    public void ExclusionDoesNotEraseTheFullTaxonomy(string label)
    {
        Assert.NotEmpty(AutoTagHierarchy.GetPath(label));
        Assert.Contains(label, GeneralPhotoVocabulary.Current.ExcludedLabels.Keys);
        Assert.DoesNotContain(label, AutoTagModelCatalog.RamPlus.SafeLabelMappings.Keys);
        Assert.DoesNotContain(label, AutoTagModelCatalog.TinyClip.SafeLabelMappings.Keys);
    }

    [Fact]
    public void PreviousNarrowProfileApprovalDoesNotAuthorizeExpandedPredictions()
    {
        var settings = new AutoTaggingSettings(true, AutoTagModelCatalog.TinyClipProfileId,
            ProfileQualityStatuses: new()
            {
                ["tinyclip.vit-40m-32.int8|tinyclip-40m-int8-cpu-v3-balanced-top10-20260916-hierarchy-v1"] =
                    AutoTagQualityStatus.Approved
            });
        Assert.False(settings.CanRun);
    }

    [Theory]
    [InlineData("newfoundland", "newfoundland dog")]
    [InlineData("labrador", "labrador retriever")]
    [InlineData("chihuahua", "chihuahua dog")]
    [InlineData("autumn leave", "autumn leaves")]
    public void ExplicitNamesReplaceGeographicAmbiguityAndSpellingErrors(string original, string corrected)
    {
        Assert.DoesNotContain(original, GeneralPhotoVocabulary.Current.Labels);
        Assert.Contains(corrected, GeneralPhotoVocabulary.Current.Labels);
        Assert.Equal(corrected, GeneralPhotoVocabulary.Current.AllowedRamLabels[original]);
        Assert.EndsWith("/" + corrected, AutoTagHierarchy.GetPath(original));
    }

    [Fact(Explicit = true)]
    public void AllRealRamLabelIndexesAgreeAcrossApplicationAndBenchmarkParsers()
    {
        var path = Environment.GetEnvironmentVariable("RAM_LABELS")
            ?? throw new InvalidOperationException("Set RAM_LABELS to the full pinned RAM++ label dictionary.");
        var applicationLabels = AutoTaggingService.ReadLabels(path);
        var benchmarkLabels = LabelResolver.Parse(File.ReadAllText(path));
        var expected = GeneralPhotoVocabulary.Current.RamLabels;
        var mappings = expected.ToDictionary(label => label, label => label, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(4585, applicationLabels.Length);
        Assert.Equal(4585, benchmarkLabels.Count);
        foreach (var labels in new IReadOnlyList<string>[] { applicationLabels, benchmarkLabels })
        {
            var predictions = AutoTaggingService.SelectPredictions(
                Enumerable.Repeat(1f, 4585).ToArray(), labels, 0.4f, 4585, mappings,
                AutoTagOutputKind.DirectMultiLabel);
            Assert.Equal(expected, predictions.Select(prediction => prediction.Tag));
        }
    }
}
