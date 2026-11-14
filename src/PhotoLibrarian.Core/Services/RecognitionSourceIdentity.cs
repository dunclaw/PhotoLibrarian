// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Security.Cryptography;
using PhotoLibrarian.Core.Diagnostics;

namespace PhotoLibrarian.Core.Services;

/// <summary>Identifies recognition input, independently of descriptive metadata where WIC supports it.</summary>
public static class RecognitionSourceIdentity
{
    public static async Task<string> ComputeAsync(
        string path,
        CancellationToken cancellationToken = default,
        string? formatPath = null)
    {
        var metadataFormatPath = formatPath ?? path;
        if (PhotoPrintImageDecoder.IsSupported(metadataFormatPath) ||
            EmbeddedMetadataWriter.IsSupported(metadataFormatPath))
        {
            try
            {
                var decoded = await WindowsImagingCodec.DecodeAsync(
                    path, cancellationToken: cancellationToken).ConfigureAwait(false);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                hash.AppendData(BitConverter.GetBytes(decoded.Width));
                hash.AppendData(BitConverter.GetBytes(decoded.Height));
                hash.AppendData(decoded.Pixels);
                return "pixels-v1:" + Convert.ToHexString(hash.GetHashAndReset());
            }
            catch (Exception exception) when (
                exception is System.Runtime.InteropServices.COMException or
                    InvalidDataException or NotSupportedException or ArgumentException)
            {
                DebugLog.WriteLine(
                    $"Recognition pixel identity unavailable for '{path}'; using file identity: {exception.Message}");
            }
        }

        // RAW and unsupported codecs remain conservative: any embedded change
        // invalidates recognition, while an XMP/adjustment sidecar does not.
        await using var stream = File.OpenRead(path);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return "file-v1:" + Convert.ToHexString(digest);
    }

    public static Task ValidateAsync(
        Models.ImageEntry image,
        CancellationToken cancellationToken) =>
        ValidateAsync(image.FilePath, image.SourceIdentity, cancellationToken);

    public static async Task ValidateAsync(
        string path,
        string? expectedIdentity,
        CancellationToken cancellationToken)
    {
        if (expectedIdentity is not null &&
            expectedIdentity != await ComputeAsync(path, cancellationToken))
        {
            throw new InvalidOperationException(
                "The photo content changed during recognition. Refresh the library before retrying.");
        }
    }
}
