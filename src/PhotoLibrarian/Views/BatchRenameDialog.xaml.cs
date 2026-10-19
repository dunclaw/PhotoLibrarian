using Microsoft.UI.Xaml.Controls;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;

namespace PhotoLibrarian.Views;

public sealed partial class BatchRenameDialog : ContentDialog
{
    private readonly IReadOnlyList<ImageEntry> _entries;

    public IReadOnlyList<RenamePreview> Preview { get; private set; } = [];

    public BatchRenameDialog(IReadOnlyList<ImageEntry> entries)
    {
        _entries = entries;
        InitializeComponent();
        Title = $"Rename {entries.Count} files";
        PatternBox.TextChanged += (_, _) => UpdatePreview();
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        Preview = BatchRenameService.Preview(_entries, PatternBox.Text);
        PreviewList.ItemsSource = Preview;
        var errors = Preview.Count(p => p.Error is not null);
        var changed = Preview.Count(p => !string.Equals(
            p.SourcePath, p.DestinationPath, StringComparison.OrdinalIgnoreCase));
        IsPrimaryButtonEnabled = errors == 0 && changed > 0;
        StatusText.Text = errors > 0
            ? $"{errors} file(s) have errors. Change the pattern to continue."
            : $"{changed} file(s) will be renamed, with their XMP sidecars if present.";
    }
}
