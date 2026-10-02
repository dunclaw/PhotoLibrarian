using PhotoLibrarian.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class AutoEnhanceAnalyzerTests
{
    [Fact]
    public void Analyze_AdaptsExposureAndTonalControlsToImageBrightness()
    {
        var dark = SolidImage(16, 16, 16);
        var bright = SolidImage(240, 240, 240);

        var darkAdjustments = AutoEnhanceAnalyzer.Analyze(dark);
        var brightAdjustments = AutoEnhanceAnalyzer.Analyze(bright);

        Assert.True(darkAdjustments.Exposure > 0);
        Assert.True(darkAdjustments.Shadows > 0);
        Assert.True(brightAdjustments.Exposure < 0);
        Assert.True(brightAdjustments.Highlights < 0);
    }

    [Fact]
    public void Analyze_IncreasesLocalContrastForLowContrastImages()
    {
        var lowContrast = Pixels((byte)96, (byte)112, (byte)128, (byte)144);
        var fullRange = Pixels((byte)0, (byte)64, (byte)192, (byte)255);

        var lowContrastAdjustments = AutoEnhanceAnalyzer.Analyze(lowContrast);
        var fullRangeAdjustments = AutoEnhanceAnalyzer.Analyze(fullRange);
        var smoothAdjustments = AutoEnhanceAnalyzer.Analyze(Pixels(128, 128, 128, 128));
        var texturedAdjustments = AutoEnhanceAnalyzer.Analyze(Pixels(0, 255, 0, 255));

        Assert.True(lowContrastAdjustments.Contrast > fullRangeAdjustments.Contrast);
        Assert.True(smoothAdjustments.Clarity > texturedAdjustments.Clarity);
    }

    [Fact]
    public void Analyze_DoesNotAddColorToGrayscaleAndIsDeterministic()
    {
        var grayscale = Pixels((byte)32, (byte)96, (byte)160, (byte)224);

        var first = AutoEnhanceAnalyzer.Analyze(grayscale);
        var second = AutoEnhanceAnalyzer.Analyze(grayscale);

        Assert.Equal(0, first.Saturation);
        Assert.Equal(first.Exposure, second.Exposure);
        Assert.Equal(first.Contrast, second.Contrast);
        Assert.Equal(first.Highlights, second.Highlights);
        Assert.Equal(first.Shadows, second.Shadows);
        Assert.Equal(first.Clarity, second.Clarity);
    }

    [Fact]
    public void Analyze_RejectsIncompletePixelData()
    {
        Assert.Throws<ArgumentException>(() => AutoEnhanceAnalyzer.Analyze([0, 0, 0]));
    }

    private static byte[] SolidImage(byte red, byte green, byte blue) =>
        [blue, green, red, 255];

    private static byte[] Pixels(params byte[] grays)
    {
        var pixels = new byte[grays.Length * 4];
        for (var i = 0; i < grays.Length; i++)
        {
            pixels[i * 4] = grays[i];
            pixels[i * 4 + 1] = grays[i];
            pixels[i * 4 + 2] = grays[i];
            pixels[i * 4 + 3] = 255;
        }

        return pixels;
    }
}
