// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax;
using Vixen.Core.Syntax.InternalSyntax;
using Vixen.Core.Syntax.Text;
using Vixen.Raven.Syntax;
using Xunit;

namespace Tests;

/// <summary>
///     How much of a shipped leaf shader an editor keystroke actually re-parses.
/// </summary>
/// <remarks>
///     <para>
///         <b>doc 07 § G's last open row, in the only form it can honestly take.</b> That row asks for
///         a gate on "&lt; 500 ms incremental recompile of a leaf shader", and the 500 ms is
///         [doc 00](../plan/00-vision-and-principles.md)'s shader hot-reload budget. ⚠ <b>A wall-clock
///         assertion is the wrong instrument for it</b> and this repository has the scar tissue to
///         prove it: a budget calibrated on an idle machine is its single largest flake source, and a
///         parse on a loaded CI runner is exactly that shape. What the incremental path <em>promises</em>
///         is not a duration — it is that an edit inside one member re-parses that member and reuses
///         the rest. That is a count, it is machine-independent, and it is what regresses first when a
///         change to the blender quietly stops reusing.
///     </para>
///     <para>
///         <b>So the number is green nodes allocated, and it is measured as a differential.</b> A bare
///         count would be a golden number that moves whenever somebody edits the shader this reads. The
///         comparison instead is against a <em>from-scratch</em> parse of the same new text, taken in
///         the same run on the same input: the full parse is what the incremental path is supposed to
///         be cheaper than, so the ratio between the two is the property, and nothing about the
///         machine enters it.
///     </para>
///     <para>
///         ⚠ <b>What this cannot say.</b> It is a gate on parse work and not on compile time — the
///         other half of that doc row, a number for the whole library, is inherently a duration and
///         has no counter standing in for it. It is not gated here, and saying so is better than a
///         green wall-clock assertion that measures the runner.
///     </para>
/// </remarks>
public class IncrementalParseWorkTests {
    /// <summary>Where the shipped library is, relative to the test binary.</summary>
    /// <remarks>
    ///     Anchored on <see cref="AppContext.BaseDirectory" /> rather than on a
    ///     <c>[CallerFilePath]</c>: CI sets <c>DeterministicSourcePaths</c>, which rewrites every
    ///     compiled source path to <c>/_/…</c>, so a test that found its own file that way would fail
    ///     on all three runners at once and pass on every developer machine.
    /// </remarks>
    static string LibraryRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Library"));

    /// <summary>
    ///     A real leaf shader, because the claim is about what an author is editing.
    /// </summary>
    /// <remarks>
    ///     `Tonemap.rvn` is the longest file in `PostFx` and a leaf in the import graph — nothing in
    ///     the library imports it — which is the shape the budget is about: the file somebody has open
    ///     while looking at the picture. A fixture string would have been a claim about the fixture.
    /// </remarks>
    static string LeafShader => File.ReadAllText(Path.Combine(LibraryRoot, "PostFx", "Tonemap.rvn"));

    /// <summary>
    ///     A keystroke inside one function body re-parses a small fraction of what a full parse
    ///     builds.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The edit is a single character appended to a comment deep inside the file, which is the
    ///         cheapest thing an author can do and therefore the one the budget is really about.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The bound is deliberately loose.</b> A tight one would be a golden number wearing
    ///         a ratio's clothes, and would go red on any edit to `Tonemap.rvn`. An eighth is far
    ///         above what the blender achieves and far below what losing member-level reuse would
    ///         cost — which is the whole tree, since a from-scratch parse is the fallback. Measured
    ///         when this was written: 133 green nodes allocated against 3109 for a full parse, so the
    ///         bound has about a factor of three of slack in it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_keystroke_reparses_a_fraction_of_what_a_full_parse_builds() {
        var (incremental, full) = Measure(reusable: true);

        // Non-vacuous in both directions: a trivial file, or a walk that found nothing, would make
        // every ratio below true and mean nothing.
        Assert.True(full > 2000, $"The leaf shader parses to only {full} green nodes, which is too small to gate on.");
        Assert.True(incremental > 0, "The edit re-parsed nothing at all, so the measurement is not reading the edit.");

