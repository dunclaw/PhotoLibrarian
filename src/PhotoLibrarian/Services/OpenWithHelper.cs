using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace PhotoLibrarian.Services;

/// <summary>
/// Enumerates recommended apps for a file extension via the Windows shell
/// (<c>SHAssocEnumHandlers</c> + <c>IAssocHandler</c>) and invokes them with
/// the user's selected files. Matches the Windows Explorer "Open with" submenu.
/// </summary>
public static class OpenWithHelper
{
    /// <summary>
    /// Returns the list of recommended handlers for the given file extension (e.g. ".jpg").
    /// </summary>
    public static List<HandlerInfo> EnumerateHandlers(string extension)
    {
        var result = new List<HandlerInfo>();
        if (string.IsNullOrEmpty(extension)) return result;

        int hr = SHAssocEnumHandlers(extension, ASSOC_FILTER.ASSOC_FILTER_RECOMMENDED, out var en);
        if (hr != 0 || en == null) return result;

        try
        {
            var buffer = new IAssocHandler[1];
            while (en.Next(1, buffer, out uint fetched) == 0 && fetched == 1)
            {
                var h = buffer[0];
                try
                {
                    h.GetName(out string? exePath);
                    h.GetUIName(out string? uiName);
                    h.GetIconLocation(out string? iconPath, out int iconIdx);
                    if (string.IsNullOrWhiteSpace(uiName) && !string.IsNullOrEmpty(exePath))
                        uiName = System.IO.Path.GetFileNameWithoutExtension(exePath);
                    result.Add(new HandlerInfo
                    {
                        UIName = uiName ?? "(Unknown)",
                        ExecutablePath = exePath,
                        IconPath = iconPath,
                        IconIndex = iconIdx,
                        Handler = h
                    });
                }
                catch
                {
                    if (h != null) Marshal.ReleaseComObject(h);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(en);
        }

        return result;
    }

    /// <summary>
    /// Invokes a handler with one or more files. Builds a shell IDataObject of the file
    /// PIDLs (the format <c>IAssocHandler.Invoke</c> expects).
    /// </summary>
    public static void Invoke(HandlerInfo handler, IReadOnlyList<string> filePaths)
    {
        if (filePaths.Count == 0) return;

        var pidls = new List<IntPtr>(filePaths.Count);
        IntPtr arrayPtr = IntPtr.Zero;
        try
        {
            foreach (var p in filePaths)
            {
                var pidl = ILCreateFromPath(p);
                if (pidl != IntPtr.Zero) pidls.Add(pidl);
            }
            if (pidls.Count == 0) return;

            arrayPtr = Marshal.AllocCoTaskMem(IntPtr.Size * pidls.Count);
            for (int i = 0; i < pidls.Count; i++)
                Marshal.WriteIntPtr(arrayPtr, i * IntPtr.Size, pidls[i]);

            int hr = SHCreateShellItemArrayFromIDLists((uint)pidls.Count, arrayPtr, out var array);
            if (hr != 0 || array == null) return;

            try
            {
                var bhidDataObject = new Guid("B8C0BD9F-ED24-455C-83E6-D5390C4FE8C4");
                var iidDataObject = typeof(IDataObject).GUID;
                hr = array.BindToHandler(IntPtr.Zero, ref bhidDataObject, ref iidDataObject, out IntPtr dataObjectPtr);
                if (hr != 0 || dataObjectPtr == IntPtr.Zero) return;

                try
                {
                    var dataObject = (IDataObject)Marshal.GetObjectForIUnknown(dataObjectPtr);
                    handler.Handler.Invoke(dataObject);
                    Marshal.ReleaseComObject(dataObject);
                }
                finally
                {
                    Marshal.Release(dataObjectPtr);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(array);
            }
        }
        finally
        {
            foreach (var pidl in pidls) ILFree(pidl);
            if (arrayPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(arrayPtr);
        }
    }

    /// <summary>
    /// Opens the standard Windows "Open with…" picker dialog for the file.
    /// Used as the trailing "Choose another app…" entry in the submenu.
    /// </summary>
    public static void ShowOpenWithDialog(string filePath, IntPtr hwnd = default)
    {
        var info = new OPENASINFO
        {
            pcszFile = filePath,
            pcszClass = null,
            oaifInFlags = OAIF.OAIF_EXEC | OAIF.OAIF_HIDE_REGISTRATION
        };
        try { SHOpenWithDialog(hwnd, ref info); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[OPS] OpenWithDialog failed: {ex.Message}"); }
    }

    // -----------------------------------------------------------------
    //  Types
    // -----------------------------------------------------------------

    public sealed class HandlerInfo
    {
        public required string UIName { get; init; }
        public string? ExecutablePath { get; init; }
        public string? IconPath { get; init; }
        public int IconIndex { get; init; }
        public required IAssocHandler Handler { get; init; }
    }

    [Flags]
    private enum ASSOC_FILTER
    {
        ASSOC_FILTER_NONE = 0,
        ASSOC_FILTER_RECOMMENDED = 1
    }

    [Flags]
    private enum OAIF : uint
    {
        OAIF_ALLOW_REGISTRATION = 0x00000001,
        OAIF_REGISTER_EXT       = 0x00000002,
        OAIF_EXEC               = 0x00000004,
        OAIF_FORCE_REGISTRATION = 0x00000008,
        OAIF_HIDE_REGISTRATION  = 0x00000020,
        OAIF_URL_PROTOCOL       = 0x00000040,
        OAIF_FILE_IS_URI        = 0x00000080
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENASINFO
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pcszFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pcszClass;
        public OAIF oaifInFlags;
    }

    [ComImport, Guid("F04061AC-1659-4A3F-A954-775AA57FC083"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAssocHandler
    {
        [PreserveSig] int GetName([MarshalAs(UnmanagedType.LPWStr)] out string? ppsz);
        [PreserveSig] int GetUIName([MarshalAs(UnmanagedType.LPWStr)] out string? ppsz);
        [PreserveSig] int GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] out string? ppszPath, out int pIndex);
        [PreserveSig] int IsRecommended();
        [PreserveSig] int MakeDefault([MarshalAs(UnmanagedType.LPWStr)] string pszDescription);
        [PreserveSig] int Invoke([MarshalAs(UnmanagedType.Interface)] IDataObject pdo);
        [PreserveSig] int CreateInvoker([MarshalAs(UnmanagedType.Interface)] IDataObject pdo, out IntPtr ppInvoker);
    }

    [ComImport, Guid("973810AE-9599-4B88-9E4D-6EE98C9552DA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumAssocHandlers
    {
        [PreserveSig] int Next(uint celt, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IAssocHandler[] rgelt, out uint pceltFetched);
    }

    [ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid rbhid, ref Guid riid, out IntPtr ppvOut);
        // Other methods omitted — we only need BindToHandler
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern int SHAssocEnumHandlers(
        [MarshalAs(UnmanagedType.LPWStr)] string pszExtra,
        ASSOC_FILTER afFilter,
        out IEnumAssocHandlers ppEnumHandler);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ILCreateFromPath([MarshalAs(UnmanagedType.LPWStr)] string pszPath);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern int SHCreateShellItemArrayFromIDLists(uint cidl, IntPtr rgpidl, out IShellItemArray ppsiItemArray);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHOpenWithDialog(IntPtr hwndParent, ref OPENASINFO poainfo);
}
