// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>What a control drawing itself costs in the draw list's side buffer.</summary>
/// <remarks>
///     <para>
///         <b>The imperative twin of <c>BorderLonghandTests</c>' <c>Assert.Empty(Boxes)</c>.</b> The
///         declarative path has asserted the cheap path for a long time; nothing asserted it for
///         <see cref="DrawContext" />, and the imperative path did not take it — every
///         <c>StrokeRectangle(rect, colour, thickness)</c> in the tree wrote a
///         <see cref="BoxStyle" /> full of zeroes, because the parameter defaults.
///     </para>
///     <para>
///         ⚠ <b>The oracle is the side buffer's length, and it has to be.</b> Nothing draws wrong
///         either way — the geometry builder expands the scalar radius back into four equal corners —
///         so a test on the picture, on the command count, or on the drawn shape passes against the
///         defect. What differs is how many entries the frame diff compares.
///     </para>
/// </remarks>
public class DrawContextBoxTests {
    /// <summary>An element that draws one box, however the test asked for it.</summary>
    sealed class Box : UiElement {
        public Action<DrawContext, Rectangle>? Draws { get; set; }

        protected internal override void OnDraw(DrawContext context) => Draws?.Invoke(context, context.Bounds);
    }

    static (UiDocument Document, Box Shape) Drawn(Action<DrawContext, Rectangle> draws) {
        var document = new UiDocument(100f, 100f);

        document.Load("""
            root { width: 100px; height: 100px; }
            shape { width: 40px; height: 20px; }
        """);

        var shape = document.Root.Add<Box>("shape");
        shape.Draws = draws;

        document.Update();
        document.Draw();

        return (document, shape);
    }

    /// <summary>The one command the fixture's element drew.</summary>
    /// <remarks>
    ///     Matched by kind rather than by index: the root paints its own background first, and a
    ///     test that took <c>Commands[0]</c> would be asserting about that instead.
    /// </remarks>
    static DrawCommand Only(UiDocument document, DrawCommandKind kind) =>
        Assert.Single(document.Drawing.Commands, command => command.Kind == kind);

    [Fact]
    public void A_plain_stroked_rectangle_writes_no_side_buffer_entry() {
        var (document, _) = Drawn(
            static (context, bounds) => context.StrokeRectangle(bounds, Color4.White, 1f)
        );

        using var owner = document;

        // ⚠ The style parameter defaults, so this is what every caller in the tree that says nothing
        // about corners was writing — one entry of zeroes per box per frame, compared entry by entry
        // against the last frame's.
        Assert.Empty(document.Drawing.Boxes);

        var command = Only(document, DrawCommandKind.Border);

        Assert.False(command.HasStyle);
        Assert.Equal(0f, command.Radius);
    }

    [Fact]
    public void One_radius_travels_in_the_scalar_and_not_in_the_side_buffer() {
        var (document, _) = Drawn(
            static (context, bounds) =>
                context.FillRectangle(bounds, Color4.White, BoxStyle.Rounded(CornerRadii.Uniform(6f)))
        );

        using var owner = document;

        // ⚠ **The half that makes this a claim about the radius rather than about an empty style.**
        // A box with corners still needs those corners drawn; what it does not need is a record to
        // say so, because `DrawCommand.Radius` is a float and the geometry builder expands it back
        // into four equal corners on the way to the shader.
        Assert.Empty(document.Drawing.Boxes);

        var command = Only(document, DrawCommandKind.Rectangle);

        Assert.Equal(6f, command.Radius);
    }

    [Fact]
    public void Four_equal_elliptical_corners_still_need_an_entry() {
        var (document, _) = Drawn(
            static (context, bounds) => context.FillRectangle(
                bounds,
                Color4.White,
                BoxStyle.Rounded(
                    new CornerRadii(
                        new Vector2(12f, 6f),
                        new Vector2(12f, 6f),
                        new Vector2(12f, 6f),
                        new Vector2(12f, 6f)
                    )
                )
            )
        );

        using var owner = document;

        // ⚠ Equal to each other and still not one number. A cheap path that tested equality rather
        // than circularity would drop these and draw a pill with circular corners, which is the
        // failure `CornerRadii.IsUniformCircular` is written against.
        Assert.Single(document.Drawing.Boxes);
        Assert.True(Only(document, DrawCommandKind.Rectangle).HasStyle);
    }

    [Fact]
    public void A_gradient_on_square_corners_still_needs_an_entry() {
        var (document, _) = Drawn(
            static (context, bounds) => context.FillRectangle(bounds, Color4.White, BoxStyle.Vertical(Color4.Black))
        );

        using var owner = document;

        // The corners are uniform circular — they are all zero — so a cheap path that only looked at
        // them would drop the second colour and paint the box flat. The style is compared whole for
        // that reason, against `Rounded` of its own corners rather than against a list of members.
        Assert.Single(document.Drawing.Boxes);
        Assert.True(Only(document, DrawCommandKind.Rectangle).HasStyle);
    }
}
