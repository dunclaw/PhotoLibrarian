// Copyright (c) Duncan Lawler. All Rights Reserved.
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.ML.Services;
using PhotoLibrarian.Services;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;

namespace PhotoLibrarian.ViewModels;

public partial class AutoTagReviewTagViewModel : ObservableObject
{
    [ObservableProperty]
    public partial bool IsEditable { get; set; } = true;

    [ObservableProperty]
    public partial bool IsAccepted { get; set; } = true;

    public string Tag { get; }
    public string DisplayText => AutoTagReviewStrings.Format(
        "AutoTagReviewSuggestion",
        Tag,
        Confidence.ToString("P0", CultureInfo.CurrentCulture));
    public string AutomationName => AutoTagReviewStrings.Format(
        "AutoTagReviewSuggestionAutomationName",
        Tag,
        Confidence.ToString("P0", CultureInfo.CurrentCulture));
    public float Confidence { get; }

    public AutoTagReviewTagViewModel(TagPrediction prediction)
    {
        Tag = prediction.Tag;
        Confidence = prediction.Confidence;
    }
}

public sealed class AutoTagReviewPhotoViewModel : ObservableObject
{
    private string _errorMessage = "";
    private bool _canRetrySave;
    private bool _isCommitted;
    private bool _areTagsCommitted;

    public ImageEntry Image { get; }
    public string FileName => Image.FileName;
    public string FilePath => Image.FilePath;
    public ObservableCollection<AutoTagReviewTagViewModel> Suggestions { get; }
    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public bool HasSuggestions => Suggestions.Count > 0;

    public bool IsCommitted
    {
        get => _isCommitted;
        set
        {
            if (!SetProperty(ref _isCommitted, value))
                return;

            foreach (var suggestion in Suggestions)
                suggestion.IsEditable = !value && !AreTagsCommitted;
        }
    }
    public bool CanRetrySave
    {
        get => _canRetrySave;
        private set => SetProperty(ref _canRetrySave, value);
    }
    public bool AreTagsCommitted
    {
        get => _areTagsCommitted;
        set
        {
            if (!SetProperty(ref _areTagsCommitted, value))
                return;

            foreach (var suggestion in Suggestions)
                suggestion.IsEditable = !value && !IsCommitted;
        }
    }

    public AutoTagReviewPhotoViewModel(SelectedPhotoAutoTagReviewItem item)
    {
        Image = item.Image;
        ErrorMessage = item.Error ?? "";
        Suggestions = new ObservableCollection<AutoTagReviewTagViewModel>(
            item.Predictions.Select(
                prediction => new AutoTagReviewTagViewModel(prediction)));
    }

    public void SetError(string message, bool canRetrySave = false)
    {
        ErrorMessage = message;
        CanRetrySave = canRetrySave;
        foreach (var suggestion in Suggestions)
            suggestion.IsEditable =
                canRetrySave && !AreTagsCommitted && !IsCommitted;
        OnPropertyChanged(nameof(HasError));
    }

    public void ClearSaveError()
    {
        if (!CanRetrySave)
            return;

        ErrorMessage = "";
        CanRetrySave = false;
        foreach (var suggestion in Suggestions)
            suggestion.IsEditable = !AreTagsCommitted && !IsCommitted;
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(HasError));
    }
}

public partial class AutoTagReviewViewModel : ObservableObject
{
    private readonly SelectedPhotoAutoTagReviewProcessor _processor;
    private readonly TagRepository _tagRepository;
    private readonly Func<AutoTaggingSettings> _settingsProvider;
    private CancellationTokenSource? _generationCancellation;
    private Task<SelectedPhotoAutoTagReviewResult>? _generationTask;
    private TaskCompletionSource? _applyCompletion;
    private Func<Task>? _beforeOpenAsync;
    private Action? _afterClose;
    private Func<Task>? _afterApplyAsync;
    private string? _scanVersion;
    private bool _recognitionSuspended;

    public ObservableCollection<AutoTagReviewPhotoViewModel> Photos { get; } = [];
    public event Action<string>? StatusUpdated;

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsApplying { get; set; }

    [ObservableProperty]
    public partial int ProgressValue { get; set; }

