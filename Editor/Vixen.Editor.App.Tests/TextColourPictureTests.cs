// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Editor.Testing;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>
///     A panel line whose sheet colours it is drawn in that colour — read off the editor's own frame,
///     in the software rasteriser and on the Vulkan renderer when a device opens (#1372).
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The defect was invisible to every computed style but one.</b> Markup puts a line's
///         words in a child <c>text</c> element, and <c>ControlTheme.vcss</c> matched that child with
///         <c>text { color: var(--text); }</c> — so <c>statistic-warning</c>'s orange and
///         <c>gpu-status</c>'s muted grey were right on the element and the glyphs were drawn in the
///         text colour. A census of every panel this session opens found nineteen such lines; this
///         reads two of them off the picture.
///     </para>
///     <para>
///         ⚠ <b>The oracle is closed-form.</b> Over the words' own box, the ground is the commonest
///         pixel and the most inked pixel must lie on the line from the ground to the element's
///         colour — at most a little past full coverage, and not off to one side. Ink of the text
///         colour fails it both ways: off to the side of orange, and far past the end of a muted grey,
///         which is darker than the text colour on this palette.
///     </para>
/// </remarks>
public sealed class TextColourPictureTests {
    [Theory]
    [InlineData("statistics", "statistic-warning")]
    [InlineData("gpu", "gpu-status")]
    public void A_panel_line_is_drawn_in_the_colour_its_sheet_gives_it(string panel, string tag) {
        using var fixture = ScrollingPanelPictureTests.Start();

        fixture.Open(panel);
        fixture.Frames(3);

        var document = fixture.Document;
        var color = document.PropertyId("color");

        var line = Descendants(document.Root).FirstOrDefault(element => element.Tag == tag)
            ?? throw fixture.Fail($"the {panel} panel shows no <{tag}>");

        var words = Assert.Single(line.Children, child => child.Tag == "text");
        Assert.False(string.IsNullOrEmpty(words.Text), $"<{tag}> has no words to draw");

        var want = document.ColorOf(line.Style, color) ?? throw fixture.Fail($"<{tag}> resolved no colour");
        var plain = document.ColorOf(document.Root.Style, color) ?? throw fixture.Fail("the root resolved no colour");

        // The line's colour has to be one the text colour could not be mistaken for, or the picture
        // below proves nothing either way.
        Assert.True(Distance(want, plain) > 0.2f, $"<{tag}>'s colour {want} is too close to the text colour {plain}");

        using var device = ScrollingPanelPictureTests.OpenDevice();
        using var gpu = device is null
            ? null
            : new ScrollingPanelPictureTests.GpuPicture(
                device,
                ScrollingPanelPictureTests.WidthOf(fixture),
                ScrollingPanelPictureTests.HeightOf(fixture)
            );

        var (software, hardware) = ScrollingPanelPictureTests.Draw(fixture, gpu, $"text-colour-{panel}");

        // ⚠ Both renderers are judged before either fails. Failing on the software picture first
        // left the Vulkan one unread in every red run, so the GPU half of the claim rested on
        // captures measured by hand rather than on this test.
        List<string> failures = [];
        Check(software, words, want, "software", failures);

        if (hardware is { } picture) {
            // Named, so a picture read from the capture directory says which device drew it.
            var adapter = device!.Adapter.Name;

            if (Environment.GetEnvironmentVariable("VIXEN_PANEL_CAPTURE") is { Length: > 0 } directory) {
                File.WriteAllText(Path.Combine(directory, $"text-colour-{panel}-adapter.txt"), adapter);
            }

            Check(picture, words, want, $"vulkan on '{adapter}'", failures);
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    static void Check(Bitmap image, UiElement words, Color4 want, string renderer, List<string> failures) {
        var left = (int)MathF.Ceiling(words.AbsoluteLeft);
        var top = (int)MathF.Ceiling(words.AbsoluteTop);
        var right = Math.Min(image.Width, (int)MathF.Floor(words.AbsoluteLeft + words.Width));
        var bottom = Math.Min(image.Height, (int)MathF.Floor(words.AbsoluteTop + words.Height));

        if (right <= left || bottom <= top) {
            failures.Add($"[{renderer}] the words have no box on screen");
            return;
        }

        var counts = new Dictionary<(byte, byte, byte), int>();

        for (var y = top; y < bottom; y++) {
            for (var x = left; x < right; x++) {
                var key = Pixel(image, x, y);
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }

        var ground = Of(counts.MaxBy(static entry => entry.Value).Key);
        var inked = ground;
        var furthest = 0f;

        for (var y = top; y < bottom; y++) {
            for (var x = left; x < right; x++) {
                var sample = Of(Pixel(image, x, y));
                var distance = Distance(sample, ground);

                if (distance > furthest) {
                    (furthest, inked) = (distance, sample);
                }
            }
        }

        if (furthest <= 0.05f) {
            failures.Add($"[{renderer}] nothing was drawn in the words' box");
            return;
        }

        // Where the most inked pixel sits on the line from the ground to the wanted colour, and how
        // far off that line it is.
        var toward = (R: want.R - ground.R, G: want.G - ground.G, B: want.B - ground.B);
        var drawn = (R: inked.R - ground.R, G: inked.G - ground.G, B: inked.B - ground.B);
        var length = (toward.R * toward.R) + (toward.G * toward.G) + (toward.B * toward.B);
        var along = ((drawn.R * toward.R) + (drawn.G * toward.G) + (drawn.B * toward.B)) / length;
        var aside = MathF.Sqrt(
            MathF.Pow(drawn.R - (along * toward.R), 2) + MathF.Pow(drawn.G - (along * toward.G), 2) + MathF.Pow(drawn.B - (along * toward.B), 2)
        );

        if (along is not (> 0.5f and < 1.1f) || aside >= 0.08f) {
            failures.Add(
                $"[{renderer}] the most inked pixel {inked} over the ground {ground} is {along:0.00} of the way to {want} and {aside:0.000} off it"
            );
        }
    }

    static (byte, byte, byte) Pixel(Bitmap image, int x, int y) {
        var at = image.Offset(x, y);
        return (image.Pixels[at], image.Pixels[at + 1], image.Pixels[at + 2]);
    }

    static Color4 Of((byte R, byte G, byte B) pixel) => new(pixel.R / 255f, pixel.G / 255f, pixel.B / 255f, 1f);

    static float Distance(Color4 a, Color4 b) =>
        MathF.Sqrt(((a.R - b.R) * (a.R - b.R)) + ((a.G - b.G) * (a.G - b.G)) + ((a.B - b.B) * (a.B - b.B)));

    static IEnumerable<UiElement> Descendants(UiElement element) {
        yield return element;

        foreach (var child in element.Children) {
            foreach (var descendant in Descendants(child)) {
                yield return descendant;
            }
        }
    }
}
