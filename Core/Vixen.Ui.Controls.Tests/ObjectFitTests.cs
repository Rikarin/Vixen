// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>A picture placed in a box that is not its shape, read off the draw command.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The draw command rather than a picture, and this is the one family in the compositing
///         cluster where that is the <i>stronger</i> evidence.</b> A blend or a mask has to be read as
///         pixels because the arithmetic is what is in question; here the arithmetic is two
///         rectangles, and the rectangles are the answer — a screenshot of a stretched texture and a
///         screenshot of a fitted one differ only where the texture has detail, and no texture is
///         registered on this path at all. `SoftwareUiRasterizer` draws nothing for an image whose
///         number it does not know, so a pixel assertion here would be a picture of a blank.
///     </para>
///     <para>
///         ⚠ <b>Every number below is worked out from CSS Images 3 § 5.5 by hand against a 64 × 16
///         picture in a 40 × 40 box</b> — a 4:1 ratio against 1:1, chosen so that <c>contain</c>,
///         <c>cover</c> and <c>none</c> are three different rectangles rather than two. The
///         destination and the source are asserted together, always: <c>contain</c> moves the
///         destination and leaves the source whole, <c>cover</c> does the reverse, and a test that
///         looked at one of them would pass on the other's implementation.
///     </para>
/// </remarks>
public class ObjectFitTests {
    /// <summary>A 64 × 16 picture in a 40 × 40 box, under whatever style is named.</summary>
    static UiTest Placed(string style) {
        var ui = UiTest.Create(60f, 60f);

        ui.Load(
            $$"""
            root  { width: 60px; height: 60px; }
            image { position: absolute; left: 0; top: 0; width: 40px; height: 40px; {{style}} }
            """
        );

        var picture = ui.Document.Create<Image>(null, ui.Document.Root);

        // ⚠ A number and not a registered texture. `Image.OnDraw` returns before it looks at anything
        // when this is zero, so the command under test would not exist — and the number names nothing,
        // because what is being measured is the geometry the command carries.
        picture.Texture = 7;
        picture.IntrinsicSize = new Vector2(64f, 16f);

        ui.Frame();

        return ui;
    }

    /// <summary>The one image command in the frame, as (destination, source).</summary>
    static (Rectangle Destination, Rectangle Source) Drawn(UiTest ui) {
        var command = ui.Document.Drawing.Commands.Single(c => c.Kind == DrawCommandKind.Image);

        return (
            new Rectangle(command.X, command.Y, command.Width, command.Height),
            command.Source
        );
    }

    static void Same(Rectangle expected, Rectangle actual, string what) {
        Assert.True(
            MathF.Abs(expected.X - actual.X) < 1e-3f
            && MathF.Abs(expected.Y - actual.Y) < 1e-3f
            && MathF.Abs(expected.Width - actual.Width) < 1e-3f
            && MathF.Abs(expected.Height - actual.Height) < 1e-3f,
            $"{what}: expected {expected}, got {actual}"
        );
    }

    /// <summary>Nothing said stretches the picture over the box, which is what it always did.</summary>
    /// <remarks>
    ///     ⚠ <b>The instrument's own check, and the compatibility claim in one.</b> Every other test
    ///     here is only legible against what "no declaration" produces — and this is also the
    ///     assertion that an application which never heard of <c>object-fit</c> sees the identical
    ///     frame to the one it saw before the property existed.
    /// </remarks>
    [Fact]
    public void An_unfitted_picture_is_stretched_over_the_whole_box() {
        using var ui = Placed(string.Empty);

        var (destination, source) = Drawn(ui);

        Same(new Rectangle(0f, 0f, 40f, 40f), destination, "destination");
        Same(new Rectangle(0f, 0f, 1f, 1f), source, "source");
    }

    /// <summary>An intrinsic size nobody supplied is <c>fill</c>, whatever the stylesheet asks for.</summary>
    /// <remarks>
    ///     ⚠ <b>Not a fallback for a missing feature — it is CSS.</b> Images 3 § 5.5 defines
    ///     <c>contain</c> as a relation between the intrinsic ratio and the box, so with no ratio
    ///     there is nothing to relate and the specification's own answer for content with no intrinsic
    ///     dimensions is to fill. An engine that letterboxed by guessing a ratio would be inventing
    ///     one.
    /// </remarks>
    [Fact]
    public void A_picture_of_unknown_size_fills_whatever_the_fit_says() {
        using var ui = UiTest.Create(60f, 60f);

        ui.Load(
            """
            root  { width: 60px; height: 60px; }
            image { position: absolute; left: 0; top: 0; width: 40px; height: 40px; object-fit: contain; }
            """
        );

        var picture = ui.Document.Create<Image>(null, ui.Document.Root);
        picture.Texture = 7;

        ui.Frame();

        var (destination, source) = Drawn(ui);

        Same(new Rectangle(0f, 0f, 40f, 40f), destination, "destination");
        Same(new Rectangle(0f, 0f, 1f, 1f), source, "source");
    }