    [ObservableProperty]
    public partial int ProgressMaximum { get; set; } = 1;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } =
        AutoTagReviewStrings.Get("AutoTagReviewInitialStatus");

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = "";

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage) ||
        Photos.Any(photo => photo.HasError);

    public bool CanApply =>
        !IsBusy &&
        !IsApplying &&
        Photos.Any(photo =>
            (!photo.HasError || photo.CanRetrySave) && !photo.IsCommitted);

    public bool CanClose => !IsApplying;

    public AutoTagReviewViewModel(
        SelectedPhotoAutoTagReviewProcessor processor,
        TagRepository tagRepository,
        Func<AutoTaggingSettings> settingsProvider)
    {
        _processor = processor;
        _tagRepository = tagRepository;
        _settingsProvider = settingsProvider;
        Photos.CollectionChanged += OnPhotosChanged;
    }

    public void SetRecognitionCallbacks(
        Func<Task> beforeOpenAsync,
        Action afterClose)
    {
        _beforeOpenAsync = beforeOpenAsync;
        _afterClose = afterClose;
    }

    public void SetAfterApplyCallback(Func<Task> afterApplyAsync) =>
        _afterApplyAsync = afterApplyAsync;

    public async Task OpenForImagesAsync(IReadOnlyList<ImageEntry> selectedImages)
    {
        if (IsOpen || IsBusy || IsApplying)
            return;

        var images = selectedImages
            .Where(image => image.MediaType == MediaType.Image)
            .Select(CreateSnapshot)
            .ToArray();
        if (images.Length == 0)
        {
            StatusMessage = AutoTagReviewStrings.Get("AutoTagReviewNoSelection");
            return;
        }

        IsOpen = true;
        IsBusy = true;
        _generationCancellation = new CancellationTokenSource();
        try
        {
            var settings = _settingsProvider();
            _scanVersion = AutoTagModelCatalog.ForId(settings.ProfileId).PipelineVersion;
            Photos.Clear();
            ErrorMessage = "";
            ProgressValue = 0;
            ProgressMaximum = Math.Max(1, images.Length);

            if (_beforeOpenAsync is not null)
            {
                _recognitionSuspended = true;
                await _beforeOpenAsync();
            }

            var token = _generationCancellation.Token;
            token.ThrowIfCancellationRequested();
            var progress = new Progress<SelectedPhotoAutoTagReviewProgress>(
                UpdateProgress);
            _generationTask = Task.Run(
                () => _processor.GenerateAsync(
                    images,
                    settings,
                    progress,
                    token),
                token);
            var result = await _generationTask;

            if (!IsOpen)
                return;

            foreach (var item in result.Items)
                Photos.Add(new AutoTagReviewPhotoViewModel(item));

            ProgressValue = result.Items.Count;
            StatusMessage = result.IsCanceled
                ? AutoTagReviewStrings.Format(
                    "AutoTagReviewCanceledStatus",
                    result.Items.Count,
                    images.Length)
                : AutoTagReviewStrings.Format(
                    "AutoTagReviewReadyStatus",
                    result.Items.Count,
                    Photos.Sum(photo => photo.Suggestions.Count));
            UpdateIssueSummary();
        }
        catch (OperationCanceledException)
            when (_generationCancellation?.IsCancellationRequested == true)
        {
            StatusMessage = AutoTagReviewStrings.Format(
                "AutoTagReviewCanceledStatus",
                ProgressValue,
                images.Length);
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            StatusMessage = AutoTagReviewStrings.Get(
                "AutoTagReviewGenerationFailed");
        }
        finally
        {
            _generationTask = null;
            _generationCancellation?.Dispose();
            _generationCancellation = null;
            IsBusy = false;
            if (!IsOpen)
                ResumeRecognition();
            NotifyCanApplyChanged();
        }
    }

    [RelayCommand]
    public void CancelGeneration() => _generationCancellation?.Cancel();

    [RelayCommand]
    public void Close()
    {
        if (IsApplying)
            return;

        IsOpen = false;
        _generationCancellation?.Cancel();
        if (!IsBusy)
            ResumeRecognition();
    }

    public async Task ShutdownAsync()
    {
        _generationCancellation?.Cancel();
        var generation = _generationTask;
        if (generation is not null)
        {
            try
            {
                await generation;
            }
            catch (OperationCanceledException)
            {
            }
        }

        var apply = _applyCompletion?.Task;
        if (apply is not null)
            await apply;

        IsApplying = false;
        Close();
    }

    [RelayCommand]
    public async Task ApplyAsync()
    {
        if (!CanApply || string.IsNullOrEmpty(_scanVersion))
            return;

        _applyCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        IsApplying = true;
        ErrorMessage = "";
        var savedPhotos = 0;
        var savedTags = 0;
        var failedPhotos = 0;

        try
        {
            foreach (var photo in Photos)
            {
                if (photo.HasError || photo.IsCommitted)
                {
                    if (!photo.CanRetrySave || photo.IsCommitted)
                        continue;
                }

                photo.ClearSaveError();
                var accepted = photo.Suggestions
                    .Where(suggestion => suggestion.IsAccepted)
                    .Select(suggestion => new GeneratedImageTag(
                        suggestion.Tag,
                        suggestion.Confidence))
                    .ToArray();
                var staleMessage =
                    SelectedPhotoAutoTagReviewProcessor.ValidatePhotoSnapshot(
                        photo.Image);
                if (staleMessage is not null)
                {
                    SetPhotoError(photo, staleMessage);
                    failedPhotos++;
                    continue;
                }

                try
                {
                    var wasSaved = await _tagRepository.TryAddReviewedAutoTagsAsync(
                        photo.Image,
                        accepted,
                        _scanVersion);
                    if (!wasSaved)
                    {
                        SetPhotoError(
                            photo,
                            AutoTagReviewStrings.Get(
                                "AutoTagReviewPhotoChanged"));
                        failedPhotos++;
                        continue;
                    }

                    photo.AreTagsCommitted = true;
                    if (accepted.Length > 0)
                    {
                        var currentStaleMessage =
                            SelectedPhotoAutoTagReviewProcessor
                                .ValidatePhotoSnapshot(photo.Image);
                        if (currentStaleMessage is not null)
                        {
                            SetPhotoError(
                                photo,
                                AutoTagReviewStrings.Format(
                                    "AutoTagReviewMetadataSkipped",
                                    currentStaleMessage));
                            failedPhotos++;
                            continue;
                        }

                        var allTags = await _tagRepository.GetTagsAsync(
                            photo.Image.Id);
                        await TagWriterService.WriteTagsToSidecarAsync(
                            photo.Image.FilePath,
                            allTags.Select(tag => tag.Tag).Distinct());
                    }

                    photo.IsCommitted = true;
                    savedPhotos++;
                    savedTags += accepted.Length;
                }
                catch (Exception exception)
                {
                    SetPhotoError(
                        photo,
                        AutoTagReviewStrings.Format(
                            "AutoTagReviewSaveFailed",
                            exception.Message),
                        canRetrySave: true);
                    failedPhotos++;
                }
            }

            UpdateIssueSummary();
            StatusMessage = AutoTagReviewStrings.Format(
                "AutoTagReviewApplyStatus",
                savedTags,
                savedPhotos,
                failedPhotos);
            StatusUpdated?.Invoke(StatusMessage);
            if (savedPhotos > 0 && _afterApplyAsync is not null)
            {
                try
                {
                    await _afterApplyAsync();
                }
                catch (Exception exception)
                {
                    ErrorMessage = AutoTagReviewStrings.Format(
                        "AutoTagReviewRefreshFailed",
                        exception.Message);
                    failedPhotos++;
                }
            }
            if (failedPhotos == 0)
            {
                IsApplying = false;
                Close();
            }
        }
        finally
        {
            IsApplying = false;
            NotifyCanApplyChanged();
            _applyCompletion.TrySetResult();
            _applyCompletion = null;
        }
    }

    private void UpdateProgress(SelectedPhotoAutoTagReviewProgress progress)
    {
        if (!IsOpen)
            return;

        ProgressValue = progress.Processed;
        StatusMessage = AutoTagReviewStrings.Format(
            "AutoTagReviewProgressStatus",
            progress.Processed,
            progress.Total,
            progress.CurrentFile);
    }

    private void UpdateIssueSummary()
    {
        var failedCount = Photos.Count(photo => photo.HasError);
        ErrorMessage = failedCount == 0
            ? ""
            : AutoTagReviewStrings.Format(
                "AutoTagReviewIssuesSummary",
                failedCount);
    }

    private void SetPhotoError(
        AutoTagReviewPhotoViewModel photo,
        string message,
        bool canRetrySave = false)
    {
        photo.SetError(message, canRetrySave);
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(CanApply));
    }

    private void OnPhotosChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasError));
        NotifyCanApplyChanged();
    }

    private void NotifyCanApplyChanged()
    {
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanClose));
    }

    partial void OnIsBusyChanged(bool value) => NotifyCanApplyChanged();

    partial void OnIsApplyingChanged(bool value) => NotifyCanApplyChanged();

    partial void OnErrorMessageChanged(string value) =>
        OnPropertyChanged(nameof(HasError));

    private void ResumeRecognition()
    {
        if (!_recognitionSuspended)
            return;

        _recognitionSuspended = false;
        _afterClose?.Invoke();
    }

    private static ImageEntry CreateSnapshot(ImageEntry image) => new()
    {
        Id = image.Id,
        FilePath = image.FilePath,
        FileName = image.FileName,
        FileSize = image.FileSize,
        DateModified = image.DateModified,
        DateIndexed = image.DateIndexed,
        MediaType = image.MediaType
    };
}
