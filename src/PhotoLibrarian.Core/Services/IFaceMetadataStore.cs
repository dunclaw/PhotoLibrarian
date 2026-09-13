using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.Core.Services;

public interface IFaceMetadataStore
{
    Task WriteAsync(
        string imagePath,
        PhotoFaceMetadata metadata,
        CancellationToken cancellationToken = default);

    async Task WriteBatchAsync(
        IReadOnlyCollection<(string ImagePath, PhotoFaceMetadata Metadata)> writes,
        CancellationToken cancellationToken = default)
    {
        foreach (var write in writes)
        {
            await WriteAsync(
                write.ImagePath,
                write.Metadata,
                cancellationToken);
        }
    }

    PhotoFaceMetadata Read(string imagePath);
}

public sealed class NullFaceMetadataStore : IFaceMetadataStore
{
    public static NullFaceMetadataStore Instance { get; } = new();

    private NullFaceMetadataStore()
    {
    }

    public Task WriteAsync(
        string imagePath,
        PhotoFaceMetadata metadata,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task WriteBatchAsync(
        IReadOnlyCollection<(string ImagePath, PhotoFaceMetadata Metadata)> writes,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public PhotoFaceMetadata Read(string imagePath) => new(0, 0, []);
}
