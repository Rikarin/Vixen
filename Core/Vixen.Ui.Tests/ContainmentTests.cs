// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Tests;

/// <summary><c>contain</c>, end to end: the declaration a stylesheet writes, and what it moves.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Each fixture is built so that the two halves of it differ, which for this property is
///         most of the work.</b> A size-contained box and an uncontained one are the same box wherever
///         the contents fit, a paint-contained one is the same picture unless something overflows, and
///         layout containment is invisible unless there is an out-of-flow descendant AND no
///         <c>position: relative</c> anywhere above it. All three arrangements are here on purpose.
///     </para>
///     <para>
///         The store's own half is <c>Vixen.Ui.Layout.Tests.ContainmentTests</c>; this file is about
///         the declaration reaching it, and about the paint half, which never enters the store at all.
///     </para>
/// </remarks>
public class ContainmentTests {
    const float Tolerance = 0.001f;

    static UiDocument Drawn(string css, Action<UiDocument> build) {
        var document = new UiDocument(400f, 300f);
        document.Load(css);
        build(document);
        document.Update();
        document.Draw();

        return document;
    }

    static List<DrawCommand> Pushes(UiDocument document) =>
        document.Drawing.Commands.Where(command => command.Kind == DrawCommandKind.ClipPush).ToList();

    /// <summary>One auto-sized box with a child two sizes too big for it.</summary>
    const string Fixture = """
        root { display: block; width: 400px; height: 300px; }
        .box { display: block; position: static; }
        .child { display: block; width: 60px; height: 40px; }
        """;

    static UiDocument WithBox(string declaration) =>
        Drawn(
            $$"""
            {{Fixture}}
            .box { {{declaration}} }
            """,
            document => document.Root.Add("div", classNames: "box").Add("div", classNames: "child")
        );

    /// <summary>Nothing declared: the box is its child, which is what the rest measures against.</summary>
    [Fact]
    public void An_auto_sized_box_is_as_tall_as_its_child() {
        using var document = WithBox(string.Empty);
        var box = document.Root.Children[0];

        Assert.Equal(40f, box.Height, Tolerance);
        Assert.Empty(Pushes(document));
    }

    /// <summary><c>contain: size</c> collapses the box and leaves the child exactly where it was.</summary>
    /// <remarks>
    ///     ⚠ <b>Both assertions, and the second is the one that catches "skip the children".</b> CSS
    ///     Containment § 3.2 stops the contents deciding the box; it does not stop them existing, and
    ///     an implementation that returned early would satisfy the first assertion perfectly.
    /// </remarks>
    [Fact]
    public void Size_containment_collapses_the_box_and_keeps_the_child() {
        using var document = WithBox("contain: size");
        var box = document.Root.Children[0];
        var child = box.Children[0];

        Assert.Equal(0f, box.Height, Tolerance);
        Assert.Equal(40f, child.Height, Tolerance);
        Assert.Equal(60f, child.Width, Tolerance);
        Assert.Equal(box.AbsoluteTop, child.AbsoluteTop, Tolerance);
    }

    /// <summary><c>contain: paint</c> clips, with no <c>overflow</c> declaration anywhere.</summary>
    /// <remarks>
    ///     ⚠ <b>The rectangle is asserted and not merely the presence of a push.</b> A clip pushed at
    ///     the wrong rectangle — the root's, say — is a draw list that passes "something is clipped"
    ///     and cuts nothing, which is the failure this property would produce if it were folded in at
    ///     the wrong level. The box is the collapsed one from the fixture above, so the child really
    ///     does hang outside it.
    /// </remarks>
    [Fact]
    public void Paint_containment_clips_the_box_without_an_overflow_declaration() {
        using var document = WithBox("contain: paint; width: 30px; height: 20px");
        var box = document.Root.Children[0];

        var push = Assert.Single(Pushes(document));
        Assert.Equal(box.AbsoluteLeft, push.X, Tolerance);
        Assert.Equal(box.AbsoluteTop, push.Y, Tolerance);
        Assert.Equal(30f, push.Width, Tolerance);
        Assert.Equal(20f, push.Height, Tolerance);

        // ⚠ The same rectangle, asked the other way. A clip the picture obeys and the hit test does
        // not is the one defect `OverflowReader` exists to prevent, so paint containment is asserted
        // through both of its callers rather than only through the one it was added for. The point is
        // inside the CHILD and outside the box, so without the clip the child is what answers.
        using var uncontained = WithBox("width: 30px; height: 20px");

        Assert.Same(uncontained.Root.Children[0].Children[0], uncontained.HitTest(50f, 10f));
        Assert.NotSame(box.Children[0], document.HitTest(50f, 10f));
    }

    /// <summary><c>contain: layout</c> is a containing block, where nothing else is one.</summary>
    /// <remarks>
    ///     ⚠ <b>Neither box is <c>position: relative</c>, which is the only arrangement in which this
    ///     can be seen at all.</b> The root is 400 wide and the box is 100, so <c>right: 0</c> lands
    ///     at 300 against the root and at 60 against the box — two numbers no rounding can confuse.
    /// </remarks>
    [Theory]
    [InlineData("", 360f)]
    [InlineData("contain: layout", 60f)]
    [InlineData("contain: paint", 60f)]
    [InlineData("contain: layout style", 60f)]
    [InlineData("contain: content", 60f)]
    [InlineData("contain: strict", 60f)]
    public void Layout_containment_is_the_containing_block_of_an_absolute_descendant(string declaration, float left) {
        using var document = Drawn(
            $$"""
            root { display: block; width: 400px; height: 300px; }
            .box { display: block; position: static; width: 100px; height: 100px; {{declaration}} }
            .pinned { display: block; position: absolute; right: 0; width: 40px; height: 10px; }
            """,
            document => document.Root.Add("div", classNames: "box").Add("div", classNames: "pinned")
        );

        var pinned = document.Root.Children[0].Children[0];

        Assert.Equal(left, pinned.AbsoluteLeft, Tolerance);
    }

    /// <summary><c>style</c> is understood, contributes nothing, and does not poison what it sits with.</summary>
    /// <remarks>
    ///     ⚠ <b>The measured refusal, written as a test rather than only as a ledger note.</b> CSS
    ///     Containment § 3.4 scopes counters and quotes and this engine has neither, so the keyword
    ///     computes and moves nothing — but "moves nothing" and "is not understood" are two different
    ///     answers, and only one of them leaves <c>contain: layout style</c> still containing layout.
    ///     The theory above pins that half; this pins the other, that <c>style</c> alone is inert.
    /// </remarks>
    [Fact]
    public void Style_containment_is_understood_and_moves_nothing() {
        using var document = WithBox("contain: style");
        var box = document.Root.Children[0];

        Assert.Equal(40f, box.Height, Tolerance);
        Assert.Empty(Pushes(document));
    }

    /// <summary>A value with a word in it that is not a keyword drops the whole declaration.</summary>
    /// <remarks>
    ///     What CSS does with a value it cannot parse, and the reason it matters here: a reader that
    ///     took the words it recognised and ignored the rest would read a future keyword as a subset
    ///     of itself, quietly containing less than the author asked for.
    /// </remarks>
    [Fact]
    public void An_unrecognised_word_drops_the_declaration_rather_than_part_of_it() {
        using var document = WithBox("contain: layout wobble");
        var box = document.Root.Children[0];

        Assert.Equal(40f, box.Height, Tolerance);
        Assert.Empty(Pushes(document));
    }
}
