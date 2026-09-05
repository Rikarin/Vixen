// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using CsCheck;
using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>The frame pass restyling incrementally, judged against a document built cold.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Deliberately driven through <see cref="UiDocument" /> rather than through
///         <see cref="StyleUpdater" />.</b> <c>Vixen.Ui.Styling.Tests.IncrementalRestyleOracleTests</c>
///         already runs this property against the updater and has been green since Phase 4b — while
///         <see cref="UiDocument.Update" /> called <c>StyleEngine.ResolveAll</c> and never touched the
///         updater at all. Every claim about incremental restyling was true of an object nothing in
///         the framework called.
///     </para>
///     <para>
///         So what is under test here is the <i>wiring</i>: that a class change goes through
///         <see cref="UiElement.AddClass" /> into the record, that the record is replayed, that
///         anything the record cannot express falls back to a cold pass, and that the answer is the
///         same either way. A test that reached for the updater directly would pass with the wiring
///         deleted, which is exactly how this went unnoticed for two phases.
///     </para>
/// </remarks>
public class IncrementalDocumentTests {
    /// <summary>The classes the generated stylesheets and mutations draw from.</summary>
    static readonly string[] Names = ["a", "b", "c", "d"];

    [Fact]
    public void An_incremental_pass_produces_exactly_what_a_cold_one_produces() {
        var incrementalPasses = 0;
        var styledElements = 0;
        var stateMutations = 0;

        Gen.Select(Gen.Int[0, 100_000], Gen.Int[2, 4], Gen.Int[2, 3], Gen.Int[4, 12]).Sample(shape => {
                var (seed, depth, breadth, mutations) = shape;
                var css = Stylesheet(seed);
                var random = new Random(seed ^ 0x51D2);

                using var live = new UiDocument(400f, 300f);
                live.Load(css);
                var elements = Build(live, seed, depth, breadth);

                // The baseline pass. Everything after it must be incremental.
                live.Update();

                for (var m = 0; m < mutations; m++) {
                    stateMutations += Mutate(elements, random) ? 1 : 0;
                }

                live.Update();

                if (!live.LastPassWasCold) {
                    incrementalPasses++;
                }

                // The oracle is a second document built *directly* in the state the mutations left
                // the first one in — not the same document with the same mutations replayed. Both
                // sides would then reach their final state through the same mutation code, so
                // anything that code gets wrong is wrong identically on both and the comparison sees
                // nothing. `IncrementalRestyleOracleTests` learned that the hard way in 4b.
                using var cold = new UiDocument(400f, 300f);
                cold.Load(css);
                var reference = Build(cold, seed, depth, breadth);
                Copy(elements, reference);
                cold.Update();

                Assert.True(cold.LastPassWasCold);

                for (var i = 0; i < elements.Count; i++) {
                    styledElements += elements[i].Style.Count > 0 ? 1 : 0;

                    Assert.Equal(
                        Describe(cold, reference[i].Style),
                        Describe(live, elements[i].Style)
                    );
                }
            }, iter: 300
        );

        // ⚠ **Three coverage assertions, and the first version of this test had only one.** A
        // property that compares two documents is perfectly happy comparing two documents where
        // nothing matched anything — and that is what the first draft did, because `Build` gave its
        // elements no classes and every generated rule needed one. Every rule was dead, both sides
        // resolved to nothing, and dropping state changes from the replay entirely **passed 300 of
        // 300 iterations**. Found by sabotage, which is the only thing that could have found it: the
        // property was green, the vacuity guard about incremental passes was green, and the suite was
        // asserting that two empty documents agree.
        Assert.True(incrementalPasses > 250, $"only {incrementalPasses} of 300 passes were incremental.");
        Assert.True(styledElements > 2_000, $"only {styledElements} elements resolved to a non-empty style.");
        Assert.True(stateMutations > 200, $"only {stateMutations} mutations were state changes.");
    }

