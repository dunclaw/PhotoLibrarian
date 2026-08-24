using PhotoLibrarian.Core.Models;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class ImageRefinementFilterTests
{
    [Fact]
    public void Matches_CombinesFacetsWithAnd()
    {
        var image = CreateImage();
        image.Rating = 4;
        image.DateTaken = new DateTime(2025, 6, 15);
        image.IsFlagged = true;
        var filter = new ImageRefinementFilter
        {
            Rating = 3,
            RatingMode = RatingFilterMode.AndHigher,
            DateFrom = new DateTime(2025, 1, 1),
            DateTo = new DateTime(2025, 12, 31),
            IncludedTags = ["travel", "family"],
            ExcludedTags = ["reject"],
            PersonId = 42,
            Flag = FlagFilterMode.Flagged,
            MediaKind = MediaKindFilter.Photos,
            Extensions = ["jpg"]
        };

        Assert.True(filter.Matches(image, ["Travel", "family"], [42]));
        Assert.False(filter.Matches(image, ["travel"], [42]));
        Assert.False(filter.Matches(image, ["travel", "family", "reject"], [42]));
        Assert.False(filter.Matches(image, ["travel", "family"], [7]));
    }

    [Theory]
    [InlineData(RatingFilterMode.Exact, 3, true)]
    [InlineData(RatingFilterMode.Exact, 4, false)]
    [InlineData(RatingFilterMode.AndHigher, 2, true)]
    [InlineData(RatingFilterMode.AndHigher, 4, false)]
    [InlineData(RatingFilterMode.AndLower, 4, true)]
    [InlineData(RatingFilterMode.AndLower, 2, false)]
    public void Matches_AppliesRatingModes(
        RatingFilterMode mode,
        int threshold,
        bool expected)
    {
        var filter = new ImageRefinementFilter
        {
            Rating = threshold,
            RatingMode = mode
        };

        var image = CreateImage();
        image.Rating = 3;

        Assert.Equal(expected, filter.Matches(image));
    }

    [Fact]
    public void Matches_AppliesEveryMissingMetadataRequirement()
    {
        var filter = new ImageRefinementFilter
        {
            MissingMetadata =
                MissingMetadataFilter.Tags |
                MissingMetadataFilter.Rating |
                MissingMetadataFilter.CaptureDate |
                MissingMetadataFilter.Geotag
        };

        Assert.True(filter.Matches(CreateImage(), []));
        Assert.False(filter.Matches(CreateImage(), ["tagged"]));
        var rated = CreateImage();
        rated.Rating = 1;
        Assert.False(filter.Matches(rated, []));

        var dated = CreateImage();
        dated.DateTaken = DateTime.Today;
        Assert.False(filter.Matches(dated, []));

        var geotagged = CreateImage();
        geotagged.GpsLatitude = 1;
        geotagged.GpsLongitude = 2;
        Assert.False(filter.Matches(geotagged, []));
    }

    [Fact]
    public void Matches_UsesInclusiveCaptureDatesAndNormalizedExtensions()
    {
        var filter = new ImageRefinementFilter
        {
            DateFrom = new DateTime(2025, 3, 1),
            DateTo = new DateTime(2025, 3, 31),
            Extensions = [".JPG"]
        };

        var endOfRange = CreateImage();
        endOfRange.DateTaken = new DateTime(2025, 3, 31, 23, 59, 59);
        Assert.True(filter.Matches(endOfRange));

        var afterRange = CreateImage();
        afterRange.DateTaken = new DateTime(2025, 4, 1);
        Assert.False(filter.Matches(afterRange));
    }

    private static ImageEntry CreateImage() => new()
    {
        FilePath = @"C:\Photos\image.jpg",
        FileName = "image.jpg",
        DateModified = DateTime.UtcNow,
        DateIndexed = DateTime.UtcNow,
        MediaType = MediaType.Image
    };
}
