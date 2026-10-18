// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>A <c>mask-image</c> changes what is painted and not what the pointer reaches (#1408).</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Parity, not a gap.</b> CSS Masking 1 §7.10: "The <c>mask-image</c> property has no effect
///         on the geometry or hit-testing of any element's CSS boxes" — and §8.8 says the same of
///         <c>mask-border-source</c>. It is <c>clip-path</c> that cuts hit testing (§5: pointer events
///         "must not be dispatched on the clipped-out (non-visible) regions"). So
///         <c>UiDocument.HitTest</c> reading no mask is the specification, for a box of any size,
///         and these tests pin it so that a well-meant "mask the hit test" is seen to be a divergence.
///     </para>
///     <para>
///         ⚠ <b>The zero box is the same sentence.</b> A zero-tall masked box paints nothing of its
///         subtree (<c>ZeroSizedBoxPaintTests</c>), because its mask is clipped to a border box with no
///         area — but the mask has no effect on the descendants' boxes either, so the child that
///         overflows it is still where a click lands. An author who wants an invisible subtree to stop
///         taking the pointer says <c>pointer-events: none</c> or <c>visibility: hidden</c>.
///     </para>
/// </remarks>
public class MaskHitTestTests {
    /// <summary>A mask that is transparent everywhere, so the box and its child paint nothing.</summary>
    const string Invisible = "mask-image: linear-gradient(to right, rgba(0, 0, 0, 0), rgba(0, 0, 0, 0));";

    static UiDocument Laid(string boxCss) {
        var document = new UiDocument(400f, 300f);
        document.Load(
            "root { width: 400px; height: 300px; flex-direction: row; align-items: flex-start; }\n"
            + ".child { display: block; width: 120px; height: 30px; background-color: #ff0000; }\n"
            + ".box { display: block; " + boxCss + " }"
        );

        document.Root.Add("div", classNames: "box").Add("div", classNames: "child");
        document.Update();
        document.Draw();

        return document;
    }

    static UiElement Box(UiDocument document) => document.Root.Children[0];

    static UiElement Child(UiDocument document) => Box(document).Children[0];

    /// <summary>A box its mask makes fully transparent still takes the click.</summary>
    [Fact]
    public void A_region_a_mask_makes_transparent_still_takes_the_pointer() {
        using var document = Laid("width: 200px; height: 100px; " + Invisible);

        Assert.Same(Box(document), document.HitTest(150f, 50f));
        Assert.Same(Child(document), document.HitTest(50f, 15f));
    }

    /// <summary>
    ///     The child of a zero-tall masked box is unpainted and still hit, as in CSS. The control without
    ///     the mask says the point is the child's either way, so the mask is what the test is about.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_unpainted_child_of_a_zero_tall_masked_box_still_takes_the_pointer(bool masked) {
        using var document = Laid("width: 200px; height: 0px; " + (masked ? Invisible : "mask-image: none;"));

        Assert.Equal(0f, Box(document).Height);

        // The instrument: the mask is real — the child is painted only without it.
        var red = new Color4(1f, 0f, 0f, 1f);
        Assert.Equal(masked ? 0 : 1, document.Drawing.Commands.Count(command => command.Kind == DrawCommandKind.Rectangle && command.Color == red));

        Assert.Same(Child(document), document.HitTest(50f, 15f));
    }
}
