// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Security.Cryptography;

namespace PhotoLibrarian.Core.Services;

internal static class LibraryMetadataIdentity
{
    public static string CaptureStamp(string imagePath) =>
        string.Join("|", GetPaths(imagePath).Select(path =>
        {
            var file = new FileInfo(path);
            return file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}" : "-";
        }));

    public static async Task<string> ComputeAsync(string imagePath, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in GetPaths(imagePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(path))
            {
                hash.AppendData([0]);
                continue;
            }

            hash.AppendData([1]);
            await using var stream = File.OpenRead(path);
            hash.AppendData(await SHA256.HashDataAsync(stream, cancellationToken));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static IEnumerable<string> GetPaths(string imagePath) =>
        new[]
        {
            imagePath,
            FaceMetadataStore.GetSidecarPathForImage(imagePath),
            Path.ChangeExtension(imagePath, ".xmp")
        }.Distinct(StringComparer.OrdinalIgnoreCase);
}
