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
        protected override void Build(BuildContext ctx) => ctx.Element(Root, "capture-box");
    }

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
        var directory = Scratch();

        var options = new UiApplicationOptions {
            Title = "capture",
            Size = new Int2(640, 400),
            Frames = 3,
            CapturePath = directory,
            InstallSystemFont = false,

            // Pure blue, so that neither the box's red nor anything else can be mistaken for it.
            Ground = new Color4(0f, 0f, 1f, 1f),
            Content = () => new Box()
        };

        options.Styles.Add(Sheet);

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
                return;
            }

            written = application.LastCapturePath;
        } finally {
            platform.Dispose();
        }

        try {
            Assert.Equal(0, code);
            Assert.Equal(Path.Combine(directory, "frame.png"), written);
            Assert.True(File.Exists(written), "the run said it captured and no file is there.");

            var picture = PngCodec.Load(written!);

            Assert.Equal(640, picture.Width);
            Assert.Equal(400, picture.Height);

            // Inside the box, one pixel clear of each edge so that the antialiasing fringe is not
            // what is measured: exactly the box's colour.
            for (var y = Top + 1; y < Top + BoxHeight - 1; y++) {
                for (var x = Left + 1; x < Left + BoxWidth - 1; x++) {
                    Assert.Equal((255, 0, 0), Pixel(picture, x, y));
                }
            }

            // Outside it, one pixel clear again: exactly the ground.
            var red = 0L;

            for (var y = 0; y < picture.Height; y++) {
                for (var x = 0; x < picture.Width; x++) {
                    var (r, _, _) = Pixel(picture, x, y);
                    red += r;

                    var outside = x < Left - 1 || x > Left + BoxWidth || y < Top - 1 || y > Top + BoxHeight;

                    if (outside) {
                        Assert.Equal((0, 0, 255), Pixel(picture, x, y));
                    }
                }
            }

            // The coverage summed over the whole picture is the box's area, to within one pixel of
            // perimeter. It is a closed-form check that the fringe is a fringe and not a second box.
            // Red is linear in coverage only to within the sRGB encode, so the bound is the perimeter
            // and not zero.
            var area = red / 255.0;
            Assert.InRange(area, BoxWidth * BoxHeight - 2 * (BoxWidth + BoxHeight), BoxWidth * BoxHeight + 2 * (BoxWidth + BoxHeight));
        } finally {
            if (Directory.Exists(directory)) {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    static (int R, int G, int B) Pixel(in Bitmap picture, int x, int y) {
        var offset = ((y * picture.Width) + x) * 4;
        var pixels = picture.Pixels;

        return (pixels[offset], pixels[offset + 1], pixels[offset + 2]);
    }
}
