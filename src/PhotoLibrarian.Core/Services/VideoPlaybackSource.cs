// Copyright (c) Duncan Lawler. All Rights Reserved.
using Windows.Media.Core;

namespace PhotoLibrarian.Core.Services;

/// <summary>
/// Opens a video for playback through an ordinary shared file handle.
/// </summary>
/// <remarks>
/// <see cref="MediaSource.CreateFromStorageFile"/> reads through an opportunistic lock. When
/// another process (Windows Search, an antivirus scan or the shell thumbnail provider) opens a
/// newly created file, Windows breaks that lock and closes the player's handle mid-playback
/// (0x80070323, "the oplock is now broken"), which surfaces as a decoding error. A plain handle
/// that shares read, write and delete access is not affected.
/// </remarks>
public sealed class VideoPlaybackSource : IDisposable
{
    private readonly FileStream _stream;

    public MediaSource Source { get; }

    private VideoPlaybackSource(FileStream stream, MediaSource source)
    {
        _stream = stream;
        Source = source;
    }

    public static VideoPlaybackSource Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.RandomAccess);
        try
        {
            var source = MediaSource.CreateFromStream(stream.AsRandomAccessStream(), GetContentType(path));
            return new VideoPlaybackSource(stream, source);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal static string GetContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mov" => "video/quicktime",
        ".avi" => "video/x-msvideo",
        ".mkv" => "video/x-matroska",
        ".wmv" => "video/x-ms-wmv",
        ".webm" => "video/webm",
        _ => "video/mp4",
    };

    public void Dispose()
    {
        Source.Dispose();
        _stream.Dispose();
    }
}