        Assert.True(
            incremental * 8 < full,
            $"A one-character edit allocated {incremental} green nodes where a full parse of the same text "
            + $"allocates {full}. Member-level reuse has regressed — see IncrementalParseTests for which "
            + "member stopped being reused."
        );
    }

    /// <summary>
    ///     ⚠ The counter can report "nothing was reused", asserted by giving it a reparse that cannot
    ///     reuse anything.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The negative control the bound above is worthless without. Handing
    ///         <see cref="SyntaxTree.WithChangedText" /> a tree parsed from <em>different</em> source
    ///         leaves it nothing to blend, which is exactly what a blender that had stopped reusing
    ///         would look like from outside — and the same measurement then has to fail the same
    ///         bound. Without this, a counter that returned zero for every input would make the gate
    ///         above green for ever.
    ///     </para>
    ///     <para>
    ///         Asserted as the bound's negation rather than as equality with <c>full</c>: the two
    ///         parses share nothing, but pinning them to the same number would pin how the blender
    ///         frames a result it could not reuse, which is an implementation detail rather than the
    ///         property.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_reparse_that_can_reuse_nothing_fails_that_bound() {
        var (incremental, full) = Measure(reusable: false);

        Assert.True(
            incremental * 8 >= full,
            $"A reparse with nothing to reuse still allocated only {incremental} of {full} green nodes, so the "
            + "counter is not measuring reuse and the bound above proves nothing."
        );
    }

    /// <summary>
    ///     Green nodes the reparse had to allocate, and the number a full parse of the same text
    ///     allocates.
    /// </summary>
    /// <remarks>
    ///     "Allocated" means "not reference-identical to a node the old tree already held". Green
    ///     nodes are immutable and position-independent, so reuse <em>is</em> reference sharing —
    ///     which makes the count exact rather than an estimate.
    /// </remarks>
    /// <param name="reusable">
    ///     Whether the tree being edited is the one the new text came from. False is the negative
    ///     control: an unrelated old tree offers nothing to blend.
    /// </param>
    static (int Incremental, int Full) Measure(bool reusable) {
        var source = LeafShader;
        var edited = SyntaxTree.ParseText(source, path: "Tonemap.rvn");

        // One character, into the last line comment in the file, so the edit is as small as an edit
        // gets and is nowhere near most of the members whose reuse is being counted.
        var at = source.LastIndexOf("//", StringComparison.Ordinal);
        Assert.True(at > 0, "The leaf shader has no line comment to edit, so this measures nothing.");

        var newText = edited.Text!.WithChanges(TextChange.Insert(at + 2, "x"));

        var oldTree = reusable
            ? edited
            : SyntaxTree.ParseText("package X\n\nshader Fresh {\n}\n", path: "Tonemap.rvn");

        var reparsed = oldTree.WithChangedText(newText);

        // The reparsed tree must still be the right tree; a blender that reused everything by
        // producing nonsense would score beautifully.
        Assert.Equal(newText.ToString(), reparsed.GetRoot().ToFullString());

        var known = new HashSet<GreenNode>(ReferenceEqualityComparer.Instance);
        Walk(oldTree.GetRoot().Green, node => known.Add(node));

        var incremental = 0;
        Walk(reparsed.GetRoot().Green, node => {
            if (!known.Contains(node)) {
                incremental++;
            }
        });

        var fresh = SyntaxTree.ParseText(newText.ToString(), path: "Tonemap.rvn");
        var full = 0;
        Walk(fresh.GetRoot().Green, _ => full++);

        return (incremental, full);
    }

    /// <summary>
    ///     Every green node in the tree, each visited once however many parents share it.
    /// </summary>
    /// <remarks>
    ///     ⚠ The visited set is not an optimisation. A green tree is a DAG — identical subtrees are
    ///     shared by construction, which is the point of the representation — so a plain recursion
    ///     would count a shared node once per parent and make the two sides of the ratio measure
    ///     different things.
    /// </remarks>
    static void Walk(GreenNode? root, Action<GreenNode> visit) {
        if (root is null) {
            return;
        }

        var seen = new HashSet<GreenNode>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<GreenNode>();
        stack.Push(root);

        while (stack.Count > 0) {
            var node = stack.Pop();

            if (!seen.Add(node)) {
                continue;
            }

            visit(node);

            for (var slot = 0; slot < node.SlotCount; slot++) {
                if (node.GetSlot(slot) is { } child) {
                    stack.Push(child);
                }
            }
        }
    }
}
