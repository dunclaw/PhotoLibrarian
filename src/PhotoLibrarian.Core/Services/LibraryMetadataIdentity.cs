// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Security.Cryptography;
using System.Text;

namespace PhotoLibrarian.Core.Services;

internal static class LibraryMetadataIdentity
{
    public static string CaptureStamp(string imagePath)
    {
        // A shared legacy sidecar can be attributed by comparing RAW companion bytes.
        var legacyExists = File.Exists(Path.ChangeExtension(imagePath, ".xmp"));
        var paths = GetPaths(imagePath).Select(FileStamp);
        var companions = legacyExists
            ? FileEditTransaction.GetMediaCompanions(imagePath).Select(path =>
                $"companion:{Path.GetFileName(path).ToUpperInvariant()}:" +
                (RawToneMappingService.IsRaw(path) ? FileStamp(path) : "present"))
            : [];
        return string.Join("|", paths.Concat(companions));
    }

    private static string FileStamp(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}" : "-";
    }

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
        var legacyExists = File.Exists(Path.ChangeExtension(imagePath, ".xmp"));
        foreach (var companion in legacyExists ? FileEditTransaction.GetMediaCompanions(imagePath) : [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData("companion\0"u8);
            AppendName(hash, companion);
            var isRaw = RawToneMappingService.IsRaw(companion);
            hash.AppendData(isRaw ? [1] : [0]);
            if (isRaw)
            {
                await using var stream = File.OpenRead(companion);
                hash.AppendData(await SHA256.HashDataAsync(stream, cancellationToken));
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendName(IncrementalHash hash, string path)
    {
        var name = Encoding.UTF8.GetBytes(Path.GetFileName(path).ToUpperInvariant());
        hash.AppendData(BitConverter.GetBytes(name.Length));
        hash.AppendData(name);
    }

    private static IEnumerable<string> GetPaths(string imagePath)
    {
        var paths = new[]
        {
            imagePath,
            FaceMetadataStore.GetSidecarPathForImage(imagePath),
            MetadataSidecarLocator.GetWritePath(imagePath),
            Path.ChangeExtension(imagePath, ".xmp")
        };
        var qualified = imagePath + ".xmp";
        return (File.Exists(qualified) ? paths.Append(qualified) : paths)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
