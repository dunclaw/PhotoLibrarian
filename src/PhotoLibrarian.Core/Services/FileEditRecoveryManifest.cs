// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoLibrarian.Core.Services;

internal sealed record FileEditRecoveryVersion(
    [property: JsonRequired] string? Copy,
    [property: JsonRequired] string? Hash);
internal sealed record FileEditRecoveryChange(
    [property: JsonRequired] string Path,
    [property: JsonRequired] FileEditRecoveryVersion Before,
    [property: JsonRequired] FileEditRecoveryVersion After,
    [property: JsonRequired] string? RecoveryPath,
    [property: JsonRequired] string? ReplacementPath);
internal sealed record FileEditRecoveryManifest(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string SourcePath,
    [property: JsonRequired] IReadOnlyList<string> Companions,
    [property: JsonRequired] bool Undo,
    [property: JsonRequired] IReadOnlyList<FileEditRecoveryChange> Changes)
{
    internal const string FileName = "recovery.json";

    internal void Write(string directory)
    {
        var path = Path.Combine(directory, FileName);
        var temporary = Path.Combine(directory, $"recovery-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, this);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
