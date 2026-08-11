// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Platform.Headless;
using Xunit;

namespace Vixen.App.Tests;

/// <summary>That a run asked for a picture writes exactly one, on the frame it said it would.</summary>
/// <remarks>
///     <para>
///         Against the Null device, deliberately. Its <c>Read</c> zeroes the destination, so what
///         these produce is a black PNG — and black is not what is under test here. What is under
///         test is the arrangement that makes a real picture possible: that the copy is recorded on
///         the frame's own list before it is finished, that the read happens after the queue has gone
///         idle, that the file lands where it was asked for and is the size the frame was, and that
///         it happens on the <em>last</em> frame rather than every one.
///     </para>
///     <para>
///         The pixels themselves are covered where a real device is available —
///         <c>Vixen.Graphics.Vulkan.Tests.OffscreenSwapChainTests</c> reads back a clear colour — and
///         a test that needed a GPU to prove the plumbing would be a test that does not run in CI.
///     </para>
/// </remarks>
public sealed class FrameCaptureTests : IDisposable {
    readonly TemporaryFileSystemHost files = new();
    readonly string directory = Path.Combine(
        Path.GetTempPath(),
        $"vixen-capture-{Guid.NewGuid():N}"
    );

    /// <inheritdoc />
    public void Dispose() {
        files.Dispose();

        if (Directory.Exists(directory)) {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    ///     The whole feature, end to end on a device with no pixels: three frames, one file, and it
    ///     is a PNG of the frame's own size.
    /// </summary>
    [Fact]
    public void TheLastFrameIsWrittenAsAPicture() {
        using var device = new Graphics.Null.NullDevice(new() { Record = true });

        using var application = Build(device, new CapturingGame(directory, frames: 3));

        application.Initialise();
        application.RunFrame();
        application.RunFrame();

        // ⚠ Nothing yet, which is half the claim. A capture on every frame would pass every
        // assertion below and cost a device-wide wait per frame for the life of the run.
        Assert.Null(application.Services.Graphics!.LastCapturePath);

        application.RunFrame();

        var path = application.Services.Graphics!.LastCapturePath;

        Assert.NotNull(path);
        Assert.True(File.Exists(path));

        var picture = PngCodec.Load(path!);
        var size = application.Services.Config.Graphics.WindowlessSize;

        Assert.Equal(size.X, picture.Width);
        Assert.Equal(size.Y, picture.Height);

        // Opaque, whatever the frame left in its alpha. A viewer that honours a zero alpha shows the
        // picture over its own checkerboard, which reads as a rendering bug rather than as a channel
        // the tonemap had no reason to write.
        Assert.All(Alpha(picture), value => Assert.Equal(255, value));
    }

    /// <summary>
    ///     ⚠ No directory, no capture — and the run is otherwise unaffected. This is the ordinary
    ///     case, and the one that must not pay a readback.
    /// </summary>
    [Fact]
    public void AskingForNoCaptureWritesNothing() {
        using var device = new Graphics.Null.NullDevice(new() { Record = true });
        using var application = Build(device, new CapturingGame(directory: null, frames: 2));

        application.Initialise();
        application.RunFrame();
        application.RunFrame();

        Assert.Null(application.Services.Graphics!.LastCapturePath);
        Assert.False(Directory.Exists(directory));
    }

    /// <summary>
    ///     ⚠ <b>It is the surface that decides, not whether there is a window.</b> A headless window
    ///     is a real window with a real size whose surface is <c>SurfaceKind.None</c>, so a run with
    ///     one is as capturable as a run with none — which is what makes <c>--vixen-headless</c> work
    ///     on a game that sets <c>config.Window</c> unconditionally, as every sample does.
    /// </summary>
    [Fact]
    public void AHeadlessWindowIsStillCapturable() {
        using var device = new Graphics.Null.NullDevice(new() { Record = true });
        using var application = Build(device, new WindowedCapturingGame(directory));

        application.Initialise();

        Assert.NotNull(application.Services.Window);
        Assert.True(application.Services.Graphics!.RequestCapture("frame"));
    }

    /// <summary>Asking for a picture with nowhere to put it is refused rather than guessed at.</summary>
    [Fact]
    public void ARequestWithNoDirectoryIsRefused() {
        using var device = new Graphics.Null.NullDevice(new() { Record = true });
        using var application = Build(device, new CapturingGame(directory: null, frames: 0));

        application.Initialise();

        Assert.False(application.Services.Graphics!.RequestCapture("frame"));
    }

    static IEnumerable<byte> Alpha(Bitmap picture) {
        for (var offset = 3; offset < picture.Pixels.Length; offset += 4) {
            yield return picture.Pixels[offset];
        }
    }

    VixenApplication Build(Graphics.IGraphicsDevice device, Game game) =>
        VixenApp.Create(["--vixen-workers", "1", "--vixen-frame-limit", "0"])
            .WithPlatform(new HeadlessPlatform(new HeadlessPlatformOptions { FileSystem = files }))
            .WithGraphics(device)
            .Build(game);

    /// <summary>A head with no window, which is what a capture run is.</summary>
    sealed class CapturingGame(string? directory, int frames) : Game {
        protected internal override void OnConfigure(AppConfig config) {
            config.Window = null;
            config.MaxFrames = frames;
            config.Graphics.CapturePath = directory;
        }
    }

    /// <summary>The same, with the window every sample asks for unconditionally.</summary>
    sealed class WindowedCapturingGame(string directory) : Game {
        protected internal override void OnConfigure(AppConfig config) {
            config.Window = new();
            config.Graphics.CapturePath = directory;
        }
    }
}
