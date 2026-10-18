// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Rendering;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>
///     The independent <c>rotate</c> and <c>translate</c> properties out of the plane: an x or y axis,
///     a vector, and a z component (#1473).
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Both used to be refused on the ground that "this engine has no depth"</b> —
///         <c>rotate: x 45deg</c> drew the element unrotated and <c>translate: 0 0 40px</c> dropped its
///         z — while <c>transform</c> beside them composed <c>rotateX</c>, <c>rotate3d</c> and
///         <c>translateZ</c> in four dimensions and projected them through the parent's
///         <c>perspective</c> (#550). The claim was false, so the refusal was a choice nobody made.
///     </para>
///     <para>
///         <b>The oracle is the specification's own equivalence.</b> Transforms 2 § 3 defines
///         <c>rotate: x 60deg</c> as <c>rotateX(60deg)</c>, <c>rotate: 1 1 0 45deg</c> as
///         <c>rotate3d(1, 1, 0, 45deg)</c> and <c>translate: 0 0 50px</c> as
///         <c>translateZ(50px)</c>, so each case lays the property and the function out side by side
///         under one perspective and asserts the two matrices are one. A projection is asserted, not
///         assumed: the instrument first requires the function's matrix to be non-affine.
///     </para>
/// </remarks>
public class IndependentTransformDepthTests {
    const float Tolerance = 0.0001f;

    /// <summary>Two equal cards under one 200-point perspective, one per declaration.</summary>
    static (UiDocument Document, UiElement Property, UiElement Function) Pair(string property, string function) {
        var document = new UiDocument(400f, 300f);
        document.Load(
            $$"""
              root { width: 400px; height: 300px; }
              .stage { position: absolute; left: 0px; top: 0px; width: 400px; height: 300px;
                       perspective: 200px; }
              .property { position: absolute; left: 150px; top: 100px; width: 100px; height: 100px;
                          background-color: #111; {{property}}; }
              .function { position: absolute; left: 150px; top: 100px; width: 100px; height: 100px;
                          background-color: #111; transform: {{function}}; }
              """
        );

        var stage = document.Root.Add("div", classNames: "stage");
        var first = stage.Add("div", classNames: "property");
        var second = stage.Add("div", classNames: "function");
        document.Update();
        document.Draw();

        return (document, first, second);
    }

    static void Same(UiTransform expected, UiTransform actual) {
        Assert.Equal(expected.M11, actual.M11, Tolerance);
        Assert.Equal(expected.M12, actual.M12, Tolerance);
        Assert.Equal(expected.M21, actual.M21, Tolerance);
        Assert.Equal(expected.M22, actual.M22, Tolerance);
        Assert.Equal(expected.Dx, actual.Dx, 0.01f);
        Assert.Equal(expected.Dy, actual.Dy, 0.01f);
        Assert.Equal(expected.M13, actual.M13, Tolerance);
        Assert.Equal(expected.M23, actual.M23, Tolerance);
        Assert.Equal(expected.M33, actual.M33, Tolerance);
    }

    /// <summary>⚠ <b>The out-of-plane rotations, named, as a vector, and with the angle first.</b></summary>
    [Theory]
    [InlineData("rotate: x 60deg", "rotateX(60deg)")]
    [InlineData("rotate: y 60deg", "rotateY(60deg)")]
    [InlineData("rotate: 1 1 0 45deg", "rotate3d(1, 1, 0, 45deg)")]
    [InlineData("rotate: 45deg 0 2 0", "rotateY(45deg)")]
    [InlineData("rotate: -1 0 0 30deg", "rotateX(-30deg)")]
    public void An_out_of_plane_rotate_is_the_function_it_is_defined_as(string property, string function) {
        var (document, card, twin) = Pair(property, function);
        using var _ = document;

        var expected = Assert.IsType<UiTransform>(twin.Transform);
        Assert.False(expected.IsAffine, "the instrument: the function's matrix is projected");

        Same(expected, Assert.IsType<UiTransform>(card.Transform));
    }

    /// <summary>⚠ <b>A <c>translate</c> with a z component is <c>translateZ</c> under the parent's perspective.</b></summary>
    [Fact]
    public void A_translate_along_z_is_translateZ() {
        var (document, card, twin) = Pair("translate: 0px 0px 50px", "translateZ(50px)");
        using var _ = document;

        var expected = Assert.IsType<UiTransform>(twin.Transform);
        Assert.False(expected.IsAffine, "the instrument: the function's matrix is projected");

        Same(expected, Assert.IsType<UiTransform>(card.Transform));
    }

    /// <summary>
    ///     The order Transforms 2 § 3 fixes, with a z translation outermost and a turn inside it: the
    ///     same picture as <c>translateZ(50px) rotateX(30deg)</c>, which is a different one from the
    ///     two the other way round whenever the parent projects.
    /// </summary>
    [Fact]
    public void A_z_translation_and_an_axis_rotation_compose_in_the_specifications_order() {
        var (document, card, twin) = Pair("translate: 0px 0px 50px; rotate: x 30deg", "translateZ(50px) rotateX(30deg)");
        using var _ = document;

        Same(Assert.IsType<UiTransform>(twin.Transform), Assert.IsType<UiTransform>(card.Transform));

        // The instrument: the other order really is a different picture, so the equality above is
        // about the order and not about two products that happen to agree.
        var (other, again, reversed) = Pair("translate: 0px 0px 50px; rotate: x 30deg", "rotateX(30deg) translateZ(50px)");
        using var __ = other;
        Assert.NotEqual(Assert.IsType<UiTransform>(again.Transform).Dy, Assert.IsType<UiTransform>(reversed.Transform).Dy, 0.01f);
    }

    /// <summary>
    ///     A <c>rotate: y 180deg</c> card has turned its back exactly as <c>rotateY(180deg)</c> has, so
    ///     <c>backface-visibility: hidden</c> takes it out of the picture and out of the hit test.
    /// </summary>
    [Fact]
    public void A_y_axis_half_turn_shows_its_back() {
        var (document, card, twin) = Pair("rotate: y 180deg; backface-visibility: hidden", "rotateY(180deg); backface-visibility: hidden");
        using var _ = document;

        Assert.True(twin.BackfaceHidden, "the instrument: the function's card has turned away");
        Assert.True(card.BackfaceHidden);
        Assert.Equal(0, document.Drawing.Commands.Count(command => command.Kind == DrawCommandKind.Rectangle));
    }

    /// <summary>
    ///     ⚠ <b>The plane rotation still takes the flat path</b>: <c>rotate: z 90deg</c> and
    ///     <c>rotate: 90deg</c> are affine, so nothing the screenshots were rendered through moves.
    /// </summary>
    [Theory]
    [InlineData("rotate: z 90deg")]
    [InlineData("rotate: 0 0 1 90deg")]
    [InlineData("rotate: 90deg")]
    public void A_plane_rotation_stays_affine(string property) {
        var (document, card, _) = Pair(property, "none");
        using var _ = document;

        Assert.True(Assert.IsType<UiTransform>(card.Transform).IsAffine);
    }
}