    // Verified by sabotage, against the whole of Vixen.Ui.Tests:
    //
    //   dropping state changes from the replay          fails 2  (1 before the coverage assertions)
    //   a structural change staying narrow              fails 74
    //   the inline block dropped from a resolve         fails 7
    //   a record that keeps only the last change        fails 2
    //   a scroll invalidating everything, as before     fails 1
    //   a cold pass that does not clear the flag        fails 7
    //
    // ⚠ **One failed to fail**: deleting `Restyler.Compact` from `UiDocument.CompactStyles`. It is
    // unreachable, not untested — compaction forces the next pass to be cold and a cold pass rewrites
    // every entry of the array the remap just fixed. Labelled as insurance where it lives, with what
    // would make it necessary written beside it.

    [Fact]
    public void Toggling_one_class_on_a_grid_resolves_a_handful_of_elements() {
        // Phase 4b's invalidation-minimality gate, asked of the document instead of the updater.
        using var document = new UiDocument(1000f, 1000f);
        document.Load("""
            root { flex-direction: column; }
            .row { flex-direction: row; }
            .cell { width: 8px; height: 8px; }
            .row.selected { background-color: #ff0000; }
        """);

        var rows = new UiElement[100];

        for (var r = 0; r < 100; r++) {
            rows[r] = document.Root.Add("div", classNames: "row");

            for (var c = 0; c < 100; c++) {
                rows[r].Add("div", classNames: "cell");
            }
        }

        document.Update();
        Assert.True(document.LastPassWasCold);
        Assert.Equal(10_101, document.StylesResolved);

        rows[50].AddClass("selected");
        document.Update();

        Assert.False(document.LastPassWasCold);

        // ⚠ The number that used to be 10 101 — and `StylesApplied` read 1 either way, which is why
        // nothing noticed. `background-color` is not inherited, so the row's hundred cells are
        // correctly not descended into.
        Assert.Equal(1, document.StylesResolved);
        Assert.Equal(1, document.StylesApplied);
    }

    [Fact]
    public void A_scroll_resolves_nothing_at_all() {
        using var document = new UiDocument(400f, 300f);
        document.Load("""
            root { width: 400px; height: 300px; }
            .box { width: 100px; height: 100px; }
        """);

        var box = document.Root.Add("div", classNames: "box");
        document.Update();

        box.OffsetY = -40f;

        Assert.True(document.Update());
        Assert.Equal(0, document.StylesResolved);
        Assert.Equal(0, document.StylesApplied);

        // And it really did move — a pass that resolved nothing is only free if it still did its job.
        Assert.Equal(-40f, box.AbsoluteTop, 0.001f);
    }

    /// <summary>And a frame that runs no pass at all reports nought, rather than the last one's number.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The counters are about the frame, and on the early-return path two of the three
    ///         used to be about whichever earlier frame had last done work.</b>
    ///         <see cref="UiDocument.Update" /> returns before it touches anything when nothing is
    ///         dirty, and cleared <c>StylesApplied</c> there and neither <c>StylesResolved</c> nor
    ///         <c>ContainerScopesEntered</c> — so a settled document reported a few hundred elements
    ///         cascaded on every frame of standing still, for the life of the session. #596.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What makes it worth a test rather than a tidy-up is that these are read by
    ///         gates.</b> A diagnostic that reads as work on a frame that did none is the shape of
    ///         fault this repository keeps meeting, and this one had already cost a gate its natural
    ///         assertion: <c>EditorShellBudgetTests</c> wanted "a settled shell cascades nothing" —
    ///         <c>StylesResolved == 0</c> — found it red against a correct implementation, and had to
    ///         be written round it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both halves, because a counter stuck at nought would pass the second one.</b> The
    ///         first pass has to be shown non-zero on the same document, or "reads zero" is a
    ///         statement about a field nothing ever writes.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_frame_that_runs_no_pass_reports_no_work() {
        using var document = new UiDocument(400f, 300f);
        document.Load("""
            root { width: 400px; height: 300px; }
            .box { width: 100px; height: 100px; }
            @container (min-width: 50px) { .inner { height: 10px; } }
            .box { container-type: inline-size; }
            """);

        document.Root.Add("div", classNames: "box").Add("div", classNames: "inner");

