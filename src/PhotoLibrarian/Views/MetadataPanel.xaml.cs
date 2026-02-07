using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoLibrarian.ViewModels;

namespace PhotoLibrarian.Views;

public sealed partial class MetadataPanel : UserControl
{
    private MetadataPanelViewModel? ViewModel => App.ViewModel?.MetadataPanel;

    public MetadataPanel()
    {
        this.InitializeComponent();
        this.Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MetadataPanelViewModel.HasImage) &&
            e.PropertyName != nameof(MetadataPanelViewModel.FileName))
            return;

        DispatcherQueue.TryEnqueue(() => UpdateDisplay());
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

        FileNameText.Text = ViewModel.FileName;
        MediaTypeText.Text = ViewModel.MediaType;
        DateTakenText.Text = ViewModel.DateTaken;
        DimensionsText.Text = ViewModel.Dimensions;
        FileSizeText.Text = ViewModel.FileSize;
        CameraText.Text = ViewModel.Camera;
        LensText.Text = ViewModel.Lens;
        ApertureText.Text = ViewModel.Aperture;
        ExposureText.Text = ViewModel.Exposure;
        FocalLengthText.Text = ViewModel.FocalLength;
        IsoText.Text = ViewModel.Iso;
        FilePathText.Text = ViewModel.FilePath;

        CameraSection.Visibility = string.IsNullOrEmpty(ViewModel.Camera)
            ? Visibility.Collapsed : Visibility.Visible;
        ShootingSection.Visibility = string.IsNullOrEmpty(ViewModel.Aperture) && string.IsNullOrEmpty(ViewModel.Iso)
            ? Visibility.Collapsed : Visibility.Visible;

        if (!string.IsNullOrEmpty(ViewModel.GpsLocation))
        {
            GpsSection.Visibility = Visibility.Visible;
            GpsText.Text = ViewModel.GpsLocation;
        }
        else
        {
            GpsSection.Visibility = Visibility.Collapsed;
        }
    }
}
