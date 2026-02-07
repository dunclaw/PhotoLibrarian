using Microsoft.UI.Xaml;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.ViewModels;

namespace PhotoLibrarian;

public partial class App : Application
{
    private Window? _window;

    public static MainViewModel ViewModel { get; private set; } = null!;

    public App()
    {
        this.InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Set up data directory
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dataDir = Path.Combine(appData, "PhotoLibrarian");
        Directory.CreateDirectory(dataDir);
        var dbPath = Path.Combine(dataDir, "cache.db");

        // Create services
        var db = new CacheDatabase(dbPath);
        var imageRepo = new ImageRepository(db);
        var thumbRepo = new ThumbnailRepository(db);
        var scanner = new FolderScannerService();
        var metadataReader = new MetadataReaderService();
        var thumbnailService = new ThumbnailService(thumbRepo);
        var indexingService = new LibraryIndexingService(db, imageRepo, thumbRepo, scanner, metadataReader, thumbnailService);

        ViewModel = new MainViewModel(db, imageRepo, thumbRepo, scanner, metadataReader, thumbnailService, indexingService);

        _window = new MainWindow();
        _window.Activate();

        await ViewModel.InitializeAsync();
    }

    public static new App Current => (App)Application.Current;
    public static Window? MainWindow => ((App)Application.Current)._window;
}
