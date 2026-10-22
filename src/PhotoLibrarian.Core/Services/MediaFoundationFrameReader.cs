// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Runtime.InteropServices;
using Vortice.MediaFoundation;
using static Vortice.MediaFoundation.MediaFactory;

namespace PhotoLibrarian.Core.Services;

/// <summary>A decoded media sample: NV12 video (display-cropped) or 16-bit PCM audio.</summary>
internal readonly record struct DecodedSample(byte[] Data, long Timestamp, long Duration);

/// <summary>
/// Synchronous Windows Media Foundation decoder producing tightly packed NV12 video frames
/// (decoder padding removed) and, optionally, 16-bit PCM audio. Not thread-safe.
/// </summary>
internal sealed class MediaFoundationFrameReader : IDisposable
{
    private readonly IMFSourceReader _reader;
    private readonly int _width, _height;
    private int _bufferWidth, _bufferHeight, _stride, _cropX, _cropY;
    private bool _disposed;

    public uint AudioRate { get; }
    public uint AudioChannels { get; }

    public MediaFoundationFrameReader(string sourcePath, int width, int height, bool includeAudio)
    {
        if (width < 2 || height < 2 || (width & 1) != 0 || (height & 1) != 0)
            throw new NotSupportedException("This video's decoded dimensions or pixel layout are not supported.");
        _width = width;
        _height = height;
        MFStartup().CheckError();
        IMFSourceReader? reader = null;
        try
        {
            reader = MFCreateSourceReaderFromURL(sourcePath, null);
            reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            using (var type = MFCreateMediaType())
            {
                type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
                type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12).CheckError();
                reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, type);
            }
            reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
            using (var decoded = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream))
                UpdateVideoFormat(decoded);

            if (includeAudio)
            {
                using var audioType = MFCreateMediaType();
                audioType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio).CheckError();
                audioType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm).CheckError();
                audioType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u).CheckError();
                reader.SetCurrentMediaType(SourceReaderIndex.FirstAudioStream, audioType);
                reader.SetStreamSelection(SourceReaderIndex.FirstAudioStream, true);
                using var audio = reader.GetCurrentMediaType(SourceReaderIndex.FirstAudioStream);
                AudioRate = audio.GetUInt32(MediaTypeAttributeKeys.AudioSamplesPerSecond);
                AudioChannels = audio.GetUInt32(MediaTypeAttributeKeys.AudioNumChannels);
            }
            _reader = reader;
        }
        catch
        {
            reader?.Dispose();
            MFShutdown().CheckError();
            throw;
        }
    }

    /// <summary>Reads the next video frame or audio buffer, or returns null at the end of the stream.</summary>
    public DecodedSample? Read(bool video, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var stream = video ? SourceReaderIndex.FirstVideoStream : SourceReaderIndex.FirstAudioStream;
        while (true)
        {
            using var sample = _reader.ReadSample(stream, SourceReaderControlFlag.None,
                out _, out var flags, out long timestamp);
            cancellationToken.ThrowIfCancellationRequested();
            if ((flags & SourceReaderFlag.Error) != 0)
                throw new InvalidDataException("Windows could not decode a media sample.");
            if ((flags & SourceReaderFlag.CurrentMediaTypeChanged) != 0)
            {
                if (!video)
                    throw new NotSupportedException("The audio track changes format during playback.");
                using var changed = _reader.GetCurrentMediaType(stream);
                UpdateVideoFormat(changed);
            }
            if ((flags & SourceReaderFlag.EndOfStream) != 0) return null;
            if (sample is null) continue;

            byte[] data = video ? CopyVideo(sample) : CopyAudio(sample);
            return new DecodedSample(data, timestamp, sample.SampleDuration);
        }
    }

    private byte[] CopyVideo(IMFSample sample)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        using var buffer2D = buffer.QueryInterfaceOrNull<IMF2DBuffer>();
        byte[] data;
        int stride = _stride;
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
            data = CopyBuffer(buffer);
        }
        return CropDecodedFrame(data, stride);
    }

    private static byte[] CopyAudio(IMFSample sample)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        return CopyBuffer(buffer);
    }

    private static byte[] CopyBuffer(IMFMediaBuffer buffer)
    {
        buffer.Lock(out nint pointer, out _, out int length);
        try
        {
            var data = new byte[length];
            Marshal.Copy(pointer, data, 0, length);
            return data;
        }
        finally
        {
            buffer.Unlock();
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
        if (_disposed) return;
        _disposed = true;
        _reader.Dispose();
        MFShutdown().CheckError();
    }
}
