// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Globalization;
using System.Runtime.InteropServices;
using PhotoLibrarian.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class ResourceStringsTests
{
    [Fact]
    public void GetReturnsResourceValueAndPreservesMissingKeyException()
    {
        Assert.Equal("Share photos", ResourceStrings.Get("ShareTitle"));
        Assert.Throws<COMException>(() => ResourceStrings.Get("ResourceStringsMissingKeyForTest"));
    }

    [Fact]
    public void FormatUsesCurrentCultureForNumericArguments()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            var culture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentCulture = culture;

            var importResult = PhotoImportStrings.Format("PhotoImportScanFound", 1234);
            var shareResult = ShareStrings.Format("ShareProgress", 1234, 5678, "photo.jpg");
            var autoTagResult = AutoTagReviewStrings.Format("AutoTagReviewIssuesSummary", 1234);

            Assert.Equal(
                $"Found {1234.ToString("N0", culture)} supported item(s). Select the items to import.",
                importResult);
            Assert.Equal(
                $"Preparing {1234.ToString("N0", culture)} of {5678.ToString("N0", culture)}: photo.jpg",
                shareResult);
            Assert.Equal(
                $"{1234.ToString("N0", culture)} photo(s) need attention. See the messages on each photo.",
                autoTagResult);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }
}
