// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Platform;
using Vixen.Platform.Headless;
using Vixen.Ui.Composition;
using Xunit;

namespace Vixen.Ui.Desktop.Tests;

/// <summary><c>--vixen-capture</c>: the whole application, drawn on a real device, written to a file.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Until #1367 no <c>Vixen.Ui</c> application could be pictured whole.</b>
///         <c>UiApplication.Run</c> read <c>--vixen-frames</c> and ignored every other flag, and it
///         always opened a visible window. Every control was pictured only on its own, in a test
///         document, and the gallery that composes them with an application's own sheet had been
///         seen only by a person running the sample.
///     </para>
///     <para>
///         <b>The oracle is closed-form.</b> A 200×100 box of one pure colour, at a known place, on
///         a ground of another pure colour. The picture must have the box's colour exactly inside
///         that rectangle and the ground's colour everywhere else, and the red coverage summed over
///         the image must be the rectangle's area. A capture of the wrong texture, the wrong frame, the
///         wrong channel order or nothing at all fails one of those.
///     </para>
///     <para>
///         <b>On the headless platform, which has no surface at all.</b> That makes it the strictest
///         case: an offscreen run must not need the window to have anything but a size. A machine with
///         no Vulkan skips, unless <c>VIXEN_REQUIRE_VULKAN=1</c> is set.
///     </para>
/// </remarks>
[Collection(SerialUiDevelopment.Name)]
public class UiApplicationCaptureTests {
    /// <summary>One box, and nothing else that could draw.</summary>
    sealed class Box : Component {
        public UiElement Element { get; private set; } = null!;

        protected override void Build(BuildContext ctx) => Element = ctx.Element(Root, "capture-box");
    }

    /// <summary>How many frames every capture here runs. The last one is the one written.</summary>
    const int Frames = 3;

    const int Left = 100;
    const int Top = 50;
    const int BoxWidth = 200;
    const int BoxHeight = 100;

    /// <summary>The sheet that places the box, in document pixels, which are device pixels at scale one.</summary>
    static readonly string Sheet = $$"""
        capture-box {
            position: absolute;
            left: {{Left}}px;
            top: {{Top}}px;
            width: {{BoxWidth}}px;
            height: {{BoxHeight}}px;
            background-color: #ff0000;
        }
        """;

    static string Scratch() => Path.Combine(Path.GetTempPath(), $"vixen-ui-capture-{Guid.NewGuid():N}");

    /// <summary>The arguments reach the options, in both of the spellings a command line uses.</summary>
    [Fact]
    public void TheCaptureArgumentsAreRead() {
        var spaced = new UiApplicationOptions();
        UiApplication.Apply(spaced, ["--vixen-frames", "3", "--vixen-capture", "shots", "--vixen-size", "800x2000"]);

        Assert.Equal(3, spaced.Frames);
        Assert.Equal("shots", spaced.CapturePath);
        Assert.Equal(new Int2(800, 2000), spaced.Size);
        Assert.False(spaced.Offscreen);

        var inline = new UiApplicationOptions();
        UiApplication.Apply(inline, ["--vixen-capture=shots", "--vixen-frames=5", "--vixen-offscreen"]);

        Assert.Equal(5, inline.Frames);
        Assert.Equal("shots", inline.CapturePath);
        Assert.True(inline.Offscreen);
    }

    /// <summary>A flag is not taken as the value of the flag before it.</summary>
    /// <remarks>
    ///     Otherwise <c>--vixen-capture --vixen-frames 3</c> would capture into a directory called
    ///     <c>--vixen-frames</c>, and with no frame count the hidden window would run for ever.
    /// </remarks>
    [Fact]
    public void AFlagIsNotTakenAsACaptureDirectory() {
        var options = new UiApplicationOptions();
        UiApplication.Apply(options, ["--vixen-capture", "--vixen-frames", "3", "app-argument"]);

        Assert.Null(options.CapturePath);
        Assert.Equal(3, options.Frames);
    }