    /// <summary><c>contain</c> shrinks the destination and keeps the whole picture.</summary>
    /// <remarks>
    ///     The scale is <c>min(40/64, 40/16) = 0.625</c>, so the picture is 40 × 10 and the 30 pixels
    ///     left over are split by the initial <c>object-position: 50% 50%</c>.
    /// </remarks>
    [Fact]
    public void Contain_letterboxes_inside_the_box() {
        using var ui = Placed("object-fit: contain;");

        var (destination, source) = Drawn(ui);

        Same(new Rectangle(0f, 15f, 40f, 10f), destination, "destination");
        Same(new Rectangle(0f, 0f, 1f, 1f), source, "source");
    }

    /// <summary><c>cover</c> keeps the destination and crops the source.</summary>
    /// <remarks>
    ///     ⚠ <b>The mirror image of <c>contain</c>, and the pair is what proves the implementation is
    ///     not simply scaling a quad.</b> The scale is <c>max(40/64, 40/16) = 2.5</c>, so the picture
    ///     is 160 × 40 and 120 pixels of it are outside the box — 60 on each side under the initial
    ///     centring, which is <c>60/160 = 0.375</c> of the picture cropped from the left and the same
    ///     from the right.
    /// </remarks>
    [Fact]
    public void Cover_fills_the_box_and_crops_the_source() {
        using var ui = Placed("object-fit: cover;");

        var (destination, source) = Drawn(ui);

        Same(new Rectangle(0f, 0f, 40f, 40f), destination, "destination");
        Same(new Rectangle(0.375f, 0f, 0.25f, 1f), source, "source");
    }

    /// <summary><c>none</c> overflows on one axis and underfills on the other, at once.</summary>
    /// <remarks>
    ///     ⚠ <b>The case a two-branch implementation gets wrong.</b> Shrinking the destination is the
    ///     obvious shape for <c>contain</c> and narrowing the source is the obvious shape for
    ///     <c>cover</c>; <c>none</c> needs both in one answer, because a 64 × 16 picture in a 40 × 40
    ///     box is 24 pixels too wide and 24 pixels too short. So the destination is 40 × 16 —
    ///     narrowed by the box, centred in it — and the source is cropped horizontally by
    ///     <c>12/64</c> at each side and not at all vertically.
    /// </remarks>
    [Fact]
    public void None_crops_one_axis_and_letterboxes_the_other() {
        using var ui = Placed("object-fit: none;");

        var (destination, source) = Drawn(ui);

        Same(new Rectangle(0f, 12f, 40f, 16f), destination, "destination");
        Same(new Rectangle(0.1875f, 0f, 0.625f, 1f), source, "source");
    }

    /// <summary><c>scale-down</c> is <c>contain</c> where the picture is too big for the box.</summary>
    /// <remarks>
    ///     ⚠ Asserted against <c>none</c> as well as against <c>contain</c>, because the keyword is
    ///     defined as the smaller of the two and a test that only checked the <c>contain</c> half
    ///     would pass on an implementation that had aliased it to <c>contain</c> outright. The second
    ///     half is the box larger than the picture, where <c>contain</c> would scale <i>up</i> and
    ///     <c>scale-down</c> must not.
    /// </remarks>
    [Fact]
    public void Scale_down_never_enlarges() {
        using (var small = Placed("object-fit: scale-down;")) {
            Same(new Rectangle(0f, 15f, 40f, 10f), Drawn(small).Destination, "shrunk");
        }

        using var big = UiTest.Create(200f, 200f);

        big.Load(
            """
            root  { width: 200px; height: 200px; }
            image { position: absolute; left: 0; top: 0; width: 160px; height: 160px;
                    object-fit: scale-down; }
            """
        );

        var picture = big.Document.Create<Image>(null, big.Document.Root);
        picture.Texture = 7;
        picture.IntrinsicSize = new Vector2(64f, 16f);

        big.Frame();

        // `contain` here would be a scale of 2.5 and a 160 × 40 destination. The picture stays 64 × 16
        // and sits in the middle of the 160 × 160 box.
        Same(new Rectangle(48f, 72f, 64f, 16f), Drawn(big).Destination, "unscaled");
    }

