// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary><c>rotate</c> with its axis written, read off the element's transform against a closed form.</summary>
/// <remarks>
///     <para>
///         Transforms 2 § 3: <c>rotate: [ x | y | z | &lt;number&gt;{3} ] &amp;&amp; &lt;angle&gt;</c>. The z
///         axis is the plane this engine draws, so <c>z θ</c> and any vector parallel to (0, 0, 1) is
///         <c>θ</c>; a vector parallel to (0, 0, −1) turns the other way, so it is <c>−θ</c>. Before
///         #1450 every form with an axis drew unrotated.
///     </para>
///     <para>
///         ⚠ <b>The direction is what the pixel tests cannot see</b> — a bar is its own half turn, so
///         <c>90deg</c> and <c>−90deg</c> ink the same pixels. A point's image says which way it went.
///     </para>
/// </remarks>
public class RotateAxisTests {
    const float Tolerance = 0.01f;

    static UiElement Box(UiDocument document, string rotate) {
        document.Load(
            $$"""
            root { width: 400px; height: 300px; }
            #box { width: 100px; height: 40px; transform-origin: 0px 0px; rotate: {{rotate}}; }
            """
        );

        var box = document.Create("div", document.Root, "box");

        document.Tick(TimeSpan.Zero);
        document.Update();

        return box;
    }

    /// <summary>Where the box's top-right corner ends up, which says the angle it turned by.</summary>
    static void Turned(UiElement box, float degrees) {
        var radians = degrees * MathF.PI / 180f;
        var tip = box.Transform is { } transform ? transform.Apply(new Vector2(100f, 0f)) : new Vector2(100f, 0f);

        Assert.Equal(100f * MathF.Cos(radians), tip.X, Tolerance);
        Assert.Equal(100f * MathF.Sin(radians), tip.Y, Tolerance);
    }

    [Theory]
    [InlineData("30deg", 30f)]
    [InlineData("z 30deg", 30f)]
    [InlineData("30deg z", 30f)]
    [InlineData("0 0 1 30deg", 30f)]
    [InlineData("30deg 0 0 1", 30f)]
    [InlineData("0 0 -1 30deg", -30f)]
    [InlineData("0 0 5 30deg", 30f)]
    [InlineData("0 0 -0.25 30deg", -30f)]
    public void A_rotation_about_z_turns_in_the_plane(string rotate, float degrees) {
        using var document = new UiDocument(400f, 300f);

        Turned(Box(document, rotate), degrees);
    }

    /// <summary>An axis of no length is refused and leaves the element alone.</summary>
    [Fact]
    public void A_rotation_about_no_axis_leaves_the_element_alone() {
        using var document = new UiDocument(400f, 300f);

        Turned(Box(document, "0 0 0 30deg"), 0f);
    }

    /// <summary>
    ///     ⚠ <b>Any other axis is the rotation it names now, not a refusal</b> (#1473; #1450 pinned the
    ///     refusal here). With no perspective a turn about y foreshortens x by <c>cos θ</c> and a turn
    ///     about x leaves a point on the x axis where it was, so the corner at (100, 0) lands at
    ///     (86.6, 0) and (100, 0). ⚠ <c>0 0.001 1</c> is a hair off z and is read as the 3D rotation it
    ///     is, which is nearly the plane one — not refused, and not snapped to z.
    /// </summary>
    [Theory]
    [InlineData("y 30deg", 86.6025f, 0f)]
    [InlineData("0 1 0 30deg", 86.6025f, 0f)]
    [InlineData("30deg 0 -3 0", 86.6025f, 0f)]
    [InlineData("x 30deg", 100f, 0f)]
    [InlineData("1 0 0 30deg", 100f, 0f)]
    [InlineData("0 0.001 1 30deg", 86.6025f, 50f)]
    public void A_rotation_about_any_other_axis_is_the_rotation_it_names(string rotate, float x, float y) {
        using var document = new UiDocument(400f, 300f);

        var box = Box(document, rotate);
        var tip = Assert.IsType<Vixen.Ui.Rendering.UiTransform>(box.Transform).Apply(new Vector2(100f, 0f));

        Assert.Equal(x, tip.X, Tolerance);
        Assert.Equal(y, tip.Y, 0.1f);
    }
}
