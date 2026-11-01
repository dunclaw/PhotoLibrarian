// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace PhotoLibrarian.Services;

public interface IRecycleBinService
{
    bool RecycleFile(string filePath);
}

public sealed class WindowsRecycleBinService : IRecycleBinService
{
    public bool RecycleFile(string filePath)
    {
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
            filePath,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
            Microsoft.VisualBasic.FileIO.UICancelOption.DoNothing);
        return !File.Exists(filePath);
    }
}

public sealed class DuplicateCandidateValidationException : IOException
{
    public string ResourceKey { get; }

    public DuplicateCandidateValidationException(string resourceKey)
    {
        ResourceKey = resourceKey;
    }
}

public sealed record DuplicateRecycleFailure(
    string FilePath,
    string? ResourceKey,
    string? Message);

public sealed record DuplicateRecycleOperationResult(
    bool PhotoRecycled,
    DuplicateRecycleFailure? SidecarFailure);

public sealed record DuplicateRecycleBatchResult(
    IReadOnlyList<DuplicateFileSnapshot> RecycledFiles,
    IReadOnlyList<DuplicateRecycleFailure> Failures,
    bool WasCanceled);

/// <summary>
/// Per-photo file operations driven by the grid context menu and drag-drop:
/// open in viewer, reveal in Explorer, set as wallpaper, rotate, copy, delete (recycle bin),
/// rename, properties dialog. Each method is selection-aware (takes a list of paths/entries).
/// </summary>
public sealed class PhotoOperationsService
{
    private readonly ImageRepository _imageRepo;

    private readonly OriginalBackupService _backupService;
    private readonly IRecycleBinService _recycleBin;

    public PhotoOperationsService(
        ImageRepository imageRepo,
        OriginalBackupService backupService,
        IRecycleBinService? recycleBin = null)
    {
        _imageRepo = imageRepo;
        _backupService = backupService;
        _recycleBin = recycleBin ?? new WindowsRecycleBinService();
    }

