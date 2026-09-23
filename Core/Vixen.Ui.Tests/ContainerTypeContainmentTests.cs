// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>What <c>container-type</c> does to the box that declares it, apart from being asked.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A query container is a contained box, and for as long as it was not, a container sized
///         by its contents could oscillate.</b> CSS Containment 3 § 3.1: <c>inline-size</c> "applies
///         style containment and inline-size containment to the principal box, and establishes an
///         independent formatting context"; <c>size</c> the same with size containment. The store
///         already had both containments for <c>contain</c> — <c>LayoutTree.CalculateLayoutImpl</c>
///         pins the contained axes before any algorithm runs, which is the one choke point doc 43
///         § D3's "coercion" asked for — and <c>container-type</c> never reached it, so the query and
///         the box it queries could each keep moving the other.
///     </para>
///     <para>
///         ⚠ <b>Not layout containment, which is what the specification used to say.</b> The
///         independent formatting context is kept and the containing block for out-of-flow
///         descendants is not; <see cref="A_query_container_is_not_the_containing_block_of_an_absolute_descendant" />
///         is the row that tells the two apart.
///     </para>
///     <para>
///         Each fixture is one where the contained and uncontained boxes differ — a flex item whose
///         width is its content's, a block whose child's margin would collapse through it — because a
///         contained box and an uncontained one are the same box wherever the size is already
///         decided from outside, which is most of them.
///     </para>
/// </remarks>
public class ContainerTypeContainmentTests {
    const float Tolerance = 0.001f;

    static UiDocument Laid(string css, Action<UiDocument> build) {
        var document = new UiDocument(400f, 300f);
        document.Load(css);
        build(document);
        document.Update();

        return document;
    }

    /// <summary>A flex item with no width, holding a 60 × 40 child: its size is its content's unless contained.</summary>
    static UiDocument ContentSized(string declaration) =>
        Laid(
            $$"""
            root { width: 400px; height: 300px; flex-direction: row; align-items: flex-start; }
            .box { display: block; {{declaration}} }
            .child { display: block; width: 60px; height: 40px; }
            """,
            document => document.Root.Add("div", classNames: "box").Add("div", classNames: "child")
        );

    [Theory]
    // The control, and the two spellings that name a box without making it a query container.
    [InlineData("", 60f, 40f)]
    [InlineData("container-type: normal", 60f, 40f)]
    [InlineData("container: card", 60f, 40f)]
    // Inline-size containment: no width from the content, and a height that still is.
    [InlineData("container-type: inline-size", 0f, 40f)]
    [InlineData("container: card / inline-size", 0f, 40f)]
    // Size containment: neither axis from the content.
    [InlineData("container-type: size", 0f, 0f)]
    [InlineData("container: card / size", 0f, 0f)]
    // ⚠ The longhand wins over the shorthand, which is `UiDocument.KindOf`'s order — the query and the
    // containment must agree about whether this is a container, or the box is contained for a query
    // that can never ask it.
    [InlineData("container: card / inline-size; container-type: normal", 60f, 40f)]
    public void A_query_container_takes_no_size_from_its_contents_on_the_axes_it_answers(
        string declaration,
        float width,
        float height
    ) {
        using var document = ContentSized(declaration);
        var box = document.Root.Children[0];
        var child = box.Children[0];

        Assert.Equal(width, box.Width, Tolerance);
        Assert.Equal(height, box.Height, Tolerance);

        // ⚠ And the contents are still laid out at their own size, which is the half that catches
        // "skip the children" — containment decides the box and nothing else.
        Assert.Equal(60f, child.Width, Tolerance);
        Assert.Equal(40f, child.Height, Tolerance);
    }

    /// <summary>A child's top margin stays inside a query container rather than collapsing out through it.</summary>
    /// <remarks>
    ///     ⚠ <b>The independent formatting context, which <c>contain: inline-size</c> alone does not
    ///     give</b> — so the twin row is the containment without the container, and it collapses. A box
    ///     with no border or padding lets its first child's margin through (CSS 2.1 § 8.3.1): the box
    ///     moves down 20 and is 10 tall. As a formatting context root it stays at 0 and is 30 tall.
    /// </remarks>
    [Theory]
    [InlineData("", 20f, 10f)]
    [InlineData("contain: inline-size", 20f, 10f)]
    [InlineData("container-type: inline-size", 0f, 30f)]
    [InlineData("container-type: size; height: 30px", 0f, 30f)]
    public void A_query_container_is_an_independent_formatting_context(string declaration, float top, float height) {
        using var document = Laid(
            $$"""
            root { display: block; width: 400px; height: 300px; }
            .box { display: block; {{declaration}} }
            .child { display: block; margin-top: 20px; height: 10px; }
            """,
            document => document.Root.Add("div", classNames: "box").Add("div", classNames: "child")
        );

        var box = document.Root.Children[0];

        Assert.Equal(top, box.AbsoluteTop, Tolerance);
        Assert.Equal(height, box.Height, Tolerance);
        Assert.Equal(20f, box.Children[0].AbsoluteTop, Tolerance);
    }

    /// <summary>But not the containing block of an absolutely positioned descendant.</summary>
    /// <remarks>
    ///     ⚠ <b>The row that tells the formatting context from layout containment.</b> CSS Containment
    ///     3 once applied layout containment here and was changed to apply only the formatting
    ///     context; the observable difference is exactly this, so reusing <c>Containment.Layout</c> for
    ///     the formatting context would pass every other test in this file. <c>right: 0</c> lands at
    ///     360 against the 400-wide root and at 60 against the 100-wide box.
    /// </remarks>
    [Theory]
    [InlineData("container-type: inline-size", 360f)]
    [InlineData("container-type: size", 360f)]
    [InlineData("contain: layout", 60f)]
    public void A_query_container_is_not_the_containing_block_of_an_absolute_descendant(string declaration, float left) {
        using var document = Laid(
            $$"""
            root { display: block; width: 400px; height: 300px; }
            .box { display: block; position: static; width: 100px; height: 100px; {{declaration}} }
            .pinned { display: block; position: absolute; right: 0; width: 40px; height: 10px; }
            """,
            document => document.Root.Add("div", classNames: "box").Add("div", classNames: "pinned")
        );

        Assert.Equal(left, document.Root.Children[0].Children[0].AbsoluteLeft, Tolerance);
    }
}
