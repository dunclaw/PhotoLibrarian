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
using Microsoft.UI.Xaml.Input;
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
            string appleStillPath = Path.Combine(folder, "apple-still.jpg");
            string appleMoviePath = Path.Combine(folder, "apple-unrelated-name.mov");
            byte[] appleStillOriginal = await File.ReadAllBytesAsync(appleStillPath);
            byte[] appleMovieOriginal = await File.ReadAllBytesAsync(appleMoviePath);
            byte[] appleStillHash = SHA256.HashData(appleStillOriginal);
            byte[] appleMovieHash = SHA256.HashData(appleMovieOriginal);
            DateTime appleStillWritten = File.GetLastWriteTimeUtc(appleStillPath);
            DateTime appleMovieWritten = File.GetLastWriteTimeUtc(appleMoviePath);
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
            var appleStill = Entry(folder, "apple-still.jpg");
            List<ImageEntry> entries = [motion, still, malformed, badMedia, video, appleStill];

            viewer.OpenImage(motion, entries);
            await UntilAsync(() => player.Visibility == Visibility.Visible && viewer.CurrentImage is not null);
            Check(!viewer.IsVideo && player.MediaPlayer.IsLoopingEnabled,
                "Motion photo automatically plays on loop while retaining its original still cover");
            Check(AutomationProperties.GetAutomationId(button) == "ViewerMotionPhoto" &&
                !string.IsNullOrEmpty(AutomationProperties.GetName(button)), "Motion action has a localized accessible name");
            Check(player.MediaPlayer.PlaybackSession.NaturalVideoWidth == 640,
                "Generated JPEG embedded H.264 MP4 plays through the actual viewer");
            Check(!((Button)overlay.FindName("ZoomInButton")).IsEnabled,
                "Photo zoom controls do not silently change the hidden cover during playback");
            if (Environment.GetCommandLineArgs().Contains("--preview"))
            {
                await Task.Delay(TimeSpan.FromSeconds(20));
            }
            Invoke(button);
            Check(player.Visibility == Visibility.Collapsed && player.MediaPlayer is null &&
                viewer.CurrentImage is not null &&
                ReferenceEquals(((Image)overlay.FindName("FullImage")).Source, viewer.CurrentImage),
                "Header pause detaches and disposes motion playback and shows the original still");
            Check(AutomationProperties.GetName(button) == "Play motion", "Paused header action offers play");
            await Task.Delay(1500);
            Check(player.MediaPlayer is null, "Paused motion stays on the still without automatically restarting");
            Check(((Button)overlay.FindName("ZoomInButton")).IsEnabled,
                "Returning to the still restores photo zoom controls");

            Invoke(button);
            await UntilAsync(() => player.Visibility == Visibility.Visible);
            var loopingPlayer = player.MediaPlayer;
            await Task.Delay(2500);
            var currentPlayer = player.MediaPlayer;
            Check(ReferenceEquals(loopingPlayer, currentPlayer) && player.Visibility == Visibility.Visible &&
                currentPlayer?.PlaybackSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing &&
                !status.IsOpen, "Playback continues across multiple clip durations instead of returning to the still");
            Check(AutomationProperties.GetName(button) == "Pause motion and show still photo",
                "Playing header action offers pause");
            var tap = new TappedRoutedEventArgs();
            typeof(ImageViewerOverlay).GetMethod("OnMotionImageTapped", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(overlay, [player, tap]);
            Check(tap.Handled && player.MediaPlayer is null && viewer.CurrentImage is not null,
                "Tapping the playing image stops motion and shows the still");

            viewer.OpenImage(appleStill, entries);
            await UntilAsync(() => button.Visibility == Visibility.Visible);
            await Task.Delay(200);
            Check(player.MediaPlayer is null && viewer.CurrentImage is not null && !status.IsOpen,
                "A verified Apple pair keeps the still visible without autoplay");
            Check(AutomationProperties.GetName(button) == "Play motion",
                "Apple motion playback is an explicit accessible action");
            Invoke(button);
            await UntilAsync(() => player.Visibility == Visibility.Visible);
            await UntilAsync(() => player.MediaPlayer?.PlaybackSession.Position > TimeSpan.Zero);
            var applePlayer = player.MediaPlayer;
            Check(applePlayer is not null && viewer.CurrentImage is not null &&
                applePlayer.PlaybackSession.NaturalVideoWidth == 640,
                "Generated Apple JPEG/MOV pair plays through the still-first viewer");
            Invoke(button);
            Check(player.MediaPlayer is null && viewer.CurrentImage is not null,
                "Stopping Apple motion returns to the original still");
            byte[] appleStillAfterHash = SHA256.HashData(await File.ReadAllBytesAsync(appleStillPath));
            byte[] appleMovieAfterHash = SHA256.HashData(await File.ReadAllBytesAsync(appleMoviePath));
            Check(appleStillHash.SequenceEqual(appleStillAfterHash) &&
                appleMovieHash.SequenceEqual(appleMovieAfterHash) &&
                appleStillWritten == File.GetLastWriteTimeUtc(appleStillPath) &&
                appleMovieWritten == File.GetLastWriteTimeUtc(appleMoviePath),
                "Apple still and MOV source bytes and timestamps remain unchanged");

            viewer.OpenImage(appleStill, entries);
            await UntilAsync(() => button.Visibility == Visibility.Visible);
            Invoke(button);
            await UntilAsync(() => player.Visibility == Visibility.Visible);
            viewer.OpenImage(still, entries);
            await UntilAsync(() => player.MediaPlayer is null &&
                button.Visibility == Visibility.Collapsed && viewer.CurrentImage is not null);
            Check(viewer.CurrentImage is not null && !status.IsOpen,
                "Navigating away from Apple playback releases its local MOV source");

            viewer.OpenImage(appleStill, entries);
            await UntilAsync(() => button.Visibility == Visibility.Visible);
            Invoke(button);
            await UntilAsync(() => player.Visibility == Visibility.Visible);
            viewer.CloseCommand.Execute(null);
            await UntilAsync(() => player.MediaPlayer is null && button.Visibility == Visibility.Collapsed);
            Check(!status.IsOpen, "Closing during Apple playback cancels work and releases the MOV");

            viewer.OpenImage(motion, entries);
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
            overlay.EnterCropMode();
            overlay.ExitCropMode();
            await Task.Delay(250);
            Check(player.MediaPlayer is null && !status.IsOpen,
                "Entering a photo edit mode cancels an in-flight motion handoff");

            viewer.OpenImage(motion, entries);
            await UntilAsync(() => button.Visibility == Visibility.Visible);
            Invoke(button);
            await Task.Delay(150);
            Check(player.MediaPlayer is null, "Header pause cancels automatic playback during extraction or opening");
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
            await UntilAsync(() => status.IsOpen);
            Check(player.MediaPlayer is null && viewer.CurrentImage is not null,
                "Invalid media accepted structurally falls back after native decoder failure");

            viewer.OpenImage(video, entries);
            await UntilAsync(() => ((MediaPlayerElement)overlay.FindName("VideoPlayer")).MediaPlayer is not null);
            Check(viewer.IsVideo && button.Visibility == Visibility.Collapsed && !status.IsOpen,
                "Standalone video remains on the original playback path");
            viewer.CloseCommand.Execute(null);

            viewer.OpenImage(motion, entries);
            await UntilAsync(() => player.Visibility == Visibility.Visible);
            _window.Content = new Grid();
            await UntilAsync(() => !overlay.IsLoaded);
            await UntilAsync(() => player.MediaPlayer is null);
            Check(player.MediaPlayer is null, "Unloading the viewer disposes native playback");
            _window.Content = overlay;
            await UntilAsync(() => overlay.IsLoaded && player.Visibility == Visibility.Visible);
            Check(player.MediaPlayer?.IsLoopingEnabled == true, "Reloading the full viewer restores automatic looping playback");
            byte[] afterPlayback = await File.ReadAllBytesAsync(motion.FilePath);
            Check(SHA256.HashData(original).SequenceEqual(SHA256.HashData(afterPlayback)) &&
                written == File.GetLastWriteTimeUtc(motion.FilePath), "All playback lifecycle operations leave source bytes and timestamp unchanged");
            Check(Directory.GetFiles(folder).Length == 7, "Playback creates no extracted library artifacts");
            string[] arguments = Environment.GetCommandLineArgs();
            int sampleArgument = Array.IndexOf(arguments, "--motion-photo-sample");
            string? samplePath = sampleArgument >= 0 ? arguments[sampleArgument + 1] : null;
            if (!string.IsNullOrWhiteSpace(samplePath))
            {
                byte[] sampleBefore = SHA256.HashData(await File.ReadAllBytesAsync(samplePath));
                DateTime sampleWritten = File.GetLastWriteTimeUtc(samplePath);
                var sample = new ImageEntry { FilePath = samplePath, FileName = Path.GetFileName(samplePath), MediaType = MediaType.Image };
                viewer.OpenImage(sample, [sample]);
                await UntilAsync(() => button.Visibility == Visibility.Visible || status.IsOpen);
                if (status.IsOpen) throw new InvalidOperationException($"Opt-in sample detection failed: {status.Message}");
                Check(!status.IsOpen, "Opt-in sample detection succeeds");
                await UntilAsync(() => player.Visibility == Visibility.Visible || status.IsOpen);
                if (status.IsOpen) throw new InvalidOperationException($"Opt-in sample playback failed: {status.Message}");
                Check(player.MediaPlayer is not null, "Opt-in sample native playback succeeds");
                await Task.Delay(250);
                Check(!status.IsOpen && player.MediaPlayer!.PlaybackSession.Position > TimeSpan.Zero,
                    "Opt-in sample automatic native playback advances");
                viewer.CloseCommand.Execute(null);
                byte[] sampleAfter = SHA256.HashData(await File.ReadAllBytesAsync(samplePath));
                Check(sampleBefore.SequenceEqual(sampleAfter) && sampleWritten == File.GetLastWriteTimeUtc(samplePath),
                    "Opt-in sample source hash and timestamp remain unchanged");
            }
        }
        catch (Exception error)
        {
            failure = error.ToString();
        }
        finally
        {
            viewer?.CloseCommand.Execute(null);
            // Only explicitly named generated fixtures belong to this isolated test directory.
            foreach (string name in new[]
                { "motion.jpg", "still.jpg", "malformed.jpg", "bad-media.jpg", "generated.mp4",
                  "apple-still.jpg", "apple-unrelated-name.mov" })
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
        const string appleIdentifier = "123e4567-e89b-12d3-a456-426614174000";
        await File.WriteAllBytesAsync(Path.Combine(folder, "apple-still.jpg"),
            AddAppleExif(image, appleIdentifier));
        await File.WriteAllBytesAsync(Path.Combine(folder, "apple-unrelated-name.mov"),
            AddAppleMetadataToMovie(video, appleIdentifier));
    }

    private static byte[] AddAppleExif(byte[] jpeg, string identifier)
    {
        byte[] makerNote = CreateAppleMakerNote(identifier);
        const int makeOffset = 38;
        const int exifIfdOffset = 44;
        const int makerNoteOffset = 62;
        var tiff = new byte[makerNoteOffset + makerNote.Length];
        tiff[0] = (byte)'I';
        tiff[1] = (byte)'I';
        WriteUInt16LittleEndian(tiff, 2, 42);
        WriteUInt32LittleEndian(tiff, 4, 8);
        WriteUInt16LittleEndian(tiff, 8, 2);
        WriteIfdEntry(tiff, 10, 0x010f, 2, 6, makeOffset);
        WriteIfdEntry(tiff, 22, 0x8769, 4, 1, exifIfdOffset);
        WriteUInt32LittleEndian(tiff, 34, 0);
        Encoding.ASCII.GetBytes("Apple\0").CopyTo(tiff, makeOffset);
        WriteUInt16LittleEndian(tiff, exifIfdOffset, 1);
        WriteIfdEntry(tiff, exifIfdOffset + 2, 0x927c, 7, makerNote.Length, makerNoteOffset);
        WriteUInt32LittleEndian(tiff, exifIfdOffset + 14, 0);
        makerNote.CopyTo(tiff, makerNoteOffset);

        byte[] prefix = Encoding.ASCII.GetBytes("Exif\0\0");
        using var output = new MemoryStream();
        output.Write(jpeg.AsSpan(0, 2));
        output.Write([0xff, 0xe1]);
        WriteUInt16BigEndian(output, checked(2 + prefix.Length + tiff.Length));
        output.Write(prefix);
        output.Write(tiff);
        output.Write(jpeg.AsSpan(2));
        return output.ToArray();
    }

    private static byte[] CreateAppleMakerNote(string identifier)
    {
        byte[] text = Encoding.ASCII.GetBytes(identifier + "\0");
        var note = new byte[18 + text.Length];
        WriteUInt16LittleEndian(note, 0, 1);
        WriteIfdEntry(note, 2, 0x0011, 2, text.Length, 18);
        WriteUInt32LittleEndian(note, 14, 0);
        text.CopyTo(note, 18);
        return note;
    }

    private static byte[] AddAppleMetadataToMovie(byte[] movie, string identifier)
    {
        byte[] identifierKey = Encoding.UTF8.GetBytes("com.apple.quicktime.content.identifier");
        byte[] stillTimeKey = Encoding.UTF8.GetBytes("com.apple.quicktime.still-image-time");
        var keys = new List<byte>();
        keys.AddRange(new byte[4]);
        WriteUInt32BigEndian(keys, 2);
        AddQuickTimeKey(keys, identifierKey);
        AddQuickTimeKey(keys, stillTimeKey);

        byte[] identifierData = Atom("data",
            [.. UInt32BigEndian(1), .. new byte[4], .. Encoding.UTF8.GetBytes(identifier)]);
        byte[] timingData = Atom("data", [.. UInt32BigEndian(21), .. new byte[4], 0xff]);
        byte[] itemList = [.. Atom(1, identifierData), .. Atom(2, timingData)];
        byte[] handler = Atom("hdlr",
            [.. new byte[8], .. Encoding.ASCII.GetBytes("mdta"), .. new byte[4]]);
        byte[] metadata = Atom("meta",
            [.. new byte[4], .. handler, .. Atom("keys", keys.ToArray()), .. Atom("ilst", itemList)]);

        using var output = new MemoryStream();
        int position = 0;
        bool inserted = false;
        while (position < movie.Length)
        {
            if (movie.Length - position < 8)
                throw new InvalidDataException("Generated MP4 has a truncated top-level atom.");
            uint size = BinaryPrimitives.ReadUInt32BigEndian(movie.AsSpan(position, 4));
            if (size < 8 || size > movie.Length - position)
                throw new InvalidDataException("Generated MP4 has an invalid top-level atom size.");
            if (Encoding.ASCII.GetString(movie, position + 4, 4) == "moov")
            {
                if (inserted)
                    throw new InvalidDataException("Generated MP4 has multiple movie atoms.");
                WriteUInt32BigEndian(output, checked(size + (uint)metadata.Length));
                output.Write(movie.AsSpan(position + 4, 4));
                output.Write(movie.AsSpan(position + 8, checked((int)size - 8)));
                output.Write(metadata);
                inserted = true;
            }
            else
                output.Write(movie.AsSpan(position, checked((int)size)));
            position = checked(position + (int)size);
        }
        if (!inserted)
            throw new InvalidDataException("Generated MP4 has no movie atom.");
        return output.ToArray();
    }

    private static void AddQuickTimeKey(List<byte> keys, byte[] key)
    {
        WriteUInt32BigEndian(keys, checked((uint)(key.Length + 8)));
        keys.AddRange(Encoding.ASCII.GetBytes("mdta"));
        keys.AddRange(key);
    }

    private static byte[] Atom(string type, byte[] payload) =>
        Atom(BinaryPrimitives.ReadUInt32BigEndian(Encoding.ASCII.GetBytes(type)), payload);

    private static byte[] Atom(uint type, byte[] payload)
    {
        using var atom = new MemoryStream();
        WriteUInt32BigEndian(atom, checked((uint)(payload.Length + 8)));
        WriteUInt32BigEndian(atom, type);
        atom.Write(payload);
        return atom.ToArray();
    }

    private static byte[] UInt32BigEndian(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static void WriteIfdEntry(byte[] bytes, int offset, ushort tag, ushort type, int count, int value)
    {
        WriteUInt16LittleEndian(bytes, offset, tag);
        WriteUInt16LittleEndian(bytes, offset + 2, type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 4), checked((uint)count));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 8), checked((uint)value));
    }

    private static void WriteUInt16LittleEndian(byte[] bytes, int offset, int value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), checked((ushort)value));

    private static void WriteUInt16BigEndian(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, checked((ushort)value));
        stream.Write(bytes);
    }

    private static void WriteUInt32LittleEndian(byte[] bytes, int offset, int value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), checked((uint)value));

    private static void WriteUInt32BigEndian(List<byte> bytes, uint value) =>
        bytes.AddRange(UInt32BigEndian(value));

    private static void WriteUInt32BigEndian(Stream stream, uint value) =>
        stream.Write(UInt32BigEndian(value));

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
            <rdf:li><container:Item item:Mime="video/mp4" item:Semantic="MotionPhoto" item:Length="{length}" item:Padding="0" /></rdf:li>
            </rdf:Seq></container:Directory></rdf:Description></rdf:RDF>
            """;
        byte[] payload = Encoding.UTF8.GetBytes("http://ns.adobe.com/xap/1.0/\0" + xmp);
        byte[] size = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(size, checked((ushort)(payload.Length + 2)));
        return [.. image[..2], 0xff, 0xe1, .. size, .. payload, .. image[2..], .. video];
    }
}
