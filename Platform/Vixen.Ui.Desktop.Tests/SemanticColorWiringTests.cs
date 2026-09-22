// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Platform;
using Vixen.Platform.Headless;
using Vixen.Ui.Composition;
using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Desktop.Tests;

/// <summary>That this host reads the platform's own palette, at boot and on a change.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The four production call sites of <c>PlatformInput.ApplySemanticColors</c> and
///         <c>ApplyAccent</c> were covered by nothing, in either host.</b> A sweep for callers over
///         <c>.cs</c> and <c>.vxml</c> found one test file naming either method —
///         <c>SystemPaletteWiringTests</c> — and it drives <c>PlatformInput</c> directly and never
///         builds a host. So deleting both lines from <see cref="UiApplication" /> left every suite
///         in the tree green while <c>color: CanvasText</c> silently stopped following the platform
///         in this host alone, which is the two-renderers hazard with the two hosts in the renderers'
///         place. <c>EditorHostTests</c> is the other half.
///     </para>
///     <para>
///         ⚠ <b>Two tests and not one, because the host reads the platform in two places and either
///         one alone looks like a wire.</b> The seed runs before the first frame — no desktop posts
///         an event for the appearance the machine already had — and the handler runs on
///         <see cref="PlatformEventKind.SystemColorSchemeChanged" />, which is also the event an
///         accent or palette move rides. A host with only the seed comes up right and never follows;
///         a host with only the handler follows and comes up wrong. The two below fail one each.
///     </para>
///     <para>
///         <b>Asserted through the palette rather than through a drawn rectangle</b>, which is where
///         this file differs from <c>ColorSchemeWiringTests</c> beside it. What a media query does is
///         pick a rule, so a width is the only honest evidence it fired; what a platform palette does
///         is fill a cell that <c>SystemColor</c> names, and
///         <c>SystemPaletteWiringTests.A_platform_label_colour_reaches_a_sheet…</c> already carries
///         the cell-to-sheet half through a frame. Duplicating it here would put a second copy of
///         that claim under a name about wiring.
///     </para>
/// </remarks>
[Collection(SerialUiDevelopment.Name)]
public class SemanticColorWiringTests {
    /// <summary>A palette no default table could produce, so a match cannot be a coincidence.</summary>
    /// <remarks>
    ///     ⚠ Both halves of the assertion are needed and the second is the one that catches a
    ///     forgotten conversion: <see cref="SystemPalette" /> holds linear and every platform reports
    ///     sRGB, so a host that handed the numbers straight over would make a palette that is visibly
    ///     too bright with nothing anywhere reporting it.
    /// </remarks>
    static readonly Color4 Label = new(0.42f, 0.13f, 0.77f, 1f);

    static readonly Color4 Paper = new(0.11f, 0.83f, 0.29f, 1f);

    static SystemSemanticColors Palette => new(Canvas: Paper, CanvasText: Label);

    sealed class Probe : Component {
        protected override void Build(BuildContext ctx) => ctx.Element(Root, "probe-panel");
    }

    /// <summary>Runs a headless application over a platform a test has set up.</summary>
    /// <param name="before">Given the platform before the loop starts.</param>
    /// <param name="frames">How many frames to run.</param>
    /// <param name="each">Given the platform and the frame index once a frame.</param>
    /// <returns>The application, for its document.</returns>
    /// <remarks>
    ///     ⚠ <b>The queue is drained between <paramref name="before" /> and the loop, and without
    ///     that line this whole file proves nothing.</b> Setting a platform setting queues a
    ///     <see cref="PlatformEventKind.SystemColorSchemeChanged" /> — deliberately, because that is
    ///     what a desktop's poll does — so a test that set the palette and started the loop would
    ///     have the seed and the handler apply the same value on the same run. Deleting the seed
    ///     then leaves the test green, which is exactly what it did the first time it was written.
    ///     Draining first is what makes the seed the only thing that could have supplied the colour.
    /// </remarks>
    static UiApplication Run(
        Action<HeadlessPlatform> before,
        int frames = 2,
        Action<HeadlessPlatform, int>? each = null
    ) {
        var platform = new HeadlessPlatform();
        var frame = 0;

        before(platform);
        platform.PumpEvents();

        var options = new UiApplicationOptions {
            Title = "test",
            Size = new Int2(400, 300),
            Frames = frames,
            InstallSystemFont = false,
            InstallControlTheme = false,
            Content = () => new Probe(),
            Frame = each is null ? null : (_, _) => each(platform, frame++)
        };

        var window = platform.CreateWindow(new WindowOptions { Title = "test", Size = new Int2(400, 300) });
        var application = new UiApplication(options, platform, window);

        application.Run();

        return application;
    }

