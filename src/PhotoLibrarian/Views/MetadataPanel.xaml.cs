using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PhotoLibrarian.ViewModels;

namespace PhotoLibrarian.Views;

public sealed partial class MetadataPanel : UserControl
{
    private MetadataPanelViewModel? ViewModel => App.ViewModel?.MetadataPanel;

    // Star glyph codes
    private const string StarFilled = "\uE1CF";  // FavoriteStar filled
    private const string StarEmpty = "\uE1CE";    // FavoriteStar empty

    public MetadataPanel()
    {
        this.InitializeComponent();
        this.Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        
        // Bind tags list to ViewModel's Tags collection
        TagsList.ItemsSource = ViewModel.Tags;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MetadataPanelViewModel.HasImage):
            case nameof(MetadataPanelViewModel.FileName):
                DispatcherQueue.TryEnqueue(UpdateDisplay);
                break;
            case nameof(MetadataPanelViewModel.Rating):
                DispatcherQueue.TryEnqueue(UpdateStars);
                break;
        }
    }

    private void UpdateDisplay()
    {
        if (ViewModel is null || !ViewModel.HasImage)
        {
            EmptyContent.Visibility = Visibility.Visible;
            DetailContent.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyContent.Visibility = Visibility.Collapsed;
        DetailContent.Visibility = Visibility.Visible;

        // Caption
        CaptionBox.Text = ViewModel.Caption;

        // Geotag
        if (!string.IsNullOrEmpty(ViewModel.GpsLatitude))
        {
            GeotagText.Text = $"{ViewModel.GpsLatitude}, {ViewModel.GpsLongitude}";
        }
        else
        {
            GeotagText.Text = "Add geotag";
        }

        // Information section
        InfoFileName.Text = ViewModel.FileName;
        InfoDateTaken.Text = !string.IsNullOrEmpty(ViewModel.DateTaken) ? ViewModel.DateTaken : "—";
        InfoFileSize.Text = ViewModel.FileSize;
        InfoDimensions.Text = !string.IsNullOrEmpty(ViewModel.Dimensions) ? ViewModel.Dimensions : "—";
        InfoCamera.Text = !string.IsNullOrEmpty(ViewModel.Camera) ? ViewModel.Camera : "—";
        InfoAuthor.Text = !string.IsNullOrEmpty(ViewModel.Author) ? ViewModel.Author : "Add an author";
        InfoExposure.Text = ViewModel.Exposure;
        InfoAperture.Text = ViewModel.Aperture;
        InfoFocalLength.Text = ViewModel.FocalLength;
        InfoIso.Text = ViewModel.Iso;
        InfoLatitude.Text = ViewModel.GpsLatitude;
        InfoLongitude.Text = ViewModel.GpsLongitude;
        InfoFilePath.Text = ViewModel.FilePath;

        // Hide rows with no data
        InfoCameraRow.Visibility = string.IsNullOrEmpty(ViewModel.Camera) ? Visibility.Collapsed : Visibility.Visible;
        InfoExposureRow.Visibility = string.IsNullOrEmpty(ViewModel.Exposure) ? Visibility.Collapsed : Visibility.Visible;
        InfoApertureRow.Visibility = string.IsNullOrEmpty(ViewModel.Aperture) ? Visibility.Collapsed : Visibility.Visible;
        InfoFocalLengthRow.Visibility = string.IsNullOrEmpty(ViewModel.FocalLength) ? Visibility.Collapsed : Visibility.Visible;
        InfoIsoRow.Visibility = string.IsNullOrEmpty(ViewModel.Iso) ? Visibility.Collapsed : Visibility.Visible;
        InfoLatRow.Visibility = string.IsNullOrEmpty(ViewModel.GpsLatitude) ? Visibility.Collapsed : Visibility.Visible;
        InfoLonRow.Visibility = string.IsNullOrEmpty(ViewModel.GpsLongitude) ? Visibility.Collapsed : Visibility.Visible;
        InfoDimensionsRow.Visibility = string.IsNullOrEmpty(ViewModel.Dimensions) ? Visibility.Collapsed : Visibility.Visible;

        UpdateStars();
    }

    // --- Star Rating ---

    private void UpdateStars()
    {
        if (ViewModel is null) return;
        int rating = ViewModel.Rating;
        Star1Icon.Glyph = rating >= 1 ? StarFilled : StarEmpty;
        Star2Icon.Glyph = rating >= 2 ? StarFilled : StarEmpty;
        Star3Icon.Glyph = rating >= 3 ? StarFilled : StarEmpty;
        Star4Icon.Glyph = rating >= 4 ? StarFilled : StarEmpty;
        Star5Icon.Glyph = rating >= 5 ? StarFilled : StarEmpty;

        // Color filled stars gold
        var goldBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 185, 0));
        var grayBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray);
        Star1Icon.Foreground = rating >= 1 ? goldBrush : grayBrush;
        Star2Icon.Foreground = rating >= 2 ? goldBrush : grayBrush;
        Star3Icon.Foreground = rating >= 3 ? goldBrush : grayBrush;
        Star4Icon.Foreground = rating >= 4 ? goldBrush : grayBrush;
        Star5Icon.Foreground = rating >= 5 ? goldBrush : grayBrush;
    }

    private void OnStarClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || sender is not Button btn || btn.Tag is not string tagStr) return;
        if (int.TryParse(tagStr, out int star))
        {
            ViewModel.SetRatingCommand.Execute(star);
        }
    }

    // --- Caption ---

    private void OnCaptionLostFocus(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.Caption = CaptionBox.Text;
        ViewModel.SaveCaptionCommand.Execute(null);
    }

    // --- Tags ---

    private void OnAddTagClick(object sender, RoutedEventArgs e)
    {
        AddCurrentTag();
    }

    private void OnNewTagKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            AddCurrentTag();
            e.Handled = true;
        }
    }

    private void AddCurrentTag()
    {
        if (ViewModel is null) return;
        var tag = NewTagBox.Text?.Trim();
        if (string.IsNullOrEmpty(tag)) return;
        
        ViewModel.AddTagCommand.Execute(tag);
        NewTagBox.Text = "";
    }

    private void OnRemoveTagClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || sender is not Button btn) return;
        var tag = btn.Tag as string;
        if (!string.IsNullOrEmpty(tag))
        {
            ViewModel.RemoveTagCommand.Execute(tag);
        }
    }

    // Keep DetailGrid MinHeight in sync with ScrollViewer viewport
    // so the * spacer row pushes the info block to the bottom.
    private void OnDetailScrollViewerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        DetailGrid.MinHeight = e.NewSize.Height;
    }
}
