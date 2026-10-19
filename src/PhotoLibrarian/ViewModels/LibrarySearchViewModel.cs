using CommunityToolkit.Mvvm.ComponentModel;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Diagnostics;

namespace PhotoLibrarian.ViewModels;

public partial class LibrarySearchViewModel(
    ImageSearchRepository repository,
    ImageGridViewModel grid,
    ImageViewerViewModel viewer) : ObservableObject
{
    private CancellationTokenSource? _suggestionsCts;
    private readonly SemaphoreSlim _submitGate = new(1, 1);

    [ObservableProperty]
    public partial IReadOnlyList<ImageEntry> Suggestions { get; set; } = [];

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = "";

    [ObservableProperty]
    public partial bool IsError { get; set; }

    public async Task UpdateSuggestionsAsync(string text)
    {
        _suggestionsCts?.Cancel();
        _suggestionsCts?.Dispose();
        var cts = _suggestionsCts = new CancellationTokenSource();
        var token = cts.Token;
        Suggestions = [];
        if (string.IsNullOrWhiteSpace(text))
        {
            await SubmitAsync("");
            return;
        }

        try
        {
            await Task.Delay(250, token);
            var matches = await Task.Run(() => repository.SearchAsync(text, 8, token), token);
            if (!token.IsCancellationRequested)
            {
                Suggestions = matches;
                IsError = false;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!token.IsCancellationRequested) ReportError(exception);
        }
    }

    public async Task SubmitAsync(string text)
    {
        _suggestionsCts?.Cancel();
        Suggestions = [];
        await _submitGate.WaitAsync();
        try
        {
            IsError = false;
            viewer.CloseCommand.Execute(null);
            await grid.ApplySearchAsync(text);
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
        finally
        {
            _submitGate.Release();
        }
    }

    private void ReportError(Exception exception)
    {
        DebugLog.WriteLine($"Library search failed: {exception}");
        ErrorMessage = $"Search failed: {exception.Message}";
        IsError = true;
    }

    public void Cleanup()
    {
        _suggestionsCts?.Cancel();
        _suggestionsCts?.Dispose();
    }
}