    /// <summary>The palette the machine already had reaches the document before the first frame.</summary>
    /// <remarks>
    ///     ⚠ <b>The accent rides along, and it is asserted here rather than in a file of its own
    ///     because it was uncovered for exactly the same reason.</b> <c>ApplyAccent</c> sits on the
    ///     same two lines as <c>ApplySemanticColors</c> in both hosts and had no host-level test
    ///     either; one run can ask about both, and a run that asked about only one would leave the
    ///     other in the state this file exists to get out of.
    /// </remarks>
    [Fact]
    public void The_palette_the_platform_already_had_is_read_before_the_first_frame() {
        var accent = new Color4(0.5f, 0.25f, 0.75f, 1f);

        var application = Run(
            platform => {
                platform.SemanticColors = Palette;
                platform.Accent = new SystemAccent(accent, new Color4(1f, 1f, 1f, 1f));
            }
        );

        var colours = application.Document.SystemColors;

        Assert.True(colours.IsFromPlatform(SystemColor.CanvasText), "the host read no semantic palette.");
        Assert.Equal(Color4.FromSrgb(Label), colours[SystemColor.CanvasText]);
        Assert.Equal(Color4.FromSrgb(Paper), colours[SystemColor.Canvas]);

        // ⚠ And not the sRGB numbers themselves, which is the failure that looks like a working
        // palette until somebody compares two screenshots.
        Assert.NotEqual(Label, colours[SystemColor.CanvasText]);

        Assert.True(colours.IsFromPlatform(SystemColor.AccentColor), "the host read no accent.");
        Assert.Equal(Color4.FromSrgb(accent), colours[SystemColor.AccentColor]);
    }

    /// <summary>And a palette that moves under a running application is picked up.</summary>
    /// <remarks>
    ///     ⚠ <b>Expressed in frames rather than in milliseconds</b>, on the terms
    ///     <c>ColorSchemeWiringTests</c> states them: the platform queues the change when it is set
    ///     and the loop consumes it on its next pump, so "by the end of the run" is a statement about
    ///     order and reads the same on an idle machine and a loaded one.
    /// </remarks>
    [Fact]
    public void A_palette_that_moves_under_a_running_application_is_picked_up() {
        var moved = false;

        var application = Run(
            _ => { },
            frames: 4,
            each: (platform, frame) => {
                if (frame != 0) {
                    return;
                }

                moved = true;
                platform.SemanticColors = Palette;
            }
        );

        Assert.True(moved, "the per-frame hook never ran, so nothing was changed under the loop.");

        var colours = application.Document.SystemColors;

        Assert.True(colours.IsFromPlatform(SystemColor.CanvasText), "the change reached no handler.");
        Assert.Equal(Color4.FromSrgb(Label), colours[SystemColor.CanvasText]);
    }

    /// <summary>A platform with nothing to report leaves the defaults alone.</summary>
    /// <remarks>
    ///     ⚠ <b>The half a host that invented a palette would fail.</b> A headless run genuinely has
    ///     no operating-system colours, and <see cref="SystemSemanticColors.Unknown" /> has to reach
    ///     the document as "keep following the table" rather than as eleven black roles — which is
    ///     what a <c>default</c> struct written through unguarded would mean.
    /// </remarks>
    [Fact]
    public void A_platform_with_no_palette_leaves_the_table_in_place() {
        var application = Run(_ => { });
        var colours = application.Document.SystemColors;

        Assert.False(colours.IsFromPlatform(SystemColor.CanvasText));
        Assert.False(colours.IsFromPlatform(SystemColor.Canvas));
    }
}
