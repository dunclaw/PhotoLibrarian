// Copyright (c) Duncan Lawler. All Rights Reserved.

using System.Reflection;
using PhotoLibrarian.ML.Services;
using PhotoLibrarian.ViewModels;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class PeopleReviewViewModelTests
{
    [Fact]
    public void PeopleReviewOpenFailureClosesReviewAndUsesSharedErrorDialog()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "TestAssets", "MainWindow.xaml.cs"));
        var start = source.IndexOf("private async void OnPeopleReviewClick(", StringComparison.Ordinal);
        var end = source.IndexOf("private async void OnDuplicateReviewClick(", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var handler = source[start..end];
        var close = handler.IndexOf("ViewModel.PeopleReview.Close();", StringComparison.Ordinal);
        var error = handler.IndexOf("DialogService.Instance.ErrorAsync(", StringComparison.Ordinal);
        Assert.True(close >= 0 && error > close);
        Assert.Contains("MainLayout, \"People review couldn't be opened\", ex, \"Close\"", handler);
        Assert.DoesNotContain("new ContentDialog", handler);
    }

    [Theory]
    [InlineData("OnHidePersonClick", "OnRenamePersonClick", "Hide suggestions", "HideCurrentPersonAsync")]
    [InlineData("OnDeletePersonClick", "OnRestoreFacesClick", "Delete person", "DeletePersonAsync")]
    public void PeopleConfirmationsUseSharedDestructiveDialogAndOnlyPrimaryProceeds(
        string method, string nextMethod, string actionLabel, string action)
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "TestAssets", "PeopleReviewView.xaml.cs"));
        var start = source.IndexOf($"void {method}(", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf($"void {nextMethod}(", start, StringComparison.Ordinal);
        Assert.True(end > start);
        var confirmation = source[start..end];
        Assert.Contains("DialogService.Instance.ConfirmDestructiveAsync(", confirmation);
        Assert.Contains($"\"{actionLabel}\"", confirmation);
        Assert.Contains("\"Cancel\"", confirmation);
        Assert.Contains("if (result != ContentDialogResult.Primary) return;", confirmation);
        Assert.Contains(action, confirmation);
        Assert.DoesNotContain("new ContentDialog", confirmation);
    }

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
