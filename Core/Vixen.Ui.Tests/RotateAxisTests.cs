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

    /// <summary>Any other axis is refused and leaves the element alone, and so does an axis of no length.</summary>
    [Theory]
    [InlineData("x 30deg")]
    [InlineData("y 30deg")]
    [InlineData("1 0 0 30deg")]
    [InlineData("0 0.001 1 30deg")]
    [InlineData("0 0 0 30deg")]
    public void A_rotation_about_any_other_axis_leaves_the_element_alone(string rotate) {
        using var document = new UiDocument(400f, 300f);

        Turned(Box(document, rotate), 0f);
    }
}
