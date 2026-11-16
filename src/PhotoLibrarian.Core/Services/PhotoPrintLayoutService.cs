// Copyright (c) Duncan Lawler. All Rights Reserved.
namespace PhotoLibrarian.Core.Services;

public enum PhotoPrintLayout
{
    FullPage,
    FourBySix,
    FiveBySeven,
    EightByTen,
    Wallet,
    ContactSheet
}

public enum PhotoPrintPlacement
{
    Fit,
    Fill
}

public readonly record struct PhotoPrintRectangle(double X, double Y, double Width, double Height);

public readonly record struct PhotoPrintSlot(
    int PhotoIndex,
    PhotoPrintRectangle ImageBounds,
    PhotoPrintRectangle? CaptionBounds);

public static class PhotoPrintLayoutService
{
    public const int ContactSheetPhotosPerPage = 9;
    public const int WalletPhotosPerPage = 4;
    public const double DipsPerInch = 96;

    public static int GetPageCount(int photoCount, PhotoPrintLayout layout)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(photoCount);
        if (photoCount == 0) return 0;

        var capacity = layout switch
        {
            PhotoPrintLayout.ContactSheet => ContactSheetPhotosPerPage,
            PhotoPrintLayout.Wallet => WalletPhotosPerPage,
            _ => 1
        };
        return (photoCount + capacity - 1) / capacity;
    }

    public static IReadOnlyList<PhotoPrintSlot> CreatePageSlots(
        int pageNumber,
        int photoCount,
        PhotoPrintLayout layout,
        double pageWidth,
        double pageHeight,
        PhotoPrintRectangle imageableBounds,
        bool sourceIsPortrait = false)
    {
        var pageCount = GetPageCount(photoCount, layout);
        if (pageNumber < 1 || pageNumber > pageCount)
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        ValidatePage(pageWidth, pageHeight, imageableBounds);

        var firstPhotoIndex = layout switch
        {
            PhotoPrintLayout.ContactSheet => (pageNumber - 1) * ContactSheetPhotosPerPage,
            PhotoPrintLayout.Wallet => (pageNumber - 1) * WalletPhotosPerPage,
            _ => pageNumber - 1
        };

        return layout switch
        {
            PhotoPrintLayout.ContactSheet => CreateContactSheetSlots(
                firstPhotoIndex, photoCount, imageableBounds),
            PhotoPrintLayout.Wallet => CreateWalletSlots(
                firstPhotoIndex, photoCount, imageableBounds),
            _ => [new PhotoPrintSlot(
                firstPhotoIndex,
                CreatePhotoFrame(layout, imageableBounds, sourceIsPortrait),
                null)]
        };
    }

    public static PhotoPrintRectangle CreatePhotoFrame(
        PhotoPrintLayout layout,
        PhotoPrintRectangle imageableBounds,
        bool sourceIsPortrait = false)
    {
        ValidateBounds(imageableBounds);

        if (layout == PhotoPrintLayout.FullPage)
            return imageableBounds;

        var (widthInches, heightInches) = layout switch
        {
            PhotoPrintLayout.FourBySix => (6d, 4d),
            PhotoPrintLayout.FiveBySeven => (7d, 5d),
            PhotoPrintLayout.EightByTen => (10d, 8d),
            PhotoPrintLayout.Wallet => (2.5d, 3.5d),
            _ => throw new ArgumentOutOfRangeException(nameof(layout))
        };

        if (sourceIsPortrait && layout != PhotoPrintLayout.Wallet)
            (widthInches, heightInches) = (heightInches, widthInches);

        var targetWidth = widthInches * DipsPerInch;
        var targetHeight = heightInches * DipsPerInch;
        var scale = Math.Min(1d, Math.Min(
            imageableBounds.Width / targetWidth,
            imageableBounds.Height / targetHeight));
        var width = targetWidth * scale;
        var height = targetHeight * scale;

        return new PhotoPrintRectangle(
            imageableBounds.X + (imageableBounds.Width - width) / 2,
            imageableBounds.Y + (imageableBounds.Height - height) / 2,
            width,
            height);
    }

    public static IReadOnlyList<T> OrderSelection<T>(
        IReadOnlyList<T> displayedItems,
        IEnumerable<T> selectedItems,
        Func<T, string> getPath)
    {
        ArgumentNullException.ThrowIfNull(displayedItems);
        ArgumentNullException.ThrowIfNull(selectedItems);
        ArgumentNullException.ThrowIfNull(getPath);

        var selectedByPath = selectedItems
            .GroupBy(item => getPath(item), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var ordered = new List<T>(selectedByPath.Count);

        foreach (var item in displayedItems)
        {
            var path = getPath(item);
            if (selectedByPath.Remove(path, out var selected))
                ordered.Add(selected);
        }

        ordered.AddRange(selectedByPath
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Value));
        return ordered;
    }

    private static IReadOnlyList<PhotoPrintSlot> CreateContactSheetSlots(
        int firstPhotoIndex,
        int photoCount,
        PhotoPrintRectangle imageableBounds)
    {
        const int columns = 3;
        const int rows = 3;
        const double margin = 12;
        const double gap = 12;
        const double captionHeight = 20;

        var cellWidth = (imageableBounds.Width - 2 * margin - (columns - 1) * gap) / columns;
        var cellHeight = (imageableBounds.Height - 2 * margin - (rows - 1) * gap) / rows;
        if (cellWidth <= 0 || cellHeight <= captionHeight)
            throw new ArgumentException("The printable area is too small for a contact sheet.", nameof(imageableBounds));

        var slots = new List<PhotoPrintSlot>(ContactSheetPhotosPerPage);
        for (var index = firstPhotoIndex; index < Math.Min(firstPhotoIndex + ContactSheetPhotosPerPage, photoCount); index++)
        {
            var localIndex = index - firstPhotoIndex;
            var column = localIndex % columns;
            var row = localIndex / columns;
            var x = imageableBounds.X + margin + column * (cellWidth + gap);
            var y = imageableBounds.Y + margin + row * (cellHeight + gap);
            slots.Add(new PhotoPrintSlot(
                index,
                new PhotoPrintRectangle(x, y, cellWidth, cellHeight - captionHeight),
                new PhotoPrintRectangle(x, y + cellHeight - captionHeight, cellWidth, captionHeight)));
        }

        return slots;
    }

    private static IReadOnlyList<PhotoPrintSlot> CreateWalletSlots(
        int firstPhotoIndex,
        int photoCount,
        PhotoPrintRectangle imageableBounds)
    {
        const int columns = 2;
        const int rows = 2;
        const double gap = 16;
        const double margin = 12;
        var frameWidth = 2.5 * DipsPerInch;
        var frameHeight = 3.5 * DipsPerInch;
        var totalWidth = columns * frameWidth + (columns - 1) * gap;
        var totalHeight = rows * frameHeight + (rows - 1) * gap;
        var availableWidth = Math.Max(0, imageableBounds.Width - 2 * margin);
        var availableHeight = Math.Max(0, imageableBounds.Height - 2 * margin);
        var scale = Math.Min(1d, Math.Min(availableWidth / totalWidth, availableHeight / totalHeight));
        if (scale <= 0)
            throw new ArgumentException("The printable area is too small for wallet prints.", nameof(imageableBounds));

        var scaledWidth = totalWidth * scale;
        var scaledHeight = totalHeight * scale;
        var startX = imageableBounds.X + (imageableBounds.Width - scaledWidth) / 2;
        var startY = imageableBounds.Y + (imageableBounds.Height - scaledHeight) / 2;
        var slots = new List<PhotoPrintSlot>(WalletPhotosPerPage);

        for (var index = firstPhotoIndex; index < Math.Min(firstPhotoIndex + WalletPhotosPerPage, photoCount); index++)
        {
            var localIndex = index - firstPhotoIndex;
            slots.Add(new PhotoPrintSlot(
                index,
                new PhotoPrintRectangle(
                    startX + localIndex % columns * (frameWidth + gap) * scale,
                    startY + localIndex / columns * (frameHeight + gap) * scale,
                    frameWidth * scale,
                    frameHeight * scale),
                null));
        }

        return slots;
    }

    private static void ValidatePage(double pageWidth, double pageHeight, PhotoPrintRectangle imageableBounds)
    {
        if (!double.IsFinite(pageWidth) || !double.IsFinite(pageHeight) || pageWidth <= 0 || pageHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageWidth), "Page dimensions must be finite and positive.");
        ValidateBounds(imageableBounds);
        if (imageableBounds.X < 0 || imageableBounds.Y < 0 ||
            imageableBounds.X + imageableBounds.Width > pageWidth ||
            imageableBounds.Y + imageableBounds.Height > pageHeight)
            throw new ArgumentException("The imageable area must be contained by the page.", nameof(imageableBounds));
    }

    private static void ValidateBounds(PhotoPrintRectangle bounds)
    {
        if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) ||
            !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) ||
            bounds.Width <= 0 || bounds.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(bounds), "Bounds must be finite and have positive dimensions.");
    }
}
