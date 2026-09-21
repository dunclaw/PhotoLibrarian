using System.Reflection;
using PhotoLibrarian.ML.Services;
using PhotoLibrarian.ViewModels;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class PeopleReviewViewModelTests
{
    [Fact]
    public void ResolveGroupAfterRefresh_PrefersTheGroupWithTheCurrentFaceSelection()
    {
        var viewModel = new PeopleReviewViewModel(null!);

        var aliceFace = new FaceSuggestion(
            101,
            1,
            @"C:\Photos\alice.jpg",
            0.1,
            0.1,
            0.2,
            0.2,
            0.90f,
            []);
        var bobFace = new FaceSuggestion(
            202,
            2,
            @"C:\Photos\bob.jpg",
            0.2,
            0.2,
            0.2,
            0.2,
            0.95f,
            []);
        var aliceGroup = new FaceSuggestionGroup(
            "person:1",
            1,
            "Alice",
            0,
            [aliceFace]);
        var bobGroup = new FaceSuggestionGroup(
            "person:2",
            2,
            "Bob",
            0,
            [bobFace]);
        var aliceView = new FaceSuggestionGroupViewModel(aliceGroup);
        var bobView = new FaceSuggestionGroupViewModel(bobGroup);
        viewModel.Groups.Add(aliceView);
        viewModel.Groups.Add(bobView);

        var method = typeof(PeopleReviewViewModel)
            .GetMethod("ResolveGroupAfterRefresh", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = (FaceSuggestionGroupViewModel?)method.Invoke(
            viewModel,
            [
                null,
                0,
                new HashSet<long> { bobFace.FaceRegionId }
            ]);

        Assert.Same(bobView, result);
    }
}
