using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace PhotoLibrarian.Services;

/// <summary>
/// Per-photo file operations driven by the grid context menu and drag-drop:
/// open in viewer, reveal in Explorer, set as wallpaper, rotate, copy, delete (recycle bin),
/// rename, properties dialog. Each method is selection-aware (takes a list of paths/entries).
/// </summary>
public sealed class PhotoOperationsService
{
    private readonly ImageRepository _imageRepo;

    public PhotoOperationsService(ImageRepository imageRepo)
    {
        _imageRepo = imageRepo;
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
    //  Rotation (EXIF orientation — lossless, fast, no re-encode)
    // -----------------------------------------------------------------

    /// <summary>
    /// Rotates the image by updating its EXIF orientation tag. No pixel changes — fully
    /// lossless. Apps that honor EXIF orientation (Explorer, Photo Gallery, browsers) display
    /// the new orientation; the raw pixels remain unchanged.
    /// </summary>
    /// <param name="clockwise">true = rotate 90° clockwise, false = 90° counter-clockwise.</param>
    public async Task RotateAsync(ImageEntry entry, bool clockwise)
    {
        int current = entry.Orientation <= 0 ? 1 : entry.Orientation;
        int next = NextOrientation(current, clockwise);

        if (EmbeddedMetadataWriter.IsSupported(entry.FilePath))
        {
            try
            {
                await EmbeddedMetadataWriter.WriteAsync(entry.FilePath, orientation: (ushort)next);
                entry.Orientation = next;
                if (entry.Id > 0) await _imageRepo.UpdateOrientationAsync(entry.Id, next);
                return;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[OPS] Rotate failed for {entry.FilePath}: {ex.Message}");
            }
        }
    }

    /// <summary>Cycles EXIF orientation through the 4 right-angle states.</summary>
    private static int NextOrientation(int current, bool clockwise)
    {
        // The 4 right-angle EXIF orientations in CW order: 1 → 6 → 3 → 8 → 1
        int[] cw = { 1, 6, 3, 8 };
        int idx = Array.IndexOf(cw, current);
        if (idx < 0) idx = 0; // treat unknown/mirrored as normal
        idx = clockwise ? (idx + 1) % 4 : (idx + 3) % 4;
        return cw[idx];
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
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    entry.FilePath,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                    Microsoft.VisualBasic.FileIO.UICancelOption.DoNothing);

                // Also delete any sidecar
                var sidecar = Path.ChangeExtension(entry.FilePath, ".xmp");
                if (File.Exists(sidecar))
                {
                    try
                    {
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                            sidecar,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                            Microsoft.VisualBasic.FileIO.UICancelOption.DoNothing);
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
            if (string.IsNullOrWhiteSpace(newName)) return null;

            var dir = Path.GetDirectoryName(entry.FilePath) ?? "";
            // If the user didn't include an extension, keep the original
            if (!Path.HasExtension(newName))
                newName += Path.GetExtension(entry.FilePath);

            var newPath = Path.Combine(dir, newName);
            if (string.Equals(newPath, entry.FilePath, StringComparison.OrdinalIgnoreCase)) return entry.FilePath;
            if (File.Exists(newPath)) return null; // conflict — caller can show error

            File.Move(entry.FilePath, newPath);

            // Move sidecar if present
            var oldSidecar = Path.ChangeExtension(entry.FilePath, ".xmp");
            var newSidecar = Path.ChangeExtension(newPath, ".xmp");
            if (File.Exists(oldSidecar) && !File.Exists(newSidecar))
            {
                try { File.Move(oldSidecar, newSidecar); } catch { }
            }

            // Update DB
            var oldPath = entry.FilePath;
            entry.FilePath = newPath;
            entry.FileName = Path.GetFileName(newPath);
            if (entry.Id > 0) await _imageRepo.UpdatePathAsync(entry.Id, newPath);

            return newPath;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OPS] Rename failed for {entry.FilePath}: {ex.Message}");
            return null;
        }
    }

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
