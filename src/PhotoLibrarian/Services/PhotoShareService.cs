// Copyright (c) Duncan Lawler. All Rights Reserved.

using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace PhotoLibrarian.Services;

internal static class ShareStrings
{
    private static readonly ResourceLoader Loader = new(
        Path.Combine(AppContext.BaseDirectory, "PhotoLibrarian.pri"), "Resources");

    public static string Get(string key) => Loader.GetString(key);
    public static string Format(string key, params object?[] values) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), values);
}

internal sealed class PhotoShareService : IDisposable
{
    private DataTransferManager? _manager;
    private DataPackage? _package;

    public async Task ShowAsync(nint windowHandle, IReadOnlyList<string> paths, string title)
    {
        _package = await CreatePackageAsync(paths, title);
        if (_manager is null)
        {
            _manager = DataTransferManagerInterop.GetForWindow(windowHandle);
            _manager.DataRequested += OnDataRequested;
        }
        DataTransferManagerInterop.ShowShareUIForWindow(windowHandle);
    }

    internal static async Task<DataPackage> CreatePackageAsync(IReadOnlyList<string> paths, string title)
    {
        if (paths.Count == 0)
            throw new ArgumentException("No prepared photos are available.", nameof(paths));
        var files = new List<IStorageItem>();
        foreach (var path in paths)
            files.Add(await StorageFile.GetFileFromPathAsync(path));

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.Properties.Title = title;
        package.SetStorageItems(files, readOnly: true);
        return package;
    }

    private void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
    {
        if (_package is not null)
            args.Request.Data = _package;
        else
            args.Request.FailWithDisplayText(ShareStrings.Get("ShareNoPhotos"));
    }

    // RFC 6068 has no interoperable attachment parameter. Keep this URI short and do
    // not put local paths (or a fictitious attachment= parameter) into the message.
    internal static Uri CreateMailUri(string subject, string body) =>
        new($"mailto:?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body)}");

    internal static async Task<(bool FolderOpened, bool DraftOpened)> OpenEmailAsync(
        string folder, Uri mailUri,
        Func<string, Task<bool>> openFolder,
        Func<Uri, Task<bool>> openUri)
    {
        var folderOpened = await TryLaunchAsync(() => openFolder(folder));
        var draftOpened = await TryLaunchAsync(() => openUri(mailUri));
        return (folderOpened, draftOpened);
    }

    private static async Task<bool> TryLaunchAsync(Func<Task<bool>> launch)
    {
        try
        {
            return await launch();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            Diagnostics.DebugLog.WriteLine($"[SHARE] Could not launch email fallback: {ex}");
            return false;
        }
    }

    public static Task<(bool FolderOpened, bool DraftOpened)> OpenEmailAsync(string folder) =>
        OpenEmailAsync(folder,
            CreateMailUri(ShareStrings.Get("ShareMailSubject"), ShareStrings.Get("ShareMailBody")),
            OpenFolderAsync, uri => Launcher.LaunchUriAsync(uri).AsTask());

    private static async Task<bool> OpenFolderAsync(string path) =>
        await Launcher.LaunchFolderAsync(await StorageFolder.GetFolderFromPathAsync(path));

    public void Dispose()
    {
        if (_manager is not null)
            _manager.DataRequested -= OnDataRequested;
        _manager = null;
        _package = null;
    }
}
