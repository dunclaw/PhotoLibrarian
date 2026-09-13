using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.Core.Data;

public interface IFaceScanStore
{
    Task<List<ImageEntry>> GetImagesNeedingFaceScanAsync(
        string scanVersion,
        CancellationToken cancellationToken = default);

    Task<List<FaceRegion>> GetFacesForImageAsync(long imageId) =>
        Task.FromResult<List<FaceRegion>>([]);

    Task<bool> TryReplaceFaceRegionsAsync(
        long imageId,
        long expectedFileSize,
        DateTime expectedDateModified,
        IReadOnlyCollection<FaceRegion> faces,
        string scanVersion,
        CancellationToken cancellationToken = default);
}
