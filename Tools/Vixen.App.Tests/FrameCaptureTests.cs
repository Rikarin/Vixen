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

    /// <summary>
    ///     ⚠ A run that can present is refused <em>and says so</em>, which is the half that did not
    ///     exist.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1108">#1108</a>, and it is this
    ///         repository's named failure shape rather than a missing convenience:
    ///         <c>--vixen-frames 6 --vixen-capture ./shots</c> on a desktop rendered six frames,
    ///         printed every counter a sample logs, printed "Stopping after 6 frames.", exited zero
    ///         and wrote nothing — with no line anywhere saying why. Two runs were spent on it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The message names <c>--vixen-headless</c> and warns off
    ///         <c>--vixen-offscreen</c>, which is a correction to the issue.</b> Offscreen is parsed
    ///         into <c>GraphicsOptions.Offscreen</c> and decides which backend may answer — it is
    ///         what turns the Null fall-through into a boot failure — and reaches neither the window
    ///         nor the surface. A desktop run given it opens a window and lands right back here, so
    ///         suggesting it would have sent the operator round the same loop a third time.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The refusal itself is not new and asserting only that would prove nothing</b> —
    ///         <c>ARequestWithNoDirectoryIsRefused</c> already covers a false return. What is under
    ///         test is that the run is no longer silent, so the log record is the assertion and the
    ///         false is the precondition.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ARunThatCanPresentIsRefusedAndSaysSo() {
        using var device = new Graphics.Null.NullDevice(new() { Record = true });
        using var application = Presenting(device, new WindowedCapturingGame(directory));

        application.Initialise();

        Assert.NotNull(application.Services.Window);
        Assert.False(application.Services.Graphics!.RequestCapture("frame"));
        Assert.Null(application.Services.Graphics!.LastCapturePath);
        Assert.False(Directory.Exists(directory));

        Assert.Contains(
            application.Services.Logs.Snapshot(),
            entry => entry.EventId == 13033 && entry.Message.Contains(directory, StringComparison.Ordinal)
        );

        // ⚠ Once, however often it is asked. The one caller asks on the last frame only, so a
        // per-call line is invisible today and would be a wall of text the moment a head captured
        // more than one frame — which is the shape a warning has to survive to stay readable.
        application.Services.Graphics!.RequestCapture("frame");
        application.Services.Graphics!.RequestCapture("frame");

        Assert.Single(application.Services.Logs.Snapshot(), entry => entry.EventId == 13033);
    }

    /// <summary>
    ///     And the same run without a capture directory says nothing, because there is nothing wrong
    ///     with it.
    /// </summary>
    /// <remarks>
    ///     The instrument check. A warning that fired on every windowed run would be indistinguishable
    ///     from the one above in every assertion it makes, and would train an operator to ignore the
    ///     line that matters.
    /// </remarks>
    [Fact]
    public void ARunThatCanPresentAndAskedForNoPictureIsQuiet() {
        using var device = new Graphics.Null.NullDevice(new() { Record = true });
        using var application = Presenting(device, new WindowedGame());

        application.Initialise();

        Assert.False(application.Services.Graphics!.RequestCapture("frame"));
        Assert.DoesNotContain(application.Services.Logs.Snapshot(), entry => entry.EventId == 13033);
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

    /// <summary>A window and no capture at all: the ordinary desktop run.</summary>
    sealed class WindowedGame : Game {
        protected internal override void OnConfigure(AppConfig config) {
            config.Window = new();
        }
    }

    /// <summary>The same application on a platform whose window can actually be presented to.</summary>
    /// <remarks>
    ///     See <see cref="PresentingPlatform" /> for why the headless one cannot stand in here: its
    ///     surface is <c>SurfaceKind.None</c> unconditionally, which is the branch every other case
    ///     in this file exercises.
    /// </remarks>
    VixenApplication Presenting(Graphics.IGraphicsDevice device, Game game) =>
        VixenApp.Create(["--vixen-workers", "1", "--vixen-frame-limit", "0"])
            .WithPlatform(new PresentingPlatform(new HeadlessPlatform(new HeadlessPlatformOptions { FileSystem = files })))
            .WithGraphics(device)
            .Build(game);
}
