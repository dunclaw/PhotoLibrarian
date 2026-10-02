using System.Xml.Linq;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class ImageViewerOverlayFocusTests
{
    [Fact]
    public void OverlayCanReceiveFocusForKeyboardNavigation()
    {
        var xamlPath = Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "ImageViewerOverlay.xaml");
        var root = XDocument.Load(xamlPath).Root;

        Assert.NotNull(root);
        Assert.Equal("True", (string?)root.Attribute("IsTabStop"));
        Assert.Equal("OnKeyDown", (string?)root.Attribute("KeyDown"));
    }
}
