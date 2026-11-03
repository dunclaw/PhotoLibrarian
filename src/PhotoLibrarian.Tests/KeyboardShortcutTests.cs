// Copyright (c) Duncan Lawler. All Rights Reserved.

using System.Xml.Linq;
using Windows.System;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class KeyboardShortcutTests
{
    [Theory]
    [InlineData(VirtualKey.Number0, VirtualKeyModifiers.None, 0)]
    [InlineData(VirtualKey.Number1, VirtualKeyModifiers.None, 1)]
    [InlineData(VirtualKey.Number2, VirtualKeyModifiers.None, 2)]
    [InlineData(VirtualKey.Number3, VirtualKeyModifiers.None, 3)]
    [InlineData(VirtualKey.Number4, VirtualKeyModifiers.None, 4)]
    [InlineData(VirtualKey.Number5, VirtualKeyModifiers.None, 5)]
    [InlineData(VirtualKey.NumberPad0, VirtualKeyModifiers.None, 0)]
    [InlineData(VirtualKey.NumberPad5, VirtualKeyModifiers.None, 5)]
    [InlineData(VirtualKey.Number1, VirtualKeyModifiers.Control, 1)]
    [InlineData(VirtualKey.Number5, VirtualKeyModifiers.Control, 5)]
    public void RatingShortcuts_MapDigitsToExactRatings(
        VirtualKey key,
        VirtualKeyModifiers modifiers,
        int expectedRating)
    {
        Assert.True(MainWindow.TryGetRatingShortcut(key, modifiers, out var rating));
        Assert.Equal(expectedRating, rating);
    }

    [Theory]
    [InlineData(VirtualKey.Number0, VirtualKeyModifiers.Control)]
    [InlineData(VirtualKey.Number1, VirtualKeyModifiers.Shift)]
    [InlineData(VirtualKey.Number1, VirtualKeyModifiers.Menu)]
    [InlineData(VirtualKey.Number1, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift)]
    [InlineData(VirtualKey.A, VirtualKeyModifiers.None)]
    public void RatingShortcuts_RejectZoomAndModifiedInputKeys(
        VirtualKey key,
        VirtualKeyModifiers modifiers)
    {
        Assert.False(MainWindow.TryGetRatingShortcut(key, modifiers, out _));
    }

    [Theory]
    [InlineData(VirtualKey.Z, true)]
    [InlineData(VirtualKey.Y, false)]
    public void EditorHistoryShortcuts_MapOnlyWhenNotTyping(VirtualKey key, bool expectedUndo)
    {
        Assert.True(MainWindow.TryGetEditorHistoryShortcut(
            key, VirtualKeyModifiers.Control, isInputFocused: false, out var isUndo));
        Assert.Equal(expectedUndo, isUndo);

        Assert.False(MainWindow.TryGetEditorHistoryShortcut(
            key, VirtualKeyModifiers.Control, isInputFocused: true, out _));
        Assert.False(MainWindow.TryGetEditorHistoryShortcut(
            key, VirtualKeyModifiers.None, isInputFocused: false, out _));
    }

    [Fact]
    public void ExistingWindowAcceleratorsRemainTheOnlyF11AndPrintBindings()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestAssets", "MainWindow.xaml");
        var accelerators = XDocument.Load(path)
            .Descendants()
            .Where(element => element.Name.LocalName == "KeyboardAccelerator")
            .ToArray();

        Assert.Contains(accelerators, element =>
            (string?)element.Attribute("Key") == "F11" &&
            (string?)element.Attribute("Invoked") == "OnSlideshowInvoked");
        Assert.Contains(accelerators, element =>
            (string?)element.Attribute("Key") == "P" &&
            (string?)element.Attribute("Modifiers") == "Control" &&
            (string?)element.Attribute("Invoked") == "OnPrintPhotosInvoked");
        Assert.DoesNotContain(accelerators, element =>
            (string?)element.Attribute("Key") is "Delete" or "F2" or "R" or "L" or "T");
    }

    [Fact]
    public void PreviewRouterGuardsInputAndModalContextsBeforePhotoActions()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestAssets", "MainWindow.xaml.cs");
        var source = File.ReadAllText(path);
        var routerStart = source.IndexOf(
            "private async Task<bool> HandlePhotoLibraryShortcutAsync",
            StringComparison.Ordinal);
        var routerEnd = source.IndexOf(
            "private bool CanRunPhotoLibraryShortcut",
            routerStart,
            StringComparison.Ordinal);

        Assert.True(routerStart >= 0);
        Assert.True(routerEnd > routerStart);
        var router = source[routerStart..routerEnd];
        Assert.Contains("IsInputOrDialogFocused(focused)", router);
        Assert.Contains("CanRunPhotoLibraryShortcut()", router);
        Assert.Contains("DeleteSelectedAsync()", router);
        Assert.Contains("RenameSelectedAsync()", router);
        Assert.Contains("SetRatingFromShortcutAsync(rating)", router);
    }
}
