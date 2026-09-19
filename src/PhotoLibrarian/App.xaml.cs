using Microsoft.UI.Xaml;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.ML.Services;
using PhotoLibrarian.ViewModels;

namespace PhotoLibrarian;

public partial class App : Application
{
    private Window? _window;

    public static MainViewModel ViewModel { get; private set; } = null!;

    /// <summary>
    /// Global logging control. Set to false to disable debug logging.
    /// </summary>
    public static bool EnableDebugLogging
    {
        get => Diagnostics.DebugLog.EnableLogging;
        set
        {
            Diagnostics.DebugLog.EnableLogging = value;
            Core.Diagnostics.DebugLog.EnableLogging = value;
        }
    }

    public App()
    {
        this.InitializeComponent();
        
        // Enable debug logging by default (set to false to disable)
        EnableDebugLogging = true;
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
        var tagRepo = new TagRepository(db);
        var faceRepo = new FaceRepository(db);
        var scanner = new FolderScannerService();
        var metadataReader = new MetadataReaderService();
        var faceMetadataStore = new FaceMetadataStore();
        var indexingService = new LibraryIndexingService(
            db,
            imageRepo,
            tagRepo,
            faceRepo,
            scanner,
            metadataReader,
            faceMetadataStore);
        var backupService = new OriginalBackupService();
        var sessionManager = new OnnxSessionManager();
        var faceDetectionService = new FaceDetectionService(sessionManager);
        var faceEmbeddingService = new FaceEmbeddingService(sessionManager);
        var faceReviewService = new FaceReviewService(
            faceRepo,
            imageRepo,
            new FaceClusteringService(),
            new FaceRecognitionService(),
            faceMetadataStore);
        var activityGate = new UserActivityGate();
        var autoTagModelManager = new AutoTagModelManager(sessionManager);
        var autoTaggingService = new AutoTaggingService(
            sessionManager,
            autoTagModelManager);
        var recognitionPipeline = new RecognitionPipeline(
            faceRepo,
            tagRepo,
            new FaceModelProvider(sessionManager),
            faceDetectionService,
            faceEmbeddingService,
            autoTagModelManager,
            autoTaggingService,
            activityGate,
            new WindowsImageDecoder());
        var autoTagBenchmarkProcessor = new AutoTagBenchmarkProcessor(
            autoTagModelManager,
            autoTaggingService);
        var autoTaggingSettingsStore = new AutoTaggingSettingsStore();

        // Note: ThumbnailRepository removed - we use Windows thumbnail cache instead
        ViewModel = new MainViewModel(
            db,
            imageRepo,
            tagRepo,
            faceRepo,
            scanner,
            metadataReader,
            indexingService,
            backupService,
            recognitionPipeline,
            faceReviewService,
            sessionManager,
            autoTaggingSettingsStore,
            autoTagModelManager,
            autoTagBenchmarkProcessor,
            activityGate);

        _window = new MainWindow();
        _window.Activate();

        await ViewModel.InitializeAsync();
    }

    public static new App Current => (App)Application.Current;
    public static Window? MainWindow => ((App)Application.Current)._window;
}
