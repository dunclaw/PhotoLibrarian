// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Runtime.InteropServices.WindowsRuntime;
using Vortice.MediaFoundation;
using Windows.Media.Core;
using Windows.Media.MediaProperties;

namespace PhotoLibrarian.Core.Services;

/// <summary>
/// Feeds decoded frames to the Windows MP4 encoder, optionally warping them with corrections
/// planned in a prior analysis pass (see <see cref="VideoFrameStabilizer"/>).
/// </summary>
internal sealed class WindowsStabilizedVideoSource : IDisposable
{
    private readonly MediaFoundationFrameReader _reader;
    private readonly object _gate = new();
    private readonly CancellationToken _cancellationToken;
    private readonly VideoFrameStabilizer? _stabilizer;
    private readonly IReadOnlyList<VideoFrameStabilizer.Correction> _corrections;
    private readonly int _brightness;
    private readonly int _contrast;
    private readonly int _width;
    private readonly int _height;
    private readonly long _trimStart;
    private readonly long _trimEnd;
    private readonly bool _isTrimming;
    private readonly double _playbackTimeScale;
    private int _frameIndex;
    private bool _disposed;

    public MediaStreamSource StreamSource { get; }
    public Exception? Failure { get; private set; }

    public WindowsStabilizedVideoSource(
        string sourcePath, MediaEncodingProfile profile, TimeSpan duration,
        VideoFrameStabilizer? stabilizer, IReadOnlyList<VideoFrameStabilizer.Correction> corrections,
        CancellationToken cancellationToken, int brightness = 0, int contrast = 100,
        TimeSpan trimStart = default, TimeSpan? trimEnd = null, int playbackSpeedPercent = 100)
    {
        if (playbackSpeedPercent is not (50 or 75 or 100))
            throw new ArgumentOutOfRangeException(nameof(playbackSpeedPercent));
        _cancellationToken = cancellationToken;
        _playbackTimeScale = 100d / playbackSpeedPercent;
        _stabilizer = stabilizer;
        _corrections = corrections;
        _brightness = brightness;
        _contrast = contrast;
        _trimStart = trimStart.Ticks;
        _trimEnd = trimEnd?.Ticks ?? long.MaxValue;
        _isTrimming = trimEnd is not null || trimStart != TimeSpan.Zero;
        int width = checked((int)profile.Video.Width), height = checked((int)profile.Video.Height);
        _width = width;
        _height = height;
        _reader = new MediaFoundationFrameReader(sourcePath, width, height,
            includeAudio: profile.Audio is not null, cancellationToken);
        try
        {
            var video = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Nv12, (uint)width, (uint)height);
            if (_reader.VideoNominalRange != 0)
            {
                video.Properties[MediaTypeAttributeKeys.VideoNominalRange] = _reader.VideoNominalRange;
                profile.Video.Properties[MediaTypeAttributeKeys.VideoNominalRange] = _reader.VideoNominalRange;
            }
            video.FrameRate.Numerator = profile.Video.FrameRate.Numerator;
            video.FrameRate.Denominator = profile.Video.FrameRate.Denominator;
            video.PixelAspectRatio.Numerator = profile.Video.PixelAspectRatio.Numerator;
            video.PixelAspectRatio.Denominator = profile.Video.PixelAspectRatio.Denominator;
            var videoDescriptor = new VideoStreamDescriptor(video);
            if (profile.Audio is not null)
            {
                uint slowedAudioRate = checked((uint)Math.Round(
                    _reader.AudioRate * (playbackSpeedPercent / 100d)));
                if (slowedAudioRate == 0)
                    throw new NotSupportedException("The selected playback speed is not supported by this audio track.");
                var audio = AudioEncodingProperties.CreatePcm(slowedAudioRate, _reader.AudioChannels, 16);
                StreamSource = new MediaStreamSource(videoDescriptor, new AudioStreamDescriptor(audio));
            }
            else
            {
                StreamSource = new MediaStreamSource(videoDescriptor);
            }
            StreamSource.BufferTime = TimeSpan.Zero;
            long sourceDuration = ((trimEnd ?? duration) - trimStart).Ticks;
            StreamSource.Duration = TimeSpan.FromTicks(ScaleTicks(sourceDuration));
            StreamSource.Starting += OnStarting;
            StreamSource.SampleRequested += OnSampleRequested;
        }
        catch
        {
            _reader.Dispose();
            throw;
        }
    }

    private static void OnStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args) =>
        args.Request.SetActualStartPosition(TimeSpan.Zero);

    private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                _cancellationToken.ThrowIfCancellationRequested();
                bool isVideo = args.Request.StreamDescriptor is VideoStreamDescriptor;
                DecodedSample sample;
                VideoFrameStabilizer.Correction correction = default;
                while (true)
                {
                    if (_reader.Read(isVideo, _cancellationToken) is not { } decoded) return;
                    if (isVideo)
                    {
                        if (_corrections.Count > 0)
                            correction = _corrections[Math.Min(_frameIndex, _corrections.Count - 1)];
                        _frameIndex++;
                    }
                    if (!_isTrimming)
                    {
                        sample = decoded;
                        break;
                    }
                    if (decoded.Timestamp >= _trimEnd) return;
                    if (isVideo)
                    {
                        if (decoded.Timestamp < _trimStart) continue;
                        sample = decoded with
                        {
                            Timestamp = decoded.Timestamp - _trimStart,
                            Duration = Math.Min(decoded.Duration, _trimEnd - decoded.Timestamp)
                        };
                    }
                    else
                    {
                        int blockSize = checked((int)_reader.AudioChannels * 2);
                        int sampleCount = decoded.Data.Length / blockSize;
                        double ticksPerSample = (double)TimeSpan.TicksPerSecond / _reader.AudioRate;
                        int first = (int)Math.Clamp(Math.Ceiling((_trimStart - decoded.Timestamp) / ticksPerSample),
                            0, sampleCount);
                        int last = (int)Math.Clamp(Math.Ceiling((_trimEnd - decoded.Timestamp) / ticksPerSample),
                            0, sampleCount);
                        if (last <= first) continue;
                        byte[] audio = first == 0 && last == sampleCount
                            ? decoded.Data : decoded.Data.AsSpan(first * blockSize, (last - first) * blockSize).ToArray();
                        sample = new DecodedSample(audio,
                            Math.Max(0, decoded.Timestamp + (long)Math.Round(first * ticksPerSample) - _trimStart),
                            (long)Math.Round((last - first) * ticksPerSample));
                    }
                    break;
                }
                sample = sample with
                {
                    Timestamp = ScaleTicks(sample.Timestamp),
                    Duration = ScaleTicks(sample.Duration)
                };
                byte[] data = sample.Data;
                if (isVideo && _stabilizer is not null)
                {
                    data = _stabilizer.Render(data, correction, _cancellationToken);
                }
                if (isVideo && (_brightness != 0 || _contrast != 100))
                    VideoToneAdjustment.ApplyBrightnessContrastNv12(
                        data, _width, _height, _reader.VideoNominalRange, _brightness, _contrast);
                var output = MediaStreamSample.CreateFromBuffer(data.AsBuffer(), TimeSpan.FromTicks(sample.Timestamp));
                output.Duration = TimeSpan.FromTicks(sample.Duration);
                args.Request.Sample = output;
            }
            catch (Exception ex)
            {
                Failure = ex;
                sender.NotifyError(MediaStreamSourceErrorStatus.Other);
            }
        }
    }

    private long ScaleTicks(long ticks)
    {
        double scaled = ticks * _playbackTimeScale;
        if (!double.IsFinite(scaled) || scaled > long.MaxValue || scaled < long.MinValue)
            throw new InvalidDataException("A video sample timestamp exceeds the supported duration.");
        return checked((long)Math.Round(scaled));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            StreamSource.SampleRequested -= OnSampleRequested;
            StreamSource.Starting -= OnStarting;
            _reader.Dispose();
        }
    }
}