    /// <summary>
    /// Creates a uniquely named sibling copy of an image and its XMP sidecar, if present.
    /// </summary>
    public static Task<string> MakeCopyForEditingAsync(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        return Task.Run(() =>
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("The photo to copy could not be found.", filePath);

            var directory = Path.GetDirectoryName(filePath)
                ?? throw new IOException("The photo does not have a parent folder.");
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            var extension = Path.GetExtension(filePath);
            var sourceSidecar = FaceMetadataStore.GetSidecarPathForImage(filePath);
            var hasSidecar = File.Exists(sourceSidecar);
            var sidecarIsAppended = string.Equals(
                sourceSidecar,
                $"{filePath}.xmp",
                StringComparison.OrdinalIgnoreCase);

            for (var copyNumber = 1; ; copyNumber++)
            {
                var suffix = copyNumber == 1 ? " - Copy" : $" - Copy ({copyNumber})";
                var copyPath = Path.Combine(directory, $"{fileName}{suffix}{extension}");
                var copySidecar = sidecarIsAppended
                    ? $"{copyPath}.xmp"
                    : Path.ChangeExtension(copyPath, ".xmp");

                if (File.Exists(copyPath) || File.Exists(copySidecar))
                    continue;

                try
                {
                    File.Copy(filePath, copyPath, overwrite: false);
                }
                catch (IOException) when (File.Exists(copyPath))
                {
                    continue;
                }

                try
                {
                    if (hasSidecar)
                        File.Copy(sourceSidecar, copySidecar, overwrite: false);
                    return copyPath;
                }
                catch (IOException) when (File.Exists(copySidecar))
                {
                    File.Delete(copyPath);
                }
                catch
                {
                    File.Delete(copyPath);
                    throw;
                }
            }
        });
    }

    // -----------------------------------------------------------------
    //  Open / reveal
    // -----------------------------------------------------------------

    /// <summary>Opens the file in the OS default association.</summary>
    public static async Task OpenWithDefaultAsync(string filePath)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(filePath);
            await Windows.System.Launcher.LaunchFileAsync(file);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OPS] OpenWithDefault failed: {ex.Message}");
        }
    }

    /// <summary>Opens Explorer at the file's folder with the file selected.</summary>
    public static void RevealInExplorer(string filePath)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{filePath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OPS] RevealInExplorer failed: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------
    //  Desktop background
    // -----------------------------------------------------------------

    private const int SPI_SETDESKWALLPAPER = 0x0014;
    private const int SPIF_UPDATEINIFILE = 0x01;
    private const int SPIF_SENDCHANGE = 0x02;

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int SystemParametersInfo(int uAction, int uParam, string lpvParam, int fuWinIni);

    public static bool SetAsDesktopBackground(string filePath)
    {
        try
        {
            int result = SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, filePath, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            return result != 0;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OPS] SetAsDesktopBackground failed: {ex.Message}");
            return false;
        }
    }

    // -----------------------------------------------------------------
    //  Rotation (baked pixel edit)
    // -----------------------------------------------------------------

    /// <summary>
    /// Rotates the image by rendering a quarter-turn into the pixels. The original is backed up
    /// before the first edit, and the EXIF orientation is reset because the rotation is baked.
    /// </summary>
    /// <param name="clockwise">true = rotate 90° clockwise, false = 90° counter-clockwise.</param>
    public async Task<(uint Width, uint Height)> RotateAsync(ImageEntry entry, bool clockwise)
    {
        if (!ImageEditRenderer.IsSupported(entry.FilePath))
            throw new NotSupportedException($"Editing not supported for {Path.GetExtension(entry.FilePath)}");

        await _backupService.BackupOriginalAsync(entry.FilePath);
        var size = await ImageEditRenderer.RenderRotatedAsync(entry.FilePath, clockwise);

        entry.Width = (int)size.Width;
        entry.Height = (int)size.Height;
        entry.Orientation = 1;
        if (entry.Id > 0)
        {
            var fileInfo = new FileInfo(entry.FilePath);
            await _imageRepo.UpdateDimensionsAsync(
                entry.Id,
                entry.Width,
                entry.Height,
                fileInfo.Length,
                fileInfo.LastWriteTimeUtc,
                invalidateFaceScan: true);
        }

        return size;
    }

    // -----------------------------------------------------------------
    //  Clipboard / drag-drop
    // -----------------------------------------------------------------

    /// <summary>Puts file references on the clipboard (Explorer-compatible paste).</summary>
    public static async Task CopyFilesToClipboardAsync(IEnumerable<string> filePaths)
    {
        var pkg = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        var items = new List<IStorageItem>();
        foreach (var p in filePaths)
        {
            try { items.Add(await StorageFile.GetFileFromPathAsync(p)); } catch { }
        }
        if (items.Count == 0) return;
        pkg.SetStorageItems(items);
        Clipboard.SetContent(pkg);
        Clipboard.Flush(); // ensures content survives after our app exits
    }

    /// <summary>
    /// Property-bag key used to mark drags that originated inside PhotoLibrarian, so in-app
    /// drop targets (e.g. the tags tree) can accept only our drags and not arbitrary file drops.
    /// We use <see cref="DataPackage.Properties"/> rather than <see cref="DataPackage.SetData(string, object)"/>
    /// because the property bag round-trips reliably for in-app drag/drop in WinUI 3, while
    /// SetData with custom format IDs doesn't always surface via DataView.Contains.
    /// </summary>
    public const string TagDropFormatId = "PhotoLibrarianTagDrop";

    /// <summary>Builds a DataPackage suitable for drag-out to Explorer / other apps.</summary>
    public static async Task PopulateDragDataAsync(DataPackage pkg, IEnumerable<string> filePaths)
    {
        pkg.RequestedOperation = DataPackageOperation.Copy;
        var items = new List<IStorageItem>();
        foreach (var p in filePaths)
        {
            try { items.Add(await StorageFile.GetFileFromPathAsync(p)); } catch { }
        }
        if (items.Count > 0) pkg.SetStorageItems(items);
    }

    // -----------------------------------------------------------------
    //  Delete to Recycle Bin
    // -----------------------------------------------------------------

    /// <summary>
    /// Sends files to the Recycle Bin (recoverable). Shows the Windows confirmation dialog.
    /// Returns the list of paths that were actually deleted.
    /// </summary>
    public async Task<List<string>> DeleteToRecycleBinAsync(IEnumerable<ImageEntry> entries)
    {
        var deleted = new List<string>();
        foreach (var entry in entries)
        {
            try
            {
                var sidecar =
                    FaceMetadataStore.GetSidecarPathForImage(entry.FilePath);
                if (!_recycleBin.RecycleFile(entry.FilePath))
                    continue;

                // Also delete any sidecar
                if (File.Exists(sidecar))
                {
                    try
                    {
                        _recycleBin.RecycleFile(sidecar);
                    }
                    catch { }
                }

                if (entry.Id > 0) await _imageRepo.DeleteByPathAsync(entry.FilePath);
                deleted.Add(entry.FilePath);
            }
            catch (OperationCanceledException) { /* user cancelled this file */ }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[OPS] Delete failed for {entry.FilePath}: {ex.Message}");
            }
        }
        return deleted;
    }

    /// <summary>
    /// Rechecks a duplicate candidate's scan-time identity before using the existing
    /// recoverable delete path. A changed or replaced file is never recycled.
    /// </summary>
    public async Task<DuplicateRecycleOperationResult> DeleteDuplicateCandidateToRecycleBinAsync(
        DuplicateFileSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        await ValidateDuplicateCandidateAsync(snapshot, cancellationToken);
        var sidecar = FaceMetadataStore.GetSidecarPathForImage(snapshot.Entry.FilePath);
        if (!_recycleBin.RecycleFile(snapshot.Entry.FilePath))
            return new DuplicateRecycleOperationResult(false, null);

        DuplicateRecycleFailure? sidecarFailure = null;
        if (File.Exists(sidecar))
        {
            try
            {
                if (!_recycleBin.RecycleFile(sidecar))
                {
                    sidecarFailure = new DuplicateRecycleFailure(
                        sidecar,
                        "DuplicateSidecarRecycleFailed",
                        null);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[OPS] Sidecar recycle failed for {sidecar}: {ex.Message}");
                sidecarFailure = new DuplicateRecycleFailure(sidecar, null, ex.Message);
            }
        }

        if (snapshot.Entry.Id > 0)
            await _imageRepo.DeleteByPathAsync(snapshot.Entry.FilePath);
        return new DuplicateRecycleOperationResult(true, sidecarFailure);
    }

    public async Task<DuplicateRecycleBatchResult> DeleteDuplicateCandidatesToRecycleBinAsync(
        DuplicateFileSnapshot keeper,
        IReadOnlyList<DuplicateFileSnapshot> candidates,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new DuplicateRecycleBatchResult(
                [],
                [],
                WasCanceled: true);
        }

        await using var keeperLease =
            await OpenVerifiedDuplicateCandidateAsync(keeper, cancellationToken);
        var recycled = new List<DuplicateFileSnapshot>();
        var failures = new List<DuplicateRecycleFailure>();
        var wasCanceled = false;

        foreach (var candidate in candidates)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                wasCanceled = true;
                break;
            }

            try
            {
                var operationResult = await DeleteDuplicateCandidateToRecycleBinAsync(
                    candidate,
                    cancellationToken);
                if (operationResult.PhotoRecycled)
                {
                    recycled.Add(candidate);
                    if (operationResult.SidecarFailure is not null)
                        failures.Add(operationResult.SidecarFailure);
                }
                else
                {
                    failures.Add(new DuplicateRecycleFailure(
                        candidate.Entry.FilePath,
                        "DuplicateRecycleCanceled",
                        null));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                wasCanceled = true;
                break;
            }
            catch (DuplicateCandidateValidationException ex)
            {
                failures.Add(new DuplicateRecycleFailure(
                    candidate.Entry.FilePath,
                    ex.ResourceKey,
                    null));
            }
            catch (Exception ex)
            {
                failures.Add(new DuplicateRecycleFailure(
                    candidate.Entry.FilePath,
                    null,
                    ex.Message));
            }
        }

        return new DuplicateRecycleBatchResult(recycled, failures, wasCanceled);
    }

    public async Task ValidateDuplicateCandidateAsync(
        DuplicateFileSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        await using var stream = await OpenVerifiedDuplicateCandidateAsync(
            snapshot,
            cancellationToken);
    }

    public async Task<FileStream> OpenVerifiedDuplicateCandidateAsync(
        DuplicateFileSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        if (snapshot.Sha256 is null)
            throw new DuplicateCandidateValidationException("DuplicateUnverifiedContent");

        var file = new FileInfo(snapshot.Entry.FilePath);
        if (!file.Exists ||
            file.Length != snapshot.FileSize ||
            file.LastWriteTimeUtc != snapshot.LastWriteTimeUtc)
        {
            throw new DuplicateCandidateValidationException("DuplicateFileChanged");
        }

        var stream = new FileStream(
            snapshot.Entry.FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            file.Refresh();
            if (!file.Exists ||
                file.Length != snapshot.FileSize ||
                file.LastWriteTimeUtc != snapshot.LastWriteTimeUtc)
            {
                throw new DuplicateCandidateValidationException("DuplicateFileChanged");
            }

            var currentHash = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken));
            if (!string.Equals(currentHash, snapshot.Sha256, StringComparison.Ordinal))
                throw new DuplicateCandidateValidationException("DuplicateContentChanged");

            file.Refresh();
            if (!file.Exists ||
                file.Length != snapshot.FileSize ||
                file.LastWriteTimeUtc != snapshot.LastWriteTimeUtc)
            {
                throw new DuplicateCandidateValidationException(
                    "DuplicateChangedDuringVerification");
            }

            stream.Position = 0;
            return stream;
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    // -----------------------------------------------------------------
    //  Rename
    // -----------------------------------------------------------------

    /// <summary>
    /// Renames a file on disk and updates the DB row. Returns the new path on success
    /// or null on failure (e.g., name conflict, invalid characters).
    /// </summary>
    public async Task<string?> RenameAsync(ImageEntry entry, string newName)
    {
        try
        {
            await new BatchRenameService(_imageRepo).RenameAsync(
                BatchRenameService.PreviewSingle(entry, newName));
            return entry.FilePath;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OPS] Rename failed for {entry.FilePath}: {ex.Message}");
            return null;
        }
    }

    public Task RenameBatchAsync(IReadOnlyList<RenamePreview> previews) =>
        Task.Run(() => new BatchRenameService(_imageRepo).RenameAsync(previews));

    // -----------------------------------------------------------------
    //  Windows shell Properties dialog
    // -----------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string lpVerb;
        public string lpFile;
        public string lpParameters;
        public string lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    private const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

    /// <summary>Opens the standard Windows shell Properties dialog for a file.</summary>
    public static void ShowPropertiesDialog(string filePath)
    {
        try
        {
            var info = new SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
                lpVerb = "properties",
                lpFile = filePath,
                nShow = 1,
                fMask = SEE_MASK_INVOKEIDLIST
            };
            ShellExecuteEx(ref info);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OPS] ShowPropertiesDialog failed: {ex.Message}");
        }
    }
}
