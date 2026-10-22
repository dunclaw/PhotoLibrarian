// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.Core;
using Windows.Media.MediaProperties;

namespace PhotoLibrarian.Core.Services;

/// <summary>
/// Feeds stabilized frames to the Windows MP4 encoder. Frames are decoded in order and warped
/// with corrections planned in a prior analysis pass (see <see cref="VideoFrameStabilizer"/>).
/// </summary>
internal sealed class WindowsStabilizedVideoSource : IDisposable
{
    private readonly MediaFoundationFrameReader _reader;
    private readonly object _gate = new();
    private readonly CancellationToken _cancellationToken;
    private readonly VideoFrameStabilizer _stabilizer;
    private readonly IReadOnlyList<VideoFrameStabilizer.Correction> _corrections;
    private int _frameIndex;
    private bool _disposed;

    public MediaStreamSource StreamSource { get; }
    public Exception? Failure { get; private set; }

    public WindowsStabilizedVideoSource(
        string sourcePath, MediaEncodingProfile profile, TimeSpan duration,
        VideoFrameStabilizer stabilizer, IReadOnlyList<VideoFrameStabilizer.Correction> corrections,
        CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
        _stabilizer = stabilizer;
        _corrections = corrections;
        int width = checked((int)profile.Video.Width), height = checked((int)profile.Video.Height);
        _reader = new MediaFoundationFrameReader(sourcePath, width, height, includeAudio: profile.Audio is not null);
        try
        {
            var video = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Nv12, (uint)width, (uint)height);
            video.FrameRate.Numerator = profile.Video.FrameRate.Numerator;
            video.FrameRate.Denominator = profile.Video.FrameRate.Denominator;
            video.PixelAspectRatio.Numerator = profile.Video.PixelAspectRatio.Numerator;
            video.PixelAspectRatio.Denominator = profile.Video.PixelAspectRatio.Denominator;
            var videoDescriptor = new VideoStreamDescriptor(video);
            if (profile.Audio is not null)
            {
                var audio = AudioEncodingProperties.CreatePcm(_reader.AudioRate, _reader.AudioChannels, 16);
                StreamSource = new MediaStreamSource(videoDescriptor, new AudioStreamDescriptor(audio));
            }
            else
            {
                StreamSource = new MediaStreamSource(videoDescriptor);
            }
            StreamSource.BufferTime = TimeSpan.Zero;
            StreamSource.Duration = duration;
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
                if (_reader.Read(isVideo, _cancellationToken) is not { } sample) return;

                byte[] data = sample.Data;
                if (isVideo)
                {
                    // The analysis pass decodes the same frames in the same order; any extra frame
                    // beyond the plan (not expected) reuses the last planned correction.
                    var correction = _corrections.Count == 0
                        ? default
                        : _corrections[Math.Min(_frameIndex, _corrections.Count - 1)];
                    _frameIndex++;
                    data = _stabilizer.Render(data, correction, _cancellationToken);
                }
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
