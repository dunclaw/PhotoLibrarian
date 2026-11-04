// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.ViewModels;
using PhotoLibrarian.Views;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI;

namespace PhotoLibrarian.AccessibilityTests;

public partial class App
{
    private async Task RunMotionPhotoSuiteAsync()
    {
        string? failure = null;
        string folder = Path.Combine(Path.GetTempPath(), $"MotionPhotoUI-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        ImageViewerViewModel? viewer = null;
        try
        {
            await CreateMotionFixturesAsync(folder);
            byte[] original = await File.ReadAllBytesAsync(Path.Combine(folder, "motion.jpg"));
            DateTime written = File.GetLastWriteTimeUtc(Path.Combine(folder, "motion.jpg"));
            viewer = new ImageViewerViewModel();
            var overlay = new ImageViewerOverlay(viewer);
            _window = new Window { Title = "Generated motion photo lifecycle", Content = overlay };
            _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 720));
            _window.Activate();
            await UntilAsync(() => overlay.IsLoaded);
            Button button = (Button)overlay.FindName("MotionPhotoButton");
            MediaPlayerElement player = (MediaPlayerElement)overlay.FindName("MotionPlayer");
            InfoBar status = (InfoBar)overlay.FindName("MotionPhotoStatus");
            var motion = Entry(folder, "motion.jpg");
            var still = Entry(folder, "still.jpg");
            var malformed = Entry(folder, "malformed.jpg");
            var badMedia = Entry(folder, "bad-media.jpg");
            var video = Entry(folder, "generated.mp4", MediaType.Video);
            List<ImageEntry> entries = [motion, still, malformed, badMedia, video];

            viewer.OpenImage(motion, entries);
            await UntilAsync(() => button.Visibility == Visibility.Visible && viewer.CurrentImage is not null);
            Check(player.Visibility == Visibility.Collapsed && !viewer.IsVideo,
                "Motion photo opens as an ordinary still cover");
            Check(AutomationProperties.GetAutomationId(button) == "ViewerMotionPhoto" &&
                !string.IsNullOrEmpty(AutomationProperties.GetName(button)), "Motion action has a localized accessible name");
            Invoke(button);
            await UntilAsync(() => player.Visibility == Visibility.Visible);
            Check(player.MediaPlayer.PlaybackSession.NaturalVideoWidth == 640,
                "Generated JPEG embedded H.264 MP4 plays through the actual viewer");
            Check(!((Button)overlay.FindName("ZoomInButton")).IsEnabled,
                "Photo zoom controls do not silently change the hidden cover during playback");
            if (Environment.GetCommandLineArgs().Contains("--preview"))
            {
                player.MediaPlayer.Pause();
                await Task.Delay(TimeSpan.FromSeconds(20));
            }
            Invoke(button);
            Check(player.Visibility == Visibility.Collapsed && player.MediaPlayer is null &&
                viewer.CurrentImage is not null, "Show still detaches and disposes motion playback");
            Check(((Button)overlay.FindName("ZoomInButton")).IsEnabled,
                "Returning to the still restores photo zoom controls");

            Invoke(button);
            await UntilAsync(() => player.Visibility == Visibility.Visible);
            await UntilAsync(() => player.Visibility == Visibility.Collapsed);
            Check(viewer.CurrentImage is not null && !status.IsOpen, "Motion end returns to the cover without an error");

            Invoke(button);
            await UntilAsync(() => player.Visibility == Visibility.Visible);
            await viewer.NextImageCommand.ExecuteAsync(null);
            await Task.Delay(150);
            Check(player.MediaPlayer is null && button.Visibility == Visibility.Collapsed &&
                viewer.CurrentImage is not null && !status.IsOpen, "Navigation stops playback and preserves ordinary JPEG viewing");
            typeof(ImageViewerOverlay).GetMethod("OnMotionOpenTimeout", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(overlay, [new DispatcherTimer(), new object()]);
            Check(!status.IsOpen, "A stale native-open timeout cannot change the next photo");

            viewer.OpenImage(motion, entries);
            await UntilAsync(() => button.Visibility == Visibility.Visible);
            Invoke(button);
            overlay.EnterCropMode();
            overlay.ExitCropMode();
            await Task.Delay(250);
            Check(player.MediaPlayer is null && !status.IsOpen,
                "Entering a photo edit mode cancels an in-flight motion handoff");

            viewer.OpenImage(motion, entries);
            await UntilAsync(() => button.Visibility == Visibility.Visible);
            Invoke(button);
            viewer.CloseCommand.Execute(null);
            await Task.Delay(250);
            Check(player.MediaPlayer is null && !status.IsOpen && button.Visibility == Visibility.Collapsed,
                "Close during extraction prevents stale playback or warnings");

            viewer.OpenImage(motion, entries);
            viewer.OpenImage(still, entries);
            await Task.Delay(250);
            Check(button.Visibility == Visibility.Collapsed && !status.IsOpen,
                "Rapid navigation cancels detection and rejects stale results");

            viewer.OpenImage(malformed, entries);
            await UntilAsync(() => status.IsOpen && viewer.CurrentImage is not null);
            Check(player.MediaPlayer is null, "Malformed motion metadata reports a limitation while retaining the still");
            viewer.OpenImage(badMedia, entries);
            await UntilAsync(() => button.Visibility == Visibility.Visible);
            Invoke(button);
            await UntilAsync(() => status.IsOpen);
            Check(player.MediaPlayer is null && viewer.CurrentImage is not null,
                "Invalid media accepted structurally falls back after native decoder failure");

            viewer.OpenImage(video, entries);
            await UntilAsync(() => ((MediaPlayerElement)overlay.FindName("VideoPlayer")).MediaPlayer is not null);
            Check(viewer.IsVideo && button.Visibility == Visibility.Collapsed && !status.IsOpen,
                "Standalone video remains on the original playback path");
            viewer.CloseCommand.Execute(null);

            viewer.OpenImage(motion, entries);
            await UntilAsync(() => button.Visibility == Visibility.Visible);
            Invoke(button);
            await UntilAsync(() => player.Visibility == Visibility.Visible);
            _window.Content = new Grid();
            await UntilAsync(() => !overlay.IsLoaded);
            await UntilAsync(() => player.MediaPlayer is null);
            Check(player.MediaPlayer is null, "Unloading the viewer disposes native playback");
            _window.Content = overlay;
            await UntilAsync(() => overlay.IsLoaded && button.Visibility == Visibility.Visible);
            Check(player.MediaPlayer is null, "Reloading the viewer restores detection without autoplay or duplicate subscriptions");
            byte[] afterPlayback = await File.ReadAllBytesAsync(motion.FilePath);
            Check(SHA256.HashData(original).SequenceEqual(SHA256.HashData(afterPlayback)) &&
                written == File.GetLastWriteTimeUtc(motion.FilePath), "All playback lifecycle operations leave source bytes and timestamp unchanged");
            Check(Directory.GetFiles(folder).Length == 5, "Playback creates no extracted library artifacts");
        }
        catch (Exception error)
        {
            failure = error.ToString();
        }
        finally
        {
            viewer?.CloseCommand.Execute(null);
            // Only explicitly named generated fixtures belong to this isolated test directory.
            foreach (string name in new[] { "motion.jpg", "still.jpg", "malformed.jpg", "bad-media.jpg", "generated.mp4" })
            {
                string path = Path.Combine(folder, name);
                if (File.Exists(path)) File.Delete(path);
            }
            Directory.Delete(folder);
        }
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "motion-photo-results.json"),
            JsonSerializer.Serialize(new { passed = _passed, failure }, new JsonSerializerOptions { WriteIndented = true }));
        _window?.Close();
        Exit();
    }

    private static ImageEntry Entry(string folder, string name, MediaType type = MediaType.Image) =>
        new() { FilePath = Path.Combine(folder, name), FileName = name, MediaType = type };

    private static void Invoke(Button button) =>
        ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(button)
            .GetPattern(PatternInterface.Invoke)).Invoke();

    private static async Task UntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Generated motion-photo UI condition timed out.");
            await Task.Delay(25);
        }
    }

    private static async Task CreateMotionFixturesAsync(string folder)
    {
        using var jpeg = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, jpeg);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, 64, 48, 96, 96, new byte[64 * 48 * 4]);
        await encoder.FlushAsync();
        byte[] image = new byte[checked((int)jpeg.Size)];
        jpeg.Seek(0);
        using (var reader = new DataReader(jpeg.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)image.Length);
            reader.ReadBytes(image);
        }
        await File.WriteAllBytesAsync(Path.Combine(folder, "still.jpg"), image);
        var composition = new MediaComposition();
        composition.Clips.Add(MediaClip.CreateFromColor(Color.FromArgb(255, 40, 100, 160), TimeSpan.FromSeconds(1)));
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Vga);
        var output = await (await StorageFolder.GetFolderFromPathAsync(folder)).CreateFileAsync("generated.mp4");
        var result = await composition.RenderToFileAsync(output, MediaTrimmingPreference.Precise, profile);
        if (result != Windows.Media.Transcoding.TranscodeFailureReason.None)
            throw new InvalidOperationException($"Could not generate test-only MP4: {result}");
        byte[] video = await File.ReadAllBytesAsync(output.Path);
        await File.WriteAllBytesAsync(Path.Combine(folder, "motion.jpg"), Embed(image, video, video.Length));
        await File.WriteAllBytesAsync(Path.Combine(folder, "malformed.jpg"), Embed(image, video, long.MaxValue));
        // A structurally bounded container whose movie payload is deliberately undecodable.
        byte[] invalid = [0, 0, 0, 16, (byte)'f', (byte)'t', (byte)'y', (byte)'p', 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 8, (byte)'m', (byte)'o', (byte)'o', (byte)'v',
            0, 0, 0, 8, (byte)'m', (byte)'d', (byte)'a', (byte)'t'];
        await File.WriteAllBytesAsync(Path.Combine(folder, "bad-media.jpg"), Embed(image, invalid, invalid.Length));
    }

    private static byte[] Embed(byte[] image, byte[] video, long length)
    {
        string xmp = $"""
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
            xmlns:c="http://ns.google.com/photos/1.0/camera/"
            xmlns:container="http://ns.google.com/photos/1.0/container/"
            xmlns:item="http://ns.google.com/photos/1.0/container/item/">
            <rdf:Description c:MotionPhoto="1" c:MotionPhotoVersion="1" c:MotionPhotoPresentationTimestampUs="500000">
            <container:Directory><rdf:Seq>
            <rdf:li><container:Item item:Mime="image/jpeg" item:Semantic="Primary" /></rdf:li>
            <rdf:li><container:Item item:Mime="video/mp4" item:Semantic="MotionPhoto" item:Length="{length}" /></rdf:li>
            </rdf:Seq></container:Directory></rdf:Description></rdf:RDF>
            """;
        byte[] payload = Encoding.UTF8.GetBytes("http://ns.adobe.com/xap/1.0/\0" + xmp);
        byte[] size = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(size, checked((ushort)(payload.Length + 2)));
        return [.. image[..2], 0xff, 0xe1, .. size, .. payload, .. image[2..], .. video];
    }
}
