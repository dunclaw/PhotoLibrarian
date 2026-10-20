using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Vortice.MediaFoundation;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using static Vortice.MediaFoundation.MediaFactory;

namespace PhotoLibrarian.Core.Services;

/// <summary>Bridges Windows Media Foundation decoding to the Windows MP4 encoder.</summary>
internal sealed class WindowsStabilizedVideoSource : IDisposable
{
    private readonly IMFSourceReader _reader;
    private readonly object _gate = new();
    private readonly CancellationToken _cancellationToken;
    private readonly VideoFrameStabilizer _stabilizer;
    private readonly int _width, _height;
    private int _bufferWidth, _bufferHeight, _stride, _cropX, _cropY;
    private readonly uint _audioRate, _audioChannels;
    private bool _disposed;
    public MediaStreamSource StreamSource { get; }
    public Exception? Failure { get; private set; }

    public WindowsStabilizedVideoSource(
        string sourcePath, MediaEncodingProfile profile, TimeSpan duration, CancellationToken cancellationToken)
    {
        MFStartup().CheckError();
        IMFSourceReader? reader = null;
        try
        {
            _cancellationToken = cancellationToken;
            reader = MFCreateSourceReaderFromURL(sourcePath, null);
            reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            using (var type = MFCreateMediaType())
            {
                type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
                type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12).CheckError();
                reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, type);
            }
            reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
            using var decoded = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
            _width = checked((int)profile.Video.Width);
            _height = checked((int)profile.Video.Height);
            if (_width < 2 || _height < 2 || (_width & 1) != 0 || (_height & 1) != 0)
                throw new NotSupportedException("This video's decoded dimensions or pixel layout are not supported.");
            UpdateVideoFormat(decoded);
            _stabilizer = new VideoFrameStabilizer(_width, _height);
            var video = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Nv12, (uint)_width, (uint)_height);
            video.FrameRate.Numerator = profile.Video.FrameRate.Numerator;
            video.FrameRate.Denominator = profile.Video.FrameRate.Denominator;
            video.PixelAspectRatio.Numerator = profile.Video.PixelAspectRatio.Numerator;
            video.PixelAspectRatio.Denominator = profile.Video.PixelAspectRatio.Denominator;
            var videoDescriptor = new VideoStreamDescriptor(video);
            if (profile.Audio is not null)
            {
                using var audioType = MFCreateMediaType();
                audioType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio).CheckError();
                audioType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm).CheckError();
                audioType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u).CheckError();
                reader.SetCurrentMediaType(SourceReaderIndex.FirstAudioStream, audioType);
                reader.SetStreamSelection(SourceReaderIndex.FirstAudioStream, true);
                using var audio = reader.GetCurrentMediaType(SourceReaderIndex.FirstAudioStream);
                _audioRate = audio.GetUInt32(MediaTypeAttributeKeys.AudioSamplesPerSecond);
                _audioChannels = audio.GetUInt32(MediaTypeAttributeKeys.AudioNumChannels);
                StreamSource = new MediaStreamSource(videoDescriptor, new AudioStreamDescriptor(
                    AudioEncodingProperties.CreatePcm(_audioRate, _audioChannels, 16)));
            }
            else
                StreamSource = new MediaStreamSource(videoDescriptor);
            _reader = reader;
            StreamSource.BufferTime = TimeSpan.Zero;
            StreamSource.Duration = duration;
            StreamSource.Starting += OnStarting;
            StreamSource.SampleRequested += OnSampleRequested;
        }
        catch
        {
            reader?.Dispose();
            MFShutdown().CheckError();
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
                var stream = isVideo ? SourceReaderIndex.FirstVideoStream : SourceReaderIndex.FirstAudioStream;
                while (true)
                {
                    using var sample = _reader.ReadSample(stream, SourceReaderControlFlag.None,
                        out _, out var flags, out long timestamp);
                    _cancellationToken.ThrowIfCancellationRequested();
                    if ((flags & SourceReaderFlag.Error) != 0)
                        throw new InvalidDataException("Windows could not decode a media sample.");
                    if ((flags & SourceReaderFlag.CurrentMediaTypeChanged) != 0)
                    {
                        if (!isVideo)
                            throw new NotSupportedException("The audio track changes format during playback.");
                        using var changed = _reader.GetCurrentMediaType(stream);
                        UpdateVideoFormat(changed);
                    }
                    if ((flags & SourceReaderFlag.EndOfStream) != 0) return;
                    if (sample is null) continue;
                    using var buffer = sample.ConvertToContiguousBuffer();
                    byte[] data;
                    int stride = _stride;
                    using var buffer2D = isVideo ? buffer.QueryInterfaceOrNull<IMF2DBuffer>() : null;
                    if (buffer2D is not null)
                    {
                        data = new byte[checked(_bufferWidth * _bufferHeight * 3 / 2)];
                        if (buffer2D.ContiguousLength != data.Length)
                            throw new NotSupportedException("The decoded video has an unsupported pixel layout.");
                        buffer2D.ContiguousCopyTo(data, data.Length);
                        stride = _bufferWidth;
                    }
                    else
                    {
                        buffer.Lock(out nint pointer, out _, out int length);
                        try
                        {
                            data = new byte[length];
                            Marshal.Copy(pointer, data, 0, length);
                        }
                        finally { buffer.Unlock(); }
                    }
                    if (isVideo)
                    {
                        data = CropDecodedFrame(data, stride);
                        data = _stabilizer.Process(data, _cancellationToken);
                    }
                    var output = MediaStreamSample.CreateFromBuffer(data.AsBuffer(), TimeSpan.FromTicks(timestamp));
                    output.Duration = TimeSpan.FromTicks(sample.SampleDuration);
                    args.Request.Sample = output;
                    return;
                }
            }
            catch (Exception ex)
            {
                Failure = ex;
                sender.NotifyError(MediaStreamSourceErrorStatus.Other);
            }
        }
    }

    private void UpdateVideoFormat(IMFMediaType type)
    {
        MFGetAttributeSize(type, MediaTypeAttributeKeys.FrameSize, out uint width, out uint height).CheckError();
        _bufferWidth = checked((int)width);
        _bufferHeight = checked((int)height);
        _stride = checked((int)MFGetAttributeUInt32(type, MediaTypeAttributeKeys.DefaultStride, width));
        int displayWidth = _bufferWidth, displayHeight = _bufferHeight;
        _cropX = _cropY = 0;
        if (type.GetBlobSize(MediaTypeAttributeKeys.MinimumDisplayAperture, out _).Success)
        {
            var area = MemoryMarshal.Read<VideoArea>(type.GetBlob(MediaTypeAttributeKeys.MinimumDisplayAperture));
            if (area.OffsetX.Fract != 0 || area.OffsetY.Fract != 0)
                throw new NotSupportedException("Fractional video display apertures are not supported.");
            _cropX = area.OffsetX.Value;
            _cropY = area.OffsetY.Value;
            displayWidth = area.Area.Width;
            displayHeight = area.Area.Height;
        }
        if (type.GetGUID(MediaTypeAttributeKeys.Subtype) != VideoFormatGuids.NV12 ||
            displayWidth != _width || displayHeight != _height ||
            (_bufferWidth & 1) != 0 || (_bufferHeight & 1) != 0 || _stride < _bufferWidth ||
            _cropX < 0 || _cropY < 0 || (_cropX & 1) != 0 || (_cropY & 1) != 0 ||
            _cropX + _width > _bufferWidth || _cropY + _height > _bufferHeight)
            throw new NotSupportedException("The video changes dimensions or has an unsupported decoded pixel layout.");
    }

    private byte[] CropDecodedFrame(byte[] data, int stride)
    {
        if (data.Length < checked(stride * _bufferHeight * 3 / 2))
            throw new InvalidDataException("Windows returned an incomplete video frame.");
        if (stride == _width && _bufferHeight == _height && _cropX == 0 && _cropY == 0 &&
            data.Length == checked(_width * _height * 3 / 2))
            return data;

        var cropped = new byte[checked(_width * _height * 3 / 2)];
        for (int row = 0; row < _height; row++)
            Buffer.BlockCopy(data, (row + _cropY) * stride + _cropX, cropped, row * _width, _width);
        for (int row = 0; row < _height / 2; row++)
            Buffer.BlockCopy(data, stride * _bufferHeight + (row + _cropY / 2) * stride + _cropX,
                cropped, _width * _height + row * _width, _width);
        return cropped;
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
            MFShutdown().CheckError();
        }
    }
}
