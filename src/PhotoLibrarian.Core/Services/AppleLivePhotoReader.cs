// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Buffers.Binary;
using System.Text;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace PhotoLibrarian.Core.Services;

public enum AppleLivePhotoPairState
{
    NotLivePhoto,
    Paired,
    MissingMovie,
    AmbiguousMovies,
    MissingStillImageTime,
    DiscoveryLimitExceeded,
    UnsupportedStillFormat
}

public sealed record AppleLivePhotoPair(
    AppleLivePhotoPairState State,
    string? ContentIdentifier = null,
    string? MoviePath = null,
    long StillLength = 0,
    DateTime StillLastWriteUtc = default,
    long MovieLength = 0,
    DateTime MovieLastWriteUtc = default,
    string? StillPath = null);

/// <summary>
/// Reads Apple Live Photo identifiers and finds a unique, identifier-matched MOV in the still's
/// containing directory. It never uses filenames as pairing evidence or modifies either file.
/// </summary>
public static class AppleLivePhotoReader
{
    private const int MaximumMetadataReadBytes = 8 * 1024 * 1024;
    private const int MaximumFolderEntries = 2048;
    private const int MaximumMovieCandidates = 256;
    private const int MaximumMovieBoxes = 4096;
    private const int MaximumMovieHeaderBytes = 8 * 1024 * 1024;
    private const int MaximumMovieMetadataAtoms = 4096;
    private const int MaximumMakerNoteBytes = 1024 * 1024;
    private const int MaximumMakerNoteEntries = 128;
    private const int MakerNoteTag = 0x927c;
    public static async Task<AppleLivePhotoPair> FindPairAsync(
        string stillPath, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stillPath);
        token.ThrowIfCancellationRequested();

        var stillInfo = new FileInfo(stillPath);
        if (!stillInfo.Exists)
            throw new FileNotFoundException("The Live Photo still image no longer exists.", stillPath);
        if (stillInfo.Extension.Equals(".heic", StringComparison.OrdinalIgnoreCase) ||
            stillInfo.Extension.Equals(".heif", StringComparison.OrdinalIgnoreCase))
            return new AppleLivePhotoPair(AppleLivePhotoPairState.UnsupportedStillFormat);
        if (!IsJpegStillExtension(stillInfo.Extension))
            return new AppleLivePhotoPair(AppleLivePhotoPairState.NotLivePhoto);

        string? stillIdentifier = await Task.Run(
            () => ReadStillIdentifier(stillPath, token), token);
        if (stillIdentifier is null)
            return new AppleLivePhotoPair(AppleLivePhotoPairState.NotLivePhoto);

        string directory = Path.GetDirectoryName(Path.GetFullPath(stillPath))
            ?? throw new InvalidDataException("The still image has no containing directory.");
        var movieMatches = new List<(string Path, FileInfo Info, bool HasStillImageTime)>();
        int inspectedFiles = 0;
        int movieCandidates = 0;

        foreach (string candidatePath in System.IO.Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            token.ThrowIfCancellationRequested();
            if (++inspectedFiles > MaximumFolderEntries)
                return Result(AppleLivePhotoPairState.DiscoveryLimitExceeded);

            if (!string.Equals(Path.GetExtension(candidatePath), ".mov", StringComparison.OrdinalIgnoreCase))
                continue;
            if (++movieCandidates > MaximumMovieCandidates)
                return Result(AppleLivePhotoPairState.DiscoveryLimitExceeded);

            try
            {
                if ((File.GetAttributes(candidatePath) & FileAttributes.ReparsePoint) != 0)
                    continue;

                var candidateInfo = new FileInfo(candidatePath);
                var movie = await Task.Run(
                    () => ReadMovieMetadata(candidatePath, token), token);
                if (!string.Equals(movie.ContentIdentifier, stillIdentifier, StringComparison.Ordinal))
                    continue;

                movieMatches.Add((candidatePath, candidateInfo, movie.HasStillImageTime));
                if (movieMatches.Count > 1)
                    return Result(AppleLivePhotoPairState.AmbiguousMovies);
            }
            catch (Exception exception) when (IsUnsupportedCandidate(exception))
            {
                // Unrelated or damaged MOV files are not pairing evidence.
            }
        }

