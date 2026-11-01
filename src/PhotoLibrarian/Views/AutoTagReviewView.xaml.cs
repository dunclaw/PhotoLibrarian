// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoLibrarian.ViewModels;

namespace PhotoLibrarian.Views;

public sealed partial class AutoTagReviewView : UserControl
{
    public AutoTagReviewViewModel ViewModel => App.ViewModel.AutoTagReview;

    public AutoTagReviewView() => InitializeComponent();

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility InvertBoolToVisibility(bool value) =>
        value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility ShowEmptyState(bool isBusy, int photoCount) =>
        !isBusy && photoCount == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    public static bool IsProgressIndeterminate(
        bool isBusy,
        int progressValue) =>
        isBusy && progressValue == 0;
}
