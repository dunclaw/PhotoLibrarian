using System.Diagnostics;
using System.Text;
using PhotoLibrarian.Core.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class VideoStabilizationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-Video-{Guid.NewGuid():N}");
    private string Source => Path.Combine(_directory, "source & original.mp4");
    private string Output => Path.Combine(_directory, "stabilized copy.mp4");
    private string FakeExecutable => Path.Combine(_directory, "ffmpeg.exe");

    public VideoStabilizationTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Source, "original");
        File.WriteAllText(FakeExecutable, "");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void ArgumentsAreSeparateAndExportOnlySelectedStreams()
    {
        var info = VideoStabilizationService.CreateStartInfo(FakeExecutable, Source, Output);
        Assert.False(info.UseShellExecute);
        Assert.True(info.RedirectStandardError);
        Assert.True(info.RedirectStandardOutput);
        Assert.Contains(Source, info.ArgumentList);
        Assert.Equal(Output, info.ArgumentList.Last());
        Assert.Contains("-n", info.ArgumentList);
        Assert.Contains("-nostdin", info.ArgumentList);
        Assert.Contains("0:a:0?", info.ArgumentList);
        Assert.Contains("0:v:0", info.ArgumentList);
        Assert.Contains("deshake=rx=16:ry=16:edge=mirror,pad=ceil(iw/2)*2:ceil(ih/2)*2", info.ArgumentList);
    }

    [Fact]
    public void RejectsMissingRelativeAndNonMp4Paths()
    {
        Assert.Throws<ArgumentException>(() => VideoStabilizationService.ValidatePaths("ffmpeg.exe", Source, Output));
        Assert.Throws<ArgumentException>(() => VideoStabilizationService.ValidatePaths(Output, Source, Output));
        Assert.Throws<ArgumentException>(() => VideoStabilizationService.ValidatePaths(FakeExecutable, Output, Output));
        Assert.Throws<ArgumentException>(() => VideoStabilizationService.ValidatePaths(FakeExecutable, Source, "out.mp4"));
        Assert.Throws<ArgumentException>(() => VideoStabilizationService.ValidatePaths(FakeExecutable, Source, Output + ".avi"));
    }

    [Fact]
    public async Task NeverOverwritesSourceOrExistingOutput()
    {
        var service = new VideoStabilizationService();
        await Assert.ThrowsAsync<IOException>(() => service.StabilizeAsync(FakeExecutable, Source, Source,
            cancellationToken: TestContext.Current.CancellationToken));
        File.WriteAllText(Output, "existing");
        await Assert.ThrowsAsync<IOException>(() => service.StabilizeAsync(FakeExecutable, Source, Output,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("original", File.ReadAllText(Source));
        Assert.Equal("existing", File.ReadAllText(Output));
    }

    [Fact]
    public async Task PreCanceledExportDoesNotStartAProcess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new VideoStabilizationService().StabilizeAsync(FakeExecutable, Source, Output,
                cancellationToken: cancellation.Token));
        AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FfmpegExportsAndReducesSyntheticShake(bool withAudio)
    {
        string ffmpeg = GetIntegrationExecutable();
        string pattern = Path.Combine(_directory, "pattern.ppm");
        using (var stream = File.Create(pattern))
        {
            stream.Write(Encoding.ASCII.GetBytes("P6\n352 272\n255\n"));
            var random = new Random(77);
            byte[] blocks = new byte[44 * 34];
            random.NextBytes(blocks);
            for (int y = 0; y < 272; y++)
            for (int x = 0; x < 352; x++)
            {
                byte value = blocks[y / 8 * 44 + x / 8];
                stream.Write([value, value, value]);
            }
        }
        var arguments = new List<string> { "-y", "-loop", "1", "-framerate", "24", "-i", pattern };
        if (withAudio) arguments.AddRange(["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000"]);
        arguments.AddRange(["-t", "3", "-vf", "crop=320:240:16+8*sin(n*2):16+8*cos(n*2)",
            "-c:v", "libx264", "-crf", "15", "-pix_fmt", "yuv420p", Source]);
        await RunFfmpegAsync(ffmpeg, arguments);
        byte[] original = File.ReadAllBytes(Source);
        await new VideoStabilizationService().StabilizeAsync(ffmpeg, Source, Output,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(original, File.ReadAllBytes(Source));
        Assert.True(new FileInfo(Output).Length > 0);
        byte[] before = await DecodeAsync(ffmpeg, Source);
        byte[] after = await DecodeAsync(ffmpeg, Output);
        Assert.Equal(before.Length, after.Length);
        double ratio = TemporalDifference(after) / TemporalDifference(before);
        TestContext.Current.TestOutputHelper?.WriteLine($"Temporal difference ratio: {ratio:F3}");
        Assert.True(ratio < 0.8,
            $"Stabilization must reduce central temporal differences by at least 20%; actual ratio {ratio:F3}.");
        if (withAudio)
            await RunFfmpegAsync(ffmpeg, ["-i", Output, "-map", "0:a:0", "-f", "null", "-"]);
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task FfmpegFailureIsReportedAndCleanedUp()
    {
        string ffmpeg = GetIntegrationExecutable();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new VideoStabilizationService().StabilizeAsync(ffmpeg, Source, Output,
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("FFmpeg failed", error.Message);
        Assert.False(File.Exists(Output));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task FfmpegPadsOddDimensionsForH264()
    {
        string ffmpeg = GetIntegrationExecutable();
        await RunFfmpegAsync(ffmpeg, ["-y", "-f", "lavfi", "-i", "testsrc=size=321x241:rate=24",
            "-t", "1", "-c:v", "libx264", "-pix_fmt", "yuv444p", Source]);
        await new VideoStabilizationService().StabilizeAsync(ffmpeg, Source, Output,
            cancellationToken: TestContext.Current.CancellationToken);
        byte[] decoded = await DecodeAsync(ffmpeg, Output);
        Assert.Equal(24 * 322 * 242, decoded.Length);
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task OutputCreatedDuringExportIsNotOverwritten()
    {
        string ffmpeg = GetIntegrationExecutable();
        await RunFfmpegAsync(ffmpeg, ["-y", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=24",
            "-t", "1", "-c:v", "libx264", Source]);
        await Assert.ThrowsAsync<IOException>(() =>
            new VideoStabilizationService().StabilizeAsync(ffmpeg, Source, Output,
                new CreateOutputOnProgress(Output), TestContext.Current.CancellationToken));
        Assert.Equal("another file", File.ReadAllText(Output));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task FfmpegCancellationStopsProcessAndRemovesPartialCopy()
    {
        string ffmpeg = GetIntegrationExecutable();
        await RunFfmpegAsync(ffmpeg, ["-y", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=24",
            "-t", "30", "-c:v", "libx264", "-preset", "ultrafast", Source]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var progress = new CancelOnProgress(cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new VideoStabilizationService().StabilizeAsync(ffmpeg, Source, Output, progress, cancellation.Token));
        Assert.True(progress.WasReported);
        Assert.False(File.Exists(Output));
        AssertNoTemporaryFiles();
    }

    private static string GetIntegrationExecutable()
    {
        string? path = Environment.GetEnvironmentVariable("PHOTOLIBRARIAN_TEST_FFMPEG");
        Assert.SkipWhen(string.IsNullOrEmpty(path), "Set PHOTOLIBRARIAN_TEST_FFMPEG to run FFmpeg integration checks.");
        return path!;
    }

    private void AssertNoTemporaryFiles() =>
        Assert.Empty(Directory.GetFiles(_directory, ".photolibrarian-*"));

    private static async Task<byte[]> DecodeAsync(string executable, string path) =>
        await RunFfmpegAsync(executable, ["-i", path, "-map", "0:v:0", "-pix_fmt", "gray", "-f", "rawvideo", "pipe:1"]);

    private static double TemporalDifference(byte[] frames)
    {
        const int width = 320, height = 240, size = width * height;
        long difference = 0;
        for (int frame = 1; frame < frames.Length / size; frame++)
            for (int y = 32; y < height - 32; y++)
                for (int x = 32; x < width - 32; x++)
                {
                    int index = frame * size + y * width + x;
                    difference += Math.Abs(frames[index] - frames[index - size]);
                }
        return difference;
    }

    private static async Task<byte[]> RunFfmpegAsync(string executable, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        info.ArgumentList.Add("-hide_banner");
        info.ArgumentList.Add("-loglevel");
        info.ArgumentList.Add("error");
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var errors = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var output = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(output, TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, await errors);
        return output.ToArray();
    }

    private sealed class CancelOnProgress(CancellationTokenSource cancellation) : IProgress<string>
    {
        public bool WasReported { get; private set; }
        public void Report(string value)
        {
            WasReported = true;
            cancellation.Cancel();
        }
    }

    private sealed class CreateOutputOnProgress(string output) : IProgress<string>
    {
        public void Report(string value) => File.WriteAllText(output, "another file");
    }
}
