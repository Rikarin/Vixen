// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>What <see cref="Image" /> puts in the draw list, and which of its two branches it takes.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The nine-slice <i>geometry</i> was never the untested half.</b>
///         <c>Vixen.Ui.Tests/NineSliceImageTests</c> cuts the nine cells, pairs each with its own
///         source cell, skips the middle for a hollow frame and proves the whole thing is one draw.
///         What nothing exercised was the four lines of <see cref="Image" /> that decide whether a
///         nine-slice is asked for at all — and those four lines are gated on two properties
///         (<see cref="Image.SourceBorder" />, <see cref="Image.HollowCentre" />) that nothing in the
///         repository assigned, so the branch was unreachable in practice and green either way.
///     </para>
///     <para>
///         <b>Asserted on the command rather than on a picture.</b> The claim is about which call the
///         control makes, and the command carries the answer exactly: <c>Slice</c> and
///         <c>SourceSlice</c> are empty on a stretched image and hold the two cuts on a sliced one.
///         A screenshot would be a picture of a texture the software rasterizer has no atlas for.
///     </para>
/// </remarks>
public class ImageNineSliceTests {
    /// <summary>A number standing for a texture, which is all the draw list ever knows about one.</summary>
    const ulong Atlas = 7;

    /// <summary>Sixteen pixels of corner on a 128-pixel sheet, which is the pair the doc comment names.</summary>
    static readonly NineSlice Border = NineSlice.Uniform(16f);

    static readonly NineSlice Source = NineSlice.Uniform(16f / 128f);

    [Fact]
    public void An_image_with_neither_cut_draws_one_stretched_quad() {
        var command = Drawn(image => image.Texture = Atlas);

        Assert.True(command.Slice.IsEmpty);
        Assert.True(command.SourceSlice.IsEmpty);
    }

    /// <summary>
    ///     ⚠ Either half alone is the ordinary stretched image, which is the control's documented
    ///     contract and the reason <see cref="Image.SourceBorder" /> being unset made the branch
    ///     unreachable rather than merely unused.
    /// </summary>
    [Fact]
    public void A_destination_cut_with_no_source_cut_is_still_one_stretched_quad() {
        var command = Drawn(image => {
            image.Texture = Atlas;
            image.Border = Border;
        }
        );

        Assert.True(command.Slice.IsEmpty);
    }

    [Fact]
    public void A_source_cut_with_no_destination_cut_is_still_one_stretched_quad() {
        var command = Drawn(image => {
            image.Texture = Atlas;
            image.SourceBorder = Source;
        }
        );

        Assert.True(command.Slice.IsEmpty);
        Assert.True(command.SourceSlice.IsEmpty);
    }

    [Fact]
    public void Both_cuts_together_reach_the_command_in_their_own_spaces() {
        var command = Drawn(image => {
            image.Texture = Atlas;
            image.Border = Border;
            image.SourceBorder = Source;
        }
        );

        // ⚠ Pixels on one side and UVs on the other, and not derivable from each other here: the
        // draw list does not know how big the texture is. Asserting both is what would catch a
        // control that passed the same value twice.
        Assert.Equal(Border, command.Slice);
        Assert.Equal(Source, command.SourceSlice);
        Assert.False(command.HollowCentre);
    }

    [Fact]
    public void A_hollow_centre_reaches_the_command_only_along_the_nine_slice_branch() {
        var sliced = Drawn(image => {
            image.Texture = Atlas;
            image.Border = Border;
            image.SourceBorder = Source;
            image.HollowCentre = true;
        }
        );

        Assert.True(sliced.HollowCentre);

        // The other half of the same claim: the stretched branch has no middle cell to leave out, so
        // asking for one there must not travel. This is what makes `HollowCentre` a property of the
        // nine-slice rather than of the image.
        var stretched = Drawn(image => {
            image.Texture = Atlas;
            image.HollowCentre = true;
        }
        );

        Assert.False(stretched.HollowCentre);
    }

    /// <summary>
    ///     ⚠ An unset texture draws nothing at all, which is what an image whose asset has not
    ///     finished loading should do — so the nine-slice does not resurrect it.
    /// </summary>
    [Fact]
    public void An_image_with_no_texture_emits_no_image_command_however_it_is_cut() {
        using var fixture = new ControlFixture(css: "image { width: 200px; height: 100px; }");

        var image = fixture.Add<Image>();
        image.Border = Border;
        image.SourceBorder = Source;
        fixture.Update();

        Assert.DoesNotContain(fixture.Document.Drawing.Commands, static command => command.Image != 0);
    }

    /// <summary>Draws one <see cref="Image" /> and returns the command it emitted.</summary>
    /// <param name="configure">What the application would have set.</param>
    /// <returns>The single image command in the frame.</returns>
    static DrawCommand Drawn(Action<Image> configure) {
        using var fixture = new ControlFixture(css: "image { width: 200px; height: 100px; }");

        var image = fixture.Add<Image>();
        configure(image);
        fixture.Update();

        // Selected by carrying a texture rather than by index: the theme is installed, so the frame
        // holds the root's own boxes as well, and an image command is the only one with an `Image`.
        return Assert.Single(fixture.Document.Drawing.Commands, static command => command.Image != 0);
    }
}
