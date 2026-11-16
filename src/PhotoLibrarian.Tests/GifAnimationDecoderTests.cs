// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.ViewModels;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class GifAnimationDecoderTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-Gif-{Guid.NewGuid():N}");

    public GifAnimationDecoderTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task CompositesTransparentFramesAndKeepsPreviousPixels()
    {
        string path = await CreateFixtureAsync(
            new GifFrameSpec(0, 0, 1, 1, 1, Disposal: 1, Delay: 5),
            new GifFrameSpec(1, 0, 1, 1, 2, Disposal: 1, Delay: 0));
        var decoder = await GifAnimationDecoder.OpenAsync(path);

        var first = Assert.IsType<AnimatedGifFrame>(decoder.ReadNextFrame());
        var firstPixels = first.Pixels.ToArray();
        Assert.Equal(TimeSpan.FromMilliseconds(50), first.Delay);
        AssertPixel(firstPixels, 0, 0, 0, 255);
        AssertPixel(firstPixels, 1, 0, 0, 0);

        var second = Assert.IsType<AnimatedGifFrame>(decoder.ReadNextFrame());
        var secondPixels = second.Pixels.ToArray();
        Assert.Equal(TimeSpan.FromMilliseconds(100), second.Delay);
        AssertPixel(secondPixels, 0, 0, 0, 255);
        AssertPixel(secondPixels, 1, 255, 0, 0);
        Assert.Null(decoder.ReadNextFrame());
    }

    [Fact]
    public async Task DisposalBackgroundClearsOnlyThePreviousFrameRectangle()
    {
        string path = await CreateFixtureAsync(
            new GifFrameSpec(0, 0, 1, 1, 1, Disposal: 2),
            new GifFrameSpec(1, 0, 1, 1, 2, Disposal: 1));
        var decoder = await GifAnimationDecoder.OpenAsync(path);
        decoder.ReadNextFrame();
        var afterDisposal = Assert.IsType<AnimatedGifFrame>(decoder.ReadNextFrame()).Pixels.ToArray();

        AssertPixel(afterDisposal, 0, 0, 0, 0);
        AssertPixel(afterDisposal, 1, 255, 0, 0);
    }

    [Fact]
    public async Task DisposalPreviousRestoresCanvasBeforeNextFrame()
    {
        string path = await CreateFixtureAsync(
            new GifFrameSpec(0, 0, 1, 1, 1, Disposal: 1),
            new GifFrameSpec(0, 0, 1, 1, 2, Disposal: 3),
            new GifFrameSpec(1, 0, 1, 1, 3, Disposal: 1));
        var decoder = await GifAnimationDecoder.OpenAsync(path);
        decoder.ReadNextFrame();
        var second = Assert.IsType<AnimatedGifFrame>(decoder.ReadNextFrame()).Pixels.ToArray();
        AssertPixel(second, 0, 255, 0, 0);
        var third = Assert.IsType<AnimatedGifFrame>(decoder.ReadNextFrame()).Pixels.ToArray();

        AssertPixel(third, 0, 0, 0, 255);
        AssertPixel(third, 1, 0, 255, 0);
    }

    [Fact]
    public async Task LoopExtensionControlsRepeatCountAndCancellationStopsDecoding()
    {
        string path = await CreateFixtureAsync(
            [new GifFrameSpec(0, 0, 1, 1, 1, Disposal: 1, Delay: 2)],
            loopCount: 2);
        var decoder = await GifAnimationDecoder.OpenAsync(path);

        Assert.Equal(2, decoder.LoopCount);
        Assert.True(decoder.ShouldRepeat(0));
        Assert.True(decoder.ShouldRepeat(1));
        Assert.False(decoder.ShouldRepeat(2));
        Assert.Equal(1, decoder.FrameCount);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => decoder.ReadNextFrame(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => GifAnimationDecoder.OpenAsync(path, cancellation.Token));
    }

    [Fact]
    public async Task ViewerMarksGifAsAnimatedAndClearsPlaybackIntentOnNavigationAndClose()
    {
        var gif = new ImageEntry { FilePath = @"D:\animation.gif", FileName = "animation.gif" };
        var still = new ImageEntry { FilePath = @"D:\still.png", FileName = "still.png" };
        var viewer = new ImageViewerViewModel();

        viewer.OpenImage(gif, [gif, still]);
        Assert.True(viewer.IsAnimatedGif);
        await viewer.NextImageCommand.ExecuteAsync(null);
        Assert.False(viewer.IsAnimatedGif);
        await viewer.PreviousImageCommand.ExecuteAsync(null);
        Assert.True(viewer.IsAnimatedGif);
        viewer.CloseCommand.Execute(null);
        Assert.False(viewer.IsAnimatedGif);
        Assert.Null(viewer.CurrentEntry);
    }

    private async Task<string> CreateFixtureAsync(params GifFrameSpec[] frames) =>
        await CreateFixtureAsync(frames, loopCount: null);

    private async Task<string> CreateFixtureAsync(GifFrameSpec[] frames, int? loopCount)
    {
        string path = Path.Combine(_directory, "fixture.gif");
        await File.WriteAllBytesAsync(path, BuildGif(frames, loopCount));
        return path;
    }

    private static byte[] BuildGif(IReadOnlyList<GifFrameSpec> frames, int? loopCount)
    {
        var bytes = new List<byte>();
        WriteAscii(bytes, "GIF89a");
        WriteUInt16(bytes, 2);
        WriteUInt16(bytes, 1);
        bytes.Add(0x81);
        bytes.Add(0);
        bytes.Add(0);
        bytes.AddRange([0, 0, 0, 255, 0, 0, 0, 0, 255, 0, 255, 0]);

        if (loopCount.HasValue)
        {
            bytes.AddRange([0x21, 0xFF, 0x0B]);
            WriteAscii(bytes, "NETSCAPE2.0");
            bytes.AddRange([3, 1, (byte)loopCount.Value, (byte)(loopCount.Value >> 8), 0]);
        }

        foreach (var frame in frames)
        {
            bytes.AddRange([0x21, 0xF9, 0x04, (byte)((frame.Disposal << 2) | 1)]);
            WriteUInt16(bytes, frame.Delay);
            bytes.AddRange([0, 0, 0x2C]);
            WriteUInt16(bytes, frame.Left);
            WriteUInt16(bytes, frame.Top);
            WriteUInt16(bytes, frame.Width);
            WriteUInt16(bytes, frame.Height);
            bytes.Add(0);
            bytes.Add(2);
            var compressed = EncodeSinglePixel(frame.ColorIndex);
            bytes.Add((byte)compressed.Length);
            bytes.AddRange(compressed);
            bytes.Add(0);
        }
        bytes.Add(0x3B);
        return [.. bytes];
    }

    private static byte[] EncodeSinglePixel(byte colorIndex)
    {
        int[] codes = [4, colorIndex, 5];
        var output = new List<byte>();
        int current = 0;
        int bits = 0;
        foreach (int code in codes)
        {
            current |= code << bits;
            bits += 3;
            while (bits >= 8)
            {
                output.Add((byte)current);
                current >>= 8;
                bits -= 8;
            }
        }
        if (bits > 0) output.Add((byte)current);
        return [.. output];
    }

    private static void AssertPixel(byte[] pixels, int x, byte blue, byte green, byte red)
    {
        int offset = x * 4;
        Assert.Equal(blue, pixels[offset]);
        Assert.Equal(green, pixels[offset + 1]);
        Assert.Equal(red, pixels[offset + 2]);
        Assert.Equal((byte)(blue == 0 && green == 0 && red == 0 ? 0 : 255), pixels[offset + 3]);
    }

    private static void WriteUInt16(List<byte> bytes, int value)
    {
        bytes.Add((byte)value);
        bytes.Add((byte)(value >> 8));
    }

    private static void WriteAscii(List<byte> bytes, string value) =>
        bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(value));

    private sealed record GifFrameSpec(
        int Left, int Top, int Width, int Height, byte ColorIndex, int Disposal, int Delay = 10);
}