    /// <summary><c>object-position</c> spends the slack, and a corner keyword is two words.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The two-word values are the half of this root that needed a new reader, not a new
    ///         table.</b> <c>object-left-top</c> computes to <c>left top</c>, and
    ///         <c>UiDocument.KeywordOf</c> answers <c>null</c> to anything that is not one bare
    ///         identifier — so four of Tailwind's nine position classes were unreadable by every
    ///         accessor <c>StyleAccess</c> had. <c>left top</c> and <c>top</c> are asserted together
    ///         here for that reason: the one-word case passes against the old accessor and the
    ///         two-word case does not.
    ///     </para>
    ///     <para>
    ///         Under <c>contain</c> the picture is 40 × 10, so all 30 pixels of slack are vertical and
    ///         the horizontal component of every one of these is unobservable — which is exactly why
    ///         the fixture is 4:1 and the assertions are about <c>y</c>.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("left top", 0f)]
    [InlineData("top", 0f)]
    [InlineData("bottom", 30f)]
    [InlineData("right bottom", 30f)]
    [InlineData("center", 15f)]
    [InlineData("50%", 15f)]
    [InlineData("0% 100%", 30f)]
    public void A_position_places_the_picture_in_the_slack(string position, float top) {
        using var ui = Placed($"object-fit: contain; object-position: {position};");

        Same(new Rectangle(0f, top, 40f, 10f), Drawn(ui).Destination, position);
    }

    /// <summary>A cut of a sheet fits by the cut's ratio, not by the sheet's.</summary>
    /// <remarks>
    ///     ⚠ <b>The trap this is here for is fitting by <c>IntrinsicSize</c> alone.</b> The property
    ///     is the whole texture's size, and <c>SourceRectangle</c> is a fraction of it — so a sprite
    ///     sheet cell taking the left quarter of a 64 × 16 sheet is a 16 × 16 <i>square</i>, and
    ///     fitting it by the sheet's 4:1 would letterbox a square picture that fits the box exactly.
    ///     ⚠ And the source stays the cell rather than becoming the sheet, which is the second half of
    ///     the same mistake.
    /// </remarks>
    [Fact]
    public void A_sprite_sheet_cell_fits_by_its_own_shape() {
        using var ui = UiTest.Create(60f, 60f);

        ui.Load(
            """
            root  { width: 60px; height: 60px; }
            image { position: absolute; left: 0; top: 0; width: 40px; height: 40px; object-fit: contain; }
            """
        );

        var picture = ui.Document.Create<Image>(null, ui.Document.Root);
        picture.Texture = 7;
        picture.IntrinsicSize = new Vector2(64f, 16f);
        picture.SourceRectangle = new Rectangle(0f, 0f, 0.25f, 1f);

        ui.Frame();

        var (destination, source) = Drawn(ui);

        Same(new Rectangle(0f, 0f, 40f, 40f), destination, "destination");
        Same(new Rectangle(0f, 0f, 0.25f, 1f), source, "source");
    }

    /// <summary>A mirrored cut still fits, and stays mirrored.</summary>
    /// <remarks>
    ///     ⚠ <b>A negative extent is how this engine spells a flipped sample — <c>Viewport</c> flips
    ///     vertically with one — so the fit arithmetic has to be affine rather than clamped.</b> The
    ///     tempting defensive line is to normalise the source rectangle before doing anything with it,
    ///     which silently un-flips every flipped image the moment somebody adds an <c>object-fit</c>
    ///     to its stylesheet. Here <c>cover</c> crops 0.375 from each side of a source that runs
    ///     right-to-left, so the answer starts at <c>1 − 0.375</c> and keeps its negative width.
    /// </remarks>
    [Fact]
    public void A_flipped_source_keeps_its_sign() {
        using var ui = UiTest.Create(60f, 60f);

        ui.Load(
            """
            root  { width: 60px; height: 60px; }
            image { position: absolute; left: 0; top: 0; width: 40px; height: 40px; object-fit: cover; }
            """
        );

        var picture = ui.Document.Create<Image>(null, ui.Document.Root);
        picture.Texture = 7;
        picture.IntrinsicSize = new Vector2(64f, 16f);
        picture.SourceRectangle = new Rectangle(1f, 0f, -1f, 1f);

        ui.Frame();

        var (destination, source) = Drawn(ui);

        Same(new Rectangle(0f, 0f, 40f, 40f), destination, "destination");
        Same(new Rectangle(0.625f, 0f, -0.25f, 1f), source, "source");
    }

    /// <summary>A picture pushed entirely outside its box draws nothing at all.</summary>
    /// <remarks>
    ///     ⚠ Nothing rather than a degenerate quad. A zero- or negative-extent rectangle would reach
    ///     the geometry builder as two triangles wound the wrong way, which is a different kind of
    ///     wrong from an empty frame and much harder to read from a picture.
    /// </remarks>
    [Fact]
    public void A_picture_placed_off_the_box_is_not_drawn() {
        using var ui = UiTest.Create(60f, 60f);

        ui.Load(
            """
            root  { width: 60px; height: 60px; }
            image { position: absolute; left: 0; top: 0; width: 40px; height: 8px;
                    object-fit: none; object-position: left 200%; }
            """
        );

        var picture = ui.Document.Create<Image>(null, ui.Document.Root);
        picture.Texture = 7;
        picture.IntrinsicSize = new Vector2(64f, 16f);

        ui.Frame();

        Assert.DoesNotContain(ui.Document.Drawing.Commands, c => c.Kind == DrawCommandKind.Image);
    }
}
