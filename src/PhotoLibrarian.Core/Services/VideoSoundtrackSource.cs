// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Diagnostics.CodeAnalysis;
using Vortice.MediaFoundation;

namespace PhotoLibrarian.Core.Services;

/// <summary>Streams a replacement track on the edited video's timeline, independent of source trim and speed.</summary>
internal sealed class VideoSoundtrackSource : IDisposable
{
    private readonly MediaFoundationFrameReader _reader;
    private readonly long _frameLimit;
    private readonly int _blockSize;
    private DecodedSample? _first;
    private bool _endOfTrack;
    private long _nextFrame;

    public uint Rate => _reader.AudioRate;
    public uint Channels => _reader.AudioChannels;

    private VideoSoundtrackSource(MediaFoundationFrameReader reader, DecodedSample first, long frameLimit)
    {
        _reader = reader;
        _first = first;
        _frameLimit = frameLimit;
        _blockSize = checked((int)Channels * sizeof(short));
    }

    public static bool TryOpen(string path, TimeSpan duration, CancellationToken token,
        [NotNullWhen(true)] out VideoSoundtrackSource? soundtrack)
    {
        soundtrack = null;
        var reader = new MediaFoundationFrameReader(path, token);
        try
        {
            Guid expectedSubtype = Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase)
                ? AudioFormatGuids.Pcm : AudioFormatGuids.Mp3;
            if (reader.NativeAudioSubtype != expectedSubtype || reader.AudioBitsPerSample != 16 ||
                reader.AudioRate is not (44_100 or 48_000) || reader.AudioChannels is not (1 or 2))
                return false;

            long frameLimit = (long)Math.Floor(duration.Ticks * (reader.AudioRate / (double)TimeSpan.TicksPerSecond));
            if (frameLimit <= 0 || reader.Read(video: false, token) is not { Data.Length: > 0 } first)
                return false;
            soundtrack = new VideoSoundtrackSource(reader, first, frameLimit);
            return true;
        }
        finally
        {
            if (soundtrack is null)
                reader.Dispose();
        }
    }

    public DecodedSample? Read(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        long remaining = _frameLimit - _nextFrame;
        if (remaining <= 0) return null;

        var decoded = _endOfTrack ? null : _first ?? _reader.Read(video: false, token);
        _first = null;
        byte[] data;
        int frames;
        if (decoded is { } sample)
        {
            if (sample.Data.Length == 0 || sample.Data.Length % _blockSize != 0)
                throw new InvalidDataException("Windows returned an incomplete soundtrack audio buffer.");
            frames = (int)Math.Min(sample.Data.Length / _blockSize, remaining);
            int bytes = frames * _blockSize;
            data = bytes == sample.Data.Length ? sample.Data : sample.Data.AsSpan(0, bytes).ToArray();
        }
        else
        {
            _endOfTrack = true;
            frames = (int)Math.Min(1024, remaining);
            data = new byte[frames * _blockSize];
        }

        // Count PCM frames from zero rather than carrying container offsets or applying the video speed.
        long start = Timestamp(_nextFrame);
        _nextFrame += frames;
        return new DecodedSample(data, start, Timestamp(_nextFrame) - start);
    }

    private long Timestamp(long frame) => (long)Math.Round(frame * (TimeSpan.TicksPerSecond / (double)Rate));

    public void Dispose() => _reader.Dispose();
}