    /// <summary>An offscreen run with no frame count is refused instead of running for ever.</summary>
    [Fact]
    public void AnOffscreenRunWithNoFrameCountIsRefused() {
        var platform = new HeadlessPlatform();
        var window = platform.CreateWindow(new WindowOptions { Title = "test", Size = new Int2(64, 64) });

        using var application = new UiApplication(
            new UiApplicationOptions { CapturePath = Scratch(), InstallSystemFont = false, Content = () => new Box() },
            platform,
            window
        );

        var refused = Assert.Throws<InvalidOperationException>(() => application.Run());
        Assert.Contains("--vixen-frames", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>The last frame is written, and it is the frame the application drew.</summary>
    [Fact]
    public void TheLastFrameIsWrittenWithTheBoxWhereTheLayoutPutIt() {
        if (Capture(Sheet, () => new Box(), "box") is not { } picture) {
            return;
        }

        Assert.Equal(640, picture.Width);
        Assert.Equal(400, picture.Height);

        Covers(picture, Left, Top, BoxWidth, BoxHeight);
    }

    /// <summary>The picture is of the last frame, not of an earlier one that happens to look the same.</summary>
    /// <remarks>
    ///     ⚠ <b>The box above cannot tell frames apart.</b> Its document is the same on every frame, so
    ///     a capture of the first frame passes it too: measured, with <c>CapturingThisFrame</c> set to
    ///     frame 0 all of the tests above stayed green. Here the box moves on the last frame and only
    ///     there, so a capture of any earlier frame finds it at its first place.
    /// </remarks>
    [Fact]
    public void TheCaptureIsOfTheLastFrameAndNotAnEarlierOne() {
        const int MovedLeft = 380;
        const int MovedTop = 240;

        var sheet = Sheet + $$"""

            capture-box.moved { left: {{MovedLeft}}px; top: {{MovedTop}}px; }
            """;

        Box? built = null;

        // `Frame` runs before the frame's update and draw, so a class added there is in that frame.
        if (Capture(
                sheet,
                () => built = new Box(),
                "last-frame",
                frame: (application, _) => {
                    if (application.FrameCount == Frames - 1) {
                        built!.Element.AddClass("moved");
                    }
                }
            ) is not { } picture) {
            return;
        }

        Covers(picture, MovedLeft, MovedTop, BoxWidth, BoxHeight);
    }

    /// <summary>A zero-wide box whose child overflows it, drawn by the device (#1375).</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The picture of the fixture #1375 measured in a draw list.</b> A flex item with
    ///         <c>container-type: inline-size</c> and no width is 0 wide by containment, and its
    ///         120×30 child is laid out beside it. The child took the pointer and drew nothing,
    ///         because the paint walk returned at the zero box and took the subtree with it.
    ///     </para>
    ///     <para>
    ///         The same closed-form oracle as the box above: the child's colour exactly inside its
    ///         rectangle, the ground everywhere else, and the summed coverage equal to its area. With
    ///         the early return restored the child's rectangle is ground, so the first pixel read fails.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AChildOverflowingAZeroWideBoxIsDrawnByTheDevice() {
        var zero = $$"""
            capture-zero {
                position: absolute;
                left: {{Left}}px;
                top: {{Top}}px;
                display: flex;
                flex-direction: row;
                align-items: flex-start;
            }

            capture-zero-box { display: block; container-type: inline-size; }
            capture-zero-child { display: block; width: 120px; height: 30px; background-color: #ff0000; }
            """;

        ZeroWide? built = null;
        var widths = (Box: -1f, Child: -1f);

        // Read in `Stopping`, while the document is still alive. The application disposes it.
        if (Capture(zero, () => built = new ZeroWide(), "zero-wide", _ => widths = (built!.Box.Width, built.Child.Width))
            is not { } picture) {
            return;
        }

        // The fixture is the one the issue named, or the picture proves nothing about it.
        Assert.Equal(0f, widths.Box);
        Assert.Equal(120f, widths.Child);

        Covers(picture, Left, Top, 120, 30);
    }

    /// <summary>A zero-wide query container holding a 120×30 child.</summary>
    sealed class ZeroWide : Component {
        public UiElement Box { get; private set; } = null!;

        public UiElement Child { get; private set; } = null!;

        protected override void Build(BuildContext ctx) {
            var row = ctx.Element(Root, "capture-zero");
            Box = ctx.Element(row, "capture-zero-box");
            Child = ctx.Element(Box, "capture-zero-child");
        }
    }

    /// <summary>Runs an application of 640×400 for three frames with a capture, and loads the picture.</summary>
    /// <param name="sheet">The application's stylesheet.</param>
    /// <param name="content">Its content.</param>
    /// <param name="name">What to call a copy kept under <c>VIXEN_UI_CAPTURE</c>, when that names a directory.</param>
    /// <param name="stopping">Runs as the loop stops, while the document is still alive.</param>
    /// <param name="frame">Runs at the start of every frame, before that frame's update and draw.</param>
    /// <returns>The picture, or <see langword="null" /> when there is no device and none is required.</returns>
    static Bitmap? Capture(
        string sheet,
        Func<Component> content,
        string name,
        Action<UiApplication>? stopping = null,
        Action<UiApplication, UiFrame>? frame = null
    ) {
        var directory = Scratch();

        var options = new UiApplicationOptions {
            Title = "capture",
            Size = new Int2(640, 400),
            Frames = Frames,
            CapturePath = directory,
            InstallSystemFont = false,

            // Pure blue, so that neither the red under test nor anything else can be mistaken for it.
            Ground = new Color4(0f, 0f, 1f, 1f),
            Content = content,
            Stopping = stopping,
            Frame = frame
        };

        options.Styles.Add(sheet);

        var platform = new HeadlessPlatform();
        var window = platform.CreateWindow(new WindowOptions { Title = "capture", Size = new Int2(640, 400) });

        int code;
        string? written;

        try {
            using var application = new UiApplication(options, platform, window);

            try {
                code = application.Run();
            } catch (PlatformNotSupportedException missing) {
                if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
                    Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and the capture could not open a device: {missing.Message}");
                }

                Assert.Skip($"No Vulkan device: {missing.Message}");
                return null;
            }

            written = application.LastCapturePath;
        } finally {
            platform.Dispose();
        }

        try {
            Assert.Equal(0, code);
            Assert.Equal(Path.Combine(directory, "frame.png"), written);
            Assert.True(File.Exists(written), "the run said it captured and no file is there.");

            if (Environment.GetEnvironmentVariable("VIXEN_UI_CAPTURE") is { Length: > 0 } keep) {
                Directory.CreateDirectory(keep);
                File.Copy(written!, Path.Combine(keep, $"{name}.png"), overwrite: true);
            }

            return PngCodec.Load(written!);
        } finally {
            if (Directory.Exists(directory)) {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>Asserts that red covers exactly one rectangle of the picture and blue the rest.</summary>
    static void Covers(in Bitmap picture, int left, int top, int width, int height) {
        // Inside, one pixel clear of each edge so that the antialiasing fringe is not what is
        // measured: exactly red.
        for (var y = top + 1; y < top + height - 1; y++) {
            for (var x = left + 1; x < left + width - 1; x++) {
                Assert.Equal((255, 0, 0), Pixel(picture, x, y));
            }
        }

        // Outside, one pixel clear again: exactly the ground.
        var red = 0L;

        for (var y = 0; y < picture.Height; y++) {
            for (var x = 0; x < picture.Width; x++) {
                var (r, _, _) = Pixel(picture, x, y);
                red += r;

                var outside = x < left - 1 || x > left + width || y < top - 1 || y > top + height;

                if (outside) {
                    Assert.Equal((0, 0, 255), Pixel(picture, x, y));
                }
            }
        }

        // The coverage summed over the whole picture is the rectangle's area, to within one pixel of
        // perimeter. It is a closed-form check that the fringe is a fringe and not a second shape.
        // Red is linear in coverage only to within the sRGB encode, so the bound is the perimeter and
        // not zero.
        var area = red / 255.0;
        Assert.InRange(area, (width * height) - (2 * (width + height)), (width * height) + (2 * (width + height)));
    }

    static (int R, int G, int B) Pixel(in Bitmap picture, int x, int y) {
        var offset = ((y * picture.Width) + x) * 4;
        var pixels = picture.Pixels;

        return (pixels[offset], pixels[offset + 1], pixels[offset + 2]);
    }
}