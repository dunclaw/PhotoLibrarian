using PhotoLibrarian.Core.Models;
using PhotoLibrarian.ViewModels;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class SlideshowTests
{
    private static ImageEntry Photo(string path, MediaType type = MediaType.Image) =>
        new() { FilePath = path, FileName = path, MediaType = type };

    [Fact]
    public void NoSelectionUsesFilteredPhotosInGridOrder()
    {
        var first = Photo("first.jpg");
        var last = Photo("last.jpg");
        var sequence = new SlideshowSequence([first, Photo("video.mp4", MediaType.Video), last], []);

        Assert.Equal(2, sequence.Count);
        Assert.Same(first, sequence.Current);
        sequence.Next();
        Assert.Same(last, sequence.Current);
        sequence.Next();
        Assert.Same(first, sequence.Current);
        sequence.Previous();
        Assert.Same(last, sequence.Current);
    }

    [Fact]
    public void SelectionUsesVisiblePhotosInGridOrderNotSelectionOrder()
    {
        var first = Photo("first.jpg");
        var second = Photo("second.jpg");
        var third = Photo("third.jpg");
        var sequence = new SlideshowSequence(
            [first, second, third],
            [Photo("THIRD.JPG"), Photo("FIRST.JPG"), Photo("not-visible.jpg")]);

        Assert.Equal(2, sequence.Count);
        Assert.Same(first, sequence.Current);
        sequence.Next();
        Assert.Same(third, sequence.Current);
    }

    [Fact]
    public void ViewerStartsAtCurrentPhotoWhenItBelongsToWorkingSet()
    {
        var photos = new[] { Photo("first.jpg"), Photo("second.jpg"), Photo("third.jpg") };
        var sequence = new SlideshowSequence(photos, [], "SECOND.JPG");
        Assert.Same(photos[1], sequence.Current);
        Assert.Equal(1, sequence.Index);

        var selected = new SlideshowSequence(photos, [photos[0], photos[2]], "second.jpg");
        Assert.Same(photos[0], selected.Current);
    }

    [Fact]
    public void SelectedVideosDoNotFallBackToUnselectedPhotos()
    {
        var video = Photo("video.mp4", MediaType.Video);
        var sequence = new SlideshowSequence([Photo("photo.jpg"), video], [video]);
        Assert.Equal(0, sequence.Count);
        Assert.Null(sequence.Current);
        sequence.Next();
        sequence.Previous();
        Assert.Equal(0, sequence.Index);
    }

    [Fact]
    public void SequenceIsSnapshotAndSinglePhotoWraps()
    {
        var photos = new List<ImageEntry> { Photo("photo.jpg") };
        var sequence = new SlideshowSequence(photos, []);
        photos.Clear();
        sequence.Next();
        sequence.Previous();
        Assert.Equal(1, sequence.Count);
        Assert.Equal("photo.jpg", sequence.Current?.FileName);
    }

    [Fact]
    public void EmptySetIsSafe()
    {
        var sequence = new SlideshowSequence([], []);
        sequence.Next();
        sequence.Previous();
        Assert.Null(sequence.Current);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-10, 1)]
    [InlineData(61, 60)]
    [InlineData(3.5, 3.5)]
    [InlineData(double.NaN, 5)]
    [InlineData(double.PositiveInfinity, 5)]
    public void IntervalIsAlwaysFiniteAndWithinBounds(double input, double expected)
    {
        var vm = new SlideshowViewModel(new SlideshowSequence([], []), input);
        Assert.Equal(expected, vm.IntervalSeconds);
        vm.IntervalSeconds = input;
        Assert.Equal(expected, vm.IntervalSeconds);
    }

    [Fact]
    public async Task PauseAndCloseDoNotChangeTheWorkingSet()
    {
        var sequence = new SlideshowSequence([Photo("first.jpg"), Photo("second.jpg")], []);
        var vm = new SlideshowViewModel(sequence, 5);
        Assert.Equal("Pause", vm.PauseLabel);
        vm.TogglePauseCommand.Execute(null);
        Assert.True(vm.IsPaused);
        Assert.Equal("Resume", vm.PauseLabel);
        vm.TogglePauseCommand.Execute(null);
        Assert.False(vm.IsPaused);

        vm.Close();
        await vm.NextCommand.ExecuteAsync(null);
        await vm.PreviousCommand.ExecuteAsync(null);
        await vm.LoadAsync();
        Assert.Equal(0, sequence.Index);
        Assert.Null(vm.CurrentImage);
        Assert.False(vm.IsLoading);
    }
}
