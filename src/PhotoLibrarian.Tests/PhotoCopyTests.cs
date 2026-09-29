using PhotoLibrarian.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class PhotoCopyTests
{
    [Fact]
    public async Task MakeCopyForEditing_CreatesIndependentCopyAndPreservesSidecar()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var sourcePath = Path.Combine(directory, "portrait.cr3");
        var sourceSidecarPath = Path.ChangeExtension(sourcePath, ".xmp");
        var contents = new byte[] { 1, 2, 3, 4 };

        try
        {
            await File.WriteAllBytesAsync(sourcePath, contents);
            await File.WriteAllTextAsync(sourceSidecarPath, "face metadata");

            var copyPath = await PhotoOperationsService.MakeCopyForEditingAsync(sourcePath);

            Assert.Equal(Path.Combine(directory, "portrait - Copy.cr3"), copyPath);
            Assert.Equal(contents, await File.ReadAllBytesAsync(copyPath));
            Assert.Equal("face metadata", await File.ReadAllTextAsync(Path.ChangeExtension(copyPath, ".xmp")));

            await File.WriteAllBytesAsync(copyPath, new byte[] { 9 });
            Assert.Equal(contents, await File.ReadAllBytesAsync(sourcePath));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task MakeCopyForEditing_UsesNextAvailableNameWithoutOverwritingExistingCopy()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var sourcePath = Path.Combine(directory, "portrait.jpg");
        var firstCopyPath = Path.Combine(directory, "portrait - Copy.jpg");

        try
        {
            await File.WriteAllTextAsync(sourcePath, "original");
            await File.WriteAllTextAsync(firstCopyPath, "existing copy");

            var copyPath = await PhotoOperationsService.MakeCopyForEditingAsync(sourcePath);

            Assert.Equal(Path.Combine(directory, "portrait - Copy (2).jpg"), copyPath);
            Assert.Equal("existing copy", await File.ReadAllTextAsync(firstCopyPath));
            Assert.Equal("original", await File.ReadAllTextAsync(copyPath));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task MakeCopyForEditing_PreservesJpegSidecarNaming()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var sourcePath = Path.Combine(directory, "portrait.jpg");
        var sourceSidecarPath = $"{sourcePath}.xmp";

        try
        {
            await File.WriteAllTextAsync(sourcePath, "original");
            await File.WriteAllTextAsync(sourceSidecarPath, "face metadata");

            var copyPath = await PhotoOperationsService.MakeCopyForEditingAsync(sourcePath);

            Assert.Equal("face metadata", await File.ReadAllTextAsync($"{copyPath}.xmp"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
