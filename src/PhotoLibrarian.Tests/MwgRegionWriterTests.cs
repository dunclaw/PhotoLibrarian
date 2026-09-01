using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using System.Globalization;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class MwgRegionWriterTests
{
    [Fact]
    public async Task WriteFaceRegions_UsesInvariantNormalizedCoordinates()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"PhotoLibrarian-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var imagePath = Path.Combine(directory, "portrait.jpg");
        var originalCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            await MwgRegionWriter.WriteFaceRegionsAsync(
                imagePath,
                [
                    new FaceRegion
                    {
                        X = 0.1,
                        Y = 0.2,
                        Width = 0.3,
                        Height = 0.4,
                        PersonName = "Alex"
                    }
                ],
                1200,
                800);

            var xmp = await File.ReadAllTextAsync(
                Path.ChangeExtension(imagePath, ".xmp"),
                TestContext.Current.CancellationToken);
            Assert.Contains("Alex", xmp);
            Assert.Contains("0.250000", xmp);
            Assert.Contains("0.400000", xmp);
            Assert.DoesNotContain("0,250000", xmp);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            var sidecarPath = Path.ChangeExtension(imagePath, ".xmp");
            if (File.Exists(sidecarPath))
            {
                File.Delete(sidecarPath);
            }

            Directory.Delete(directory);
        }
    }
}