        // The floor, and it has to be the *first* pass. A later `Invalidate` re-cascades and reads
        // `StylesResolved > 0` but `StylesApplied == 0`, because the computed styles intern to the
        // same objects and nothing's layout style is rebuilt — which is the distinction between the
        // two counters that `Restyle.cs` spends a paragraph on.
        Assert.True(document.Update(), "the document was clean before it had ever been laid out");
        Assert.True(document.StylesResolved > 0, "the cold pass cascaded no elements");
        Assert.True(document.StylesApplied > 0, "the cold pass wrote no layout style");
        Assert.True(document.ContainerScopesEntered > 0, "the cold pass interned no container chain");

        // Settled, with a ceiling that is a hang check and not a budget.
        var settled = 0;

        for (var i = 0; i < 10 && document.Update(); i++) {
            settled = i + 1;
        }

        Assert.True(settled < 10, "the document never stopped dirtying itself");

        // And then the frame an application spends most of its time in.
        Assert.False(document.Update(), "a settled document reported work to do");

        Assert.Equal(0, document.StylesResolved);
        Assert.Equal(0, document.StylesApplied);
        Assert.Equal(0, document.ContainerScopesEntered);
    }

    [Fact]
    public void An_inline_style_survives_an_incremental_pass() {
        // The bug wiring this up found. `StyleUpdater.Resolve` did not pass the element's inline
        // block, because inline styles arrived in 4e and the updater was written in 4b with nothing
        // between them to notice. Every declaration written on an element vanished the moment the
        // pass became incremental — a splitter losing its ratio on the first hover.
        using var document = new UiDocument(400f, 300f);
        document.Load("""
            root { width: 400px; height: 300px; }
            .box { width: 50px; height: 20px; }
        """);

        var box = document.Root.Add("div", classNames: "box");
        box.SetStyle("width", "137px");
        document.Update();

        Assert.Equal(137f, box.Width, 0.001f);

        // Now force the incremental path and check the inline block is still there.
        box.AddClass("marked");
        document.Update();

        Assert.False(document.LastPassWasCold);
        Assert.Equal(137f, box.Width, 0.001f);
    }

    [Fact]
    public void A_change_the_record_cannot_express_falls_back_to_a_cold_pass() {
        using var document = new UiDocument(400f, 300f);
        document.Load("root { width: 400px; }");

        var box = document.Root.Add("div");
        document.Update();

        // A class change is narrow.
        box.AddClass("a");
        document.Update();
        Assert.False(document.LastPassWasCold);

        // Creating an element is not, and must not be quietly treated as one — a new element has no
        // resolved style at all, and no invalidation root reaches it.
        var fresh = document.Root.Add("div");
        document.Update();
        Assert.True(document.LastPassWasCold);
        Assert.NotSame(ComputedStyle.Empty, fresh.Style);

        // Nor is an inline style, nor a move, nor a stylesheet.
        box.SetStyle("width", "10px");
        document.Update();
        Assert.True(document.LastPassWasCold);

        box.AddClass("b");
        document.Update();
        Assert.False(document.LastPassWasCold);

        document.Move(box, 1);
        document.Update();
        Assert.True(document.LastPassWasCold);
    }

    [Fact]
    public void Several_changes_in_one_frame_are_all_replayed() {
        using var document = new UiDocument(400f, 300f);
        document.Load("""
            root { width: 400px; height: 300px; }
            .marked { width: 33px; }
            .lit { height: 21px; }
        """);

        var first = document.Root.Add("div");
        var second = document.Root.Add("div");
        var third = document.Root.Add("div");
        document.Update();

        // Three changes, one pass. A record that kept only the last would leave two of them unstyled
        // and look completely correct on any test that made one change at a time.
        first.AddClass("marked");
        second.AddClass("lit");
        third.AddClass("marked");
        document.Update();

        Assert.False(document.LastPassWasCold);
        Assert.Equal(33f, first.Width, 0.001f);
        Assert.Equal(21f, second.Height, 0.001f);
        Assert.Equal(33f, third.Width, 0.001f);
    }

    [Fact]
    public void An_ancestors_state_still_reaches_its_descendants() {
        // The sharing-cache regression from 4e, re-asserted against the incremental path — where the
        // pass scoping is `StyleUpdater`'s to get right rather than `StyleEngine.ResolveAll`'s.
        using var document = new UiDocument(400f, 300f);
        document.Load("""
            root { width: 400px; height: 300px; }
            .card .button { width: 10px; }
            .card:hover .button { width: 80px; }
        """);

        var card = document.Root.Add("div", classNames: "card");
        var button = card.Add("div", classNames: "button");
        document.Update();

        Assert.Equal(10f, button.Width, 0.001f);

        card.State |= ElementState.Hover;
        document.Update();

        Assert.False(document.LastPassWasCold);
        Assert.Equal(80f, button.Width, 0.001f);
    }

    /// <summary>A stylesheet whose rules reach in every direction a change has to be chased.</summary>
    static string Stylesheet(int seed) {
        var random = new Random(seed);
        var css = new System.Text.StringBuilder("root { width: 400px; height: 300px; }\n");

        for (var i = 0; i < 6; i++) {
            var left = Names[random.Next(Names.Length)];
            var right = Names[random.Next(Names.Length)];
            var width = (i + 2) * 3;

            css.Append(
                CultureInfo.InvariantCulture,
                $"{random.Next(4) switch {
                    0 => $".{left}",
                    1 => $".{left} .{right}",
                    2 => $".{left}:hover .{right}",
                    _ => $".{left} > .{right}"
                }} {{ width: {width}px; color: #{i}{i}{i}{i}{i}{i}; }}\n"
            );
        }

        return css.ToString();
    }

    /// <summary>Builds a tree whose elements already carry classes the stylesheet selects on.</summary>
    /// <remarks>
    ///     ⚠ <b>The classes are the point and leaving them out made the whole property vacuous.</b> A
    ///     generated rule is <c>.a .b</c> or <c>.a:hover .b</c>; a tree of bare <c>div</c>s matches
    ///     none of them, so both documents resolve every element to nothing and agree exactly however
    ///     broken the thing under test is. Seeded from the same number as the stylesheet so the
    ///     reference tree is built identically without being copied from the live one.
    /// </remarks>
    static List<UiElement> Build(UiDocument document, int seed, int depth, int breadth) {
        var elements = new List<UiElement>();
        Descend(document.Root, depth, breadth, elements, new Random(seed ^ 0x2B0D));
        return elements;
    }

    static void Descend(UiElement parent, int depth, int breadth, List<UiElement> elements, Random random) {
        if (depth == 0) {
            return;
        }

        for (var i = 0; i < breadth; i++) {
            var child = parent.Add("div");

            for (var n = random.Next(3); n > 0; n--) {
                child.AddClass(Names[random.Next(Names.Length)]);
            }

            elements.Add(child);
            Descend(child, depth - 1, breadth, elements, random);
        }
    }

    /// <summary>Makes one change, and says whether it was a state change.</summary>
    static bool Mutate(List<UiElement> elements, Random random) {
        var element = elements[random.Next(elements.Count)];

        if (random.Next(3) == 0) {
            element.State ^= ElementState.Hover;
            return true;
        }

        var name = Names[random.Next(Names.Length)];

        if (!element.RemoveClass(name)) {
            element.AddClass(name);
        }

        return false;
    }

    /// <summary>Puts the second tree into the first's final state, without replaying anything.</summary>
    static void Copy(List<UiElement> from, List<UiElement> to) {
        for (var i = 0; i < from.Count; i++) {
            foreach (var name in Names) {
                if (from[i].HasClass(name)) {
                    to[i].AddClass(name);
                } else {
                    to[i].RemoveClass(name);
                }
            }

            to[i].State = from[i].State;
        }
    }

    /// <summary>A computed style as text, so two engines' interned objects can be compared.</summary>
    /// <remarks>
    ///     ⚠ Two documents intern separately, so the same style is two different objects and reference
    ///     equality would fail on every element. By value, and by <i>name</i>, because the property and
    ///     value ids are per-engine too.
    /// </remarks>
    static string Describe(UiDocument document, ComputedStyle style) {
        var text = new System.Text.StringBuilder();

        for (var i = 0; i < style.Count; i++) {
            text.Append(document.Styles.Properties.NameOf(style.Properties[i]))
                .Append('=')
                .Append(document.Styles.Values.NameOf(style.Values[i]))
                .Append(';');
        }

        return text.ToString();
    }
}