        if (movieMatches.Count == 1)
        {
            var movie = movieMatches[0];
            if (!movie.HasStillImageTime)
                return Result(AppleLivePhotoPairState.MissingStillImageTime);
            return new AppleLivePhotoPair(AppleLivePhotoPairState.Paired, stillIdentifier,
                movie.Path, stillInfo.Length, stillInfo.LastWriteTimeUtc,
                movie.Info.Length, movie.Info.LastWriteTimeUtc, stillPath);
        }

        return Result(AppleLivePhotoPairState.MissingMovie);

        AppleLivePhotoPair Result(AppleLivePhotoPairState state) =>
            new(state, stillIdentifier, StillLength: stillInfo.Length,
                StillLastWriteUtc: stillInfo.LastWriteTimeUtc, StillPath: stillPath);
    }

    public static async Task ValidatePairAsync(AppleLivePhotoPair pair, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(pair);
        if (pair.State != AppleLivePhotoPairState.Paired ||
            pair.ContentIdentifier is null || pair.MoviePath is null)
            throw new InvalidDataException("A verified Apple Live Photo pair is required.");

        token.ThrowIfCancellationRequested();
        VerifySnapshot(pair.MoviePath, pair.MovieLength, pair.MovieLastWriteUtc);
        VerifySnapshotFromPairStill(pair);

        string? stillIdentifier = await Task.Run(
            () => ReadStillIdentifier(GetStillPath(pair), token), token);
        var movie = await Task.Run(
            () => ReadMovieMetadata(pair.MoviePath, token), token);

        token.ThrowIfCancellationRequested();
        VerifySnapshot(pair.MoviePath, pair.MovieLength, pair.MovieLastWriteUtc);
        VerifySnapshotFromPairStill(pair);
        if (!string.Equals(stillIdentifier, pair.ContentIdentifier, StringComparison.Ordinal) ||
            !string.Equals(movie.ContentIdentifier, pair.ContentIdentifier, StringComparison.Ordinal) ||
            !movie.HasStillImageTime)
            throw new InvalidDataException("The Live Photo files changed after pairing.");
    }

    private static string GetStillPath(AppleLivePhotoPair pair) =>
        pair.StillPath ?? throw new InvalidDataException("The Live Photo still path is missing.");

    private static void VerifySnapshotFromPairStill(AppleLivePhotoPair pair)
    {
        if (pair.StillPath is null)
            throw new InvalidDataException("The Live Photo still path is missing.");
        VerifySnapshot(pair.StillPath, pair.StillLength, pair.StillLastWriteUtc);
    }

    private static void VerifySnapshot(string path, long expectedLength, DateTime expectedLastWriteUtc)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != expectedLength || info.LastWriteTimeUtc != expectedLastWriteUtc)
            throw new InvalidDataException("A Live Photo file changed after pairing.");
    }

    private static bool IsJpegStillExtension(string extension) =>
        extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);

    private static string? ReadStillIdentifier(string path, CancellationToken token)
    {
        using var file = OpenRead(path);
        using var bounded = new BoundedReadStream(file, MaximumMetadataReadBytes, token);
        var directories = ImageMetadataReader.ReadMetadata(bounded, path);
        token.ThrowIfCancellationRequested();

        bool isAppleImage = directories.OfType<ExifIfd0Directory>()
            .Where(directory => directory.ContainsTag(ExifDirectoryBase.TagMake))
            .Select(directory => directory.GetString(ExifDirectoryBase.TagMake)?.Trim())
            .Any(make => make?.StartsWith("Apple", StringComparison.OrdinalIgnoreCase) == true);
        if (!isAppleImage)
            return null;

        var makerNotes = directories.OfType<ExifSubIfdDirectory>()
            .Where(directory => directory.ContainsTag(MakerNoteTag))
            .Select(directory => directory.GetByteArray(MakerNoteTag))
            .Where(bytes => bytes is { Length: > 0 })
            .ToList();
        if (makerNotes.Count == 0)
            return null;
        if (makerNotes.Count != 1)
            throw new InvalidDataException("The still image contains ambiguous MakerNote metadata.");

        return ReadAppleMakerNoteIdentifier(makerNotes[0]!);
    }

    private static string? ReadAppleMakerNoteIdentifier(byte[] makerNote)
    {
        if (makerNote.Length > MaximumMakerNoteBytes)
            throw new InvalidDataException("The Apple MakerNote exceeds the 1 MiB limit.");

        bool littleEndian;
        int ifdOffset;
        if (makerNote.AsSpan().StartsWith("Apple iOS\0"u8))
        {
            if (makerNote.Length < 20 || (makerNote[12] != (byte)'I' && makerNote[12] != (byte)'M') ||
                makerNote[12] != makerNote[13])
                throw new InvalidDataException("The Apple MakerNote header is invalid.");
            littleEndian = makerNote[12] == (byte)'I';
            ushort magic = ReadUInt16(makerNote, 14, littleEndian);
            uint firstIfd = ReadUInt32(makerNote, 16, littleEndian);
            if (magic != 42 || firstIfd > int.MaxValue)
                throw new InvalidDataException("The Apple MakerNote TIFF header is invalid.");
            ifdOffset = checked((int)firstIfd);
        }
        else
        {
            littleEndian = true;
            ifdOffset = 0;
        }

        if (ifdOffset < 0 || ifdOffset > makerNote.Length - 2)
            throw new InvalidDataException("The Apple MakerNote IFD is out of range.");

        ushort entryCount = ReadUInt16(makerNote, ifdOffset, littleEndian);
        if (entryCount > MaximumMakerNoteEntries)
            throw new InvalidDataException("The Apple MakerNote has too many entries.");
        int entriesOffset = checked(ifdOffset + 2);
        int tableLength = checked(entryCount * 12);
        if (entriesOffset > makerNote.Length - tableLength - 4)
            throw new InvalidDataException("The Apple MakerNote IFD is truncated.");

        var identifiers = new List<string>(1);
        for (int index = 0; index < entryCount; index++)
        {
            int entryOffset = checked(entriesOffset + index * 12);
            ushort tag = ReadUInt16(makerNote, entryOffset, littleEndian);
            if (tag != 0x0011)
                continue;

            ushort type = ReadUInt16(makerNote, entryOffset + 2, littleEndian);
            uint count = ReadUInt32(makerNote, entryOffset + 4, littleEndian);
            if (type != 2 || count is 0 or > 256)
                throw new InvalidDataException("The Apple content identifier is not a bounded ASCII string.");

            int valueOffset;
            if (count <= 4)
                valueOffset = entryOffset + 8;
            else
            {
                uint relativeOffset = ReadUInt32(makerNote, entryOffset + 8, littleEndian);
                if (relativeOffset > int.MaxValue)
                    throw new InvalidDataException("The Apple content identifier offset is invalid.");
                valueOffset = checked((int)relativeOffset);
            }

            if (valueOffset < 0 || valueOffset > makerNote.Length - (int)count)
                throw new InvalidDataException("The Apple content identifier is outside its MakerNote.");

            string value = Encoding.ASCII.GetString(makerNote, valueOffset, (int)count)
                .TrimEnd('\0', ' ');
            if (value.Length is 0 or > 128 || value.Any(character => char.IsControl(character)))
                throw new InvalidDataException("The Apple content identifier is invalid.");
            identifiers.Add(value);
        }

        if (identifiers.Count > 1)
            throw new InvalidDataException("The Apple MakerNote has multiple content identifiers.");
        return identifiers.SingleOrDefault();
    }

    private static (string? ContentIdentifier, bool HasStillImageTime) ReadMovieMetadata(
        string path, CancellationToken token)
    {
        using var stream = OpenRead(path);
        byte[]? movieHeader = null;
        bool hasFileType = false;
        bool hasMedia = false;
        int boxCount = 0;
        byte[] header = new byte[16];

        while (stream.Position < stream.Length)
        {
            token.ThrowIfCancellationRequested();
            if (++boxCount > MaximumMovieBoxes || stream.Length - stream.Position < 8)
                throw new InvalidDataException("The QuickTime movie has invalid or excessive top-level atoms.");

            long start = stream.Position;
            stream.ReadExactly(header.AsSpan(0, 8));
            ulong size = BinaryPrimitives.ReadUInt32BigEndian(header);
            string type = Encoding.ASCII.GetString(header, 4, 4);
            int headerLength = 8;
            if (size == 1)
            {
                if (stream.Length - stream.Position < 8)
                    throw new InvalidDataException("The QuickTime atom header is truncated.");
                stream.ReadExactly(header.AsSpan(8, 8));
                size = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8, 8));
                headerLength = 16;
            }
            else if (size == 0)
                size = checked((ulong)(stream.Length - start));

            if (size < (ulong)headerLength || size > (ulong)(stream.Length - start))
                throw new InvalidDataException("The QuickTime atom length is invalid.");

            if (type == "ftyp")
                hasFileType = size >= (ulong)(headerLength + 8);
            else if (type == "mdat")
                hasMedia = true;
            else if (type == "moov")
            {
                if (size > MaximumMovieHeaderBytes)
                    throw new InvalidDataException("The QuickTime movie header exceeds the 8 MiB limit.");
                movieHeader = new byte[checked((int)size - headerLength)];
                stream.ReadExactly(movieHeader);
            }

            stream.Position = checked(start + (long)size);
        }

        if (!hasFileType || !hasMedia || movieHeader is null)
            throw new InvalidDataException("The QuickTime movie is missing its file, movie, or media header.");

        var metadata = new MovieMetadata();
        int metadataAtomCount = 0;
        ReadMovieContainers(movieHeader, 0, movieHeader.Length, 0,
            ref metadataAtomCount, metadata, token);
        if (metadata.ContentIdentifiers.Count > 1)
            throw new InvalidDataException("The movie contains ambiguous content identifiers.");
        return (metadata.ContentIdentifiers.SingleOrDefault(), metadata.HasStillImageTime);
    }

    private static void ReadMovieContainers(
        byte[] bytes, int start, int end, int depth, ref int atomCount,
        MovieMetadata metadata, CancellationToken token)
    {
        if (depth > 12)
            throw new InvalidDataException("The QuickTime metadata hierarchy is too deep.");

        foreach (var atom in ReadAtoms(bytes, start, end, ref atomCount, token))
        {
            if (atom.Type == "meta")
                ReadMetadataAtom(bytes, atom.PayloadStart, atom.End,
                    ref atomCount, metadata, token);
            else if (atom.Type is "udta" or "trak" or "mdia" or "minf" or "stbl")
                ReadMovieContainers(bytes, atom.PayloadStart, atom.End, depth + 1,
                    ref atomCount, metadata, token);
        }
    }

    private static void ReadMetadataAtom(
        byte[] bytes, int start, int end, ref int atomCount,
        MovieMetadata metadata, CancellationToken token)
    {
        if (end - start < 4)
            throw new InvalidDataException("The QuickTime metadata atom is truncated.");

        string? handler = null;
        List<string?>? keys = null;
        var itemLists = new List<QuickTimeAtom>();
        foreach (var atom in ReadAtoms(bytes, start + 4, end, ref atomCount, token))
        {
            if (atom.Type == "hdlr")
            {
                if (atom.End - atom.PayloadStart < 12)
                    throw new InvalidDataException("The QuickTime metadata handler is truncated.");
                handler = Encoding.ASCII.GetString(bytes, atom.PayloadStart + 8, 4);
            }
            else if (atom.Type == "keys")
                keys = ReadKeyTable(bytes, atom.PayloadStart, atom.End);
            else if (atom.Type == "ilst")
                itemLists.Add(atom);
        }

        if (handler != "mdta" || keys is null)
            return;

        foreach (var itemList in itemLists)
        {
            foreach (var item in ReadAtoms(bytes, itemList.PayloadStart, itemList.End,
                ref atomCount, token))
            {
                if (item.TypeCode == 0 || item.TypeCode > keys.Count)
                    throw new InvalidDataException("The QuickTime metadata item has an invalid key index.");
                string? key = keys[(int)item.TypeCode - 1];
                if (string.Equals(key, "com.apple.quicktime.still-image-time", StringComparison.Ordinal))
                {
                    metadata.HasStillImageTime |= HasStillImageTimeValue(
                        bytes, item.PayloadStart, item.End, ref atomCount, token);
                    continue;
                }
                if (!string.Equals(key, "com.apple.quicktime.content.identifier", StringComparison.Ordinal))
                    continue;

                string? identifier = ReadIdentifierItem(bytes, item.PayloadStart,
                    item.End, ref atomCount, token);
                if (identifier is not null)
                    metadata.ContentIdentifiers.Add(identifier);
            }
        }
    }

    private static bool HasStillImageTimeValue(
        byte[] bytes, int start, int end, ref int atomCount, CancellationToken token)
    {
        bool hasValue = false;
        foreach (var atom in ReadAtoms(bytes, start, end, ref atomCount, token))
        {
            if (atom.Type != "data")
                continue;
            if (atom.End - atom.PayloadStart != 9 ||
                BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(atom.PayloadStart, 4)) != 21)
                throw new InvalidDataException("The QuickTime still-image-time value is invalid.");
            hasValue = true;
        }
        return hasValue;
    }

    private static List<string?> ReadKeyTable(byte[] bytes, int start, int end)
    {
        if (end - start < 8)
            throw new InvalidDataException("The QuickTime metadata key table is truncated.");
        uint keyCount = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(start + 4, 4));
        if (keyCount is 0 or > 256)
            throw new InvalidDataException("The QuickTime metadata key count is invalid.");

        var keys = new List<string?>(checked((int)keyCount));
        int position = start + 8;
        for (int index = 0; index < keyCount; index++)
        {
            if (end - position < 8)
                throw new InvalidDataException("The QuickTime metadata key is truncated.");
            uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position, 4));
            if (length < 8 || length > (uint)(end - position))
                throw new InvalidDataException("The QuickTime metadata key length is invalid.");
            int keyLength = checked((int)length - 8);
            string keyNamespace = Encoding.ASCII.GetString(bytes, position + 4, 4);
            string? key = null;
            if (keyNamespace == "mdta")
            {
                key = Encoding.UTF8.GetString(bytes, position + 8, keyLength);
                if (key.Length == 0 || key.Any(char.IsControl))
                    throw new InvalidDataException("The QuickTime metadata key is invalid.");
            }
            keys.Add(key);
            position = checked(position + (int)length);
        }

        if (position != end)
            throw new InvalidDataException("The QuickTime metadata key table has trailing data.");
        return keys;
    }

    private static string? ReadIdentifierItem(
        byte[] bytes, int start, int end, ref int atomCount, CancellationToken token)
    {
        string? identifier = null;
        foreach (var atom in ReadAtoms(bytes, start, end, ref atomCount, token))
        {
            if (atom.Type != "data")
                continue;
            if (atom.End - atom.PayloadStart < 8)
                throw new InvalidDataException("The QuickTime content identifier data is truncated.");
            uint valueType = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(atom.PayloadStart, 4));
            if (valueType != 1)
                throw new InvalidDataException("The QuickTime content identifier is not UTF-8 text.");
            string value = Encoding.UTF8.GetString(bytes, atom.PayloadStart + 8,
                atom.End - atom.PayloadStart - 8).TrimEnd('\0', ' ');
            if (value.Length is 0 or > 128 || value.Any(char.IsControl))
                throw new InvalidDataException("The QuickTime content identifier is invalid.");
            if (identifier is not null && !string.Equals(identifier, value, StringComparison.Ordinal))
                throw new InvalidDataException("The QuickTime metadata item has conflicting identifiers.");
            identifier = value;
        }
        return identifier;
    }

    private static List<QuickTimeAtom> ReadAtoms(
        byte[] bytes, int start, int end, ref int atomCount, CancellationToken token)
    {
        if (start < 0 || end > bytes.Length || start > end)
            throw new InvalidDataException("The QuickTime metadata atom range is invalid.");
        var atoms = new List<QuickTimeAtom>();
        int position = start;
        while (position < end)
        {
            token.ThrowIfCancellationRequested();
            if (++atomCount > MaximumMovieMetadataAtoms || end - position < 8)
                throw new InvalidDataException("The QuickTime metadata has invalid or excessive atoms.");
            ulong size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position, 4));
            uint typeCode = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position + 4, 4));
            int headerLength = 8;
            if (size == 1)
            {
                if (end - position < 16)
                    throw new InvalidDataException("The QuickTime extended metadata atom is truncated.");
                size = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(position + 8, 8));
                headerLength = 16;
            }
            else if (size == 0)
                size = checked((ulong)(end - position));
            if (size < (ulong)headerLength || size > (ulong)(end - position))
                throw new InvalidDataException("The QuickTime metadata atom length is invalid.");
            int atomEnd = checked(position + (int)size);
            atoms.Add(new QuickTimeAtom(
                Encoding.ASCII.GetString(bytes, position + 4, 4), typeCode,
                position + headerLength, atomEnd));
            position = atomEnd;
        }
        return atoms;
    }

    private sealed class MovieMetadata
    {
        public List<string> ContentIdentifiers { get; } = [];
        public bool HasStillImageTime { get; set; }
    }

    private readonly record struct QuickTimeAtom(string Type, uint TypeCode, int PayloadStart, int End);

    private static bool IsUnsupportedCandidate(Exception exception) =>
        exception is IOException or InvalidDataException or ArgumentException or
        IndexOutOfRangeException or OverflowException or ImageProcessingException;

    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.Read | FileShare.Delete, 65536, FileOptions.RandomAccess);

    private static ushort ReadUInt16(byte[] bytes, int offset, bool littleEndian)
    {
        var value = bytes.AsSpan(offset, 2);
        return littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(value)
            : BinaryPrimitives.ReadUInt16BigEndian(value);
    }

    private static uint ReadUInt32(byte[] bytes, int offset, bool littleEndian)
    {
        var value = bytes.AsSpan(offset, 4);
        return littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(value)
            : BinaryPrimitives.ReadUInt32BigEndian(value);
    }

    private sealed class BoundedReadStream(Stream inner, int maximumBytes, CancellationToken token) : Stream
    {
        private int _bytesRead;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            token.ThrowIfCancellationRequested();
            if (buffer.IsEmpty)
                return 0;
            int remaining = maximumBytes - _bytesRead;
            if (remaining <= 0)
                throw new InvalidDataException("Metadata parsing exceeded the 8 MiB read limit.");
            int read = inner.Read(buffer[..Math.Min(buffer.Length, remaining)]);
            _bytesRead = checked(_bytesRead + read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            token.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.IsEmpty)
                return 0;
            int remaining = maximumBytes - _bytesRead;
            if (remaining <= 0)
                throw new InvalidDataException("Metadata parsing exceeded the 8 MiB read limit.");
            int read = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, remaining)], cancellationToken);
            _bytesRead = checked(_bytesRead + read);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
