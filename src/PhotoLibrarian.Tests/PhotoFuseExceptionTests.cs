// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

// FirstChanceException is process-wide, so this test must not overlap other test collections.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PhotoFuseExceptionCollection
{
    public const string Name = "Photo Fuse first-chance exception observation";
}

[Collection(PhotoFuseExceptionCollection.Name)]
public sealed class PhotoFuseExceptionTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ExpectedRejectionsReturnReasonsWithoutThrowingAndReleaseSourceLeases()
    {
        const int size = 384;
        using var fixture = new PhotoFuseTests(output);
        var first = await fixture.WriteFrameAsync("base.png", PhotoFuseTests.Frame());
        var flat = Enumerable.Repeat((byte)128, size * size * 4).ToArray();
        for (int i = 3; i < flat.Length; i += 4) flat[i] = 255;
        var second = await fixture.WriteFrameAsync("flat.png", flat);
        int thrown = 0;
        void CountRejection(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
        {
            if (args.Exception is InvalidDataException or NotSupportedException)
                Interlocked.Increment(ref thrown);
        }
        PhotoFuseSession.Preparation preparation;
        PhotoFuseComposer.Rendering rendering;
        AppDomain.CurrentDomain.FirstChanceException += CountRejection;
        try
        {
            var token = TestContext.Current.CancellationToken;
            preparation = await PhotoFuseSession.TryPrepareAsync([first, second], token);
            rendering = PhotoFuseComposer.TryRender([PhotoFuseTests.Frame(), PhotoFuseTests.Frame(changed: 1)],
                [default, default], size, size, [new(1, 76, 80, 24, 24)], token);
            var unsupported = await PhotoFuseSession.TryPrepareAsync(["unsupported.dng"], token);
            Assert.Null(unsupported.Session);
            Assert.NotNull(unsupported.Rejection);
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= CountRejection; }
        Assert.Equal(0, thrown);
        Assert.Null(preparation.Session);
        Assert.NotNull(preparation.Rejection);
        Assert.Null(rendering.Pixels);
        Assert.NotNull(rendering.Rejection);
        using (File.Open(first, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        using (File.Open(second, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    }
}
