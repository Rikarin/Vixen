// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax;
using Vixen.Core.Syntax.Diagnostics;
using Vixen.Core.Syntax.InternalSyntax;
using Vixen.Raven;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Syntax;
using Xunit;

namespace Tests;

/// <summary>
///     What compiling the whole shipped library costs, expressed as work rather than as a duration.
/// </summary>
/// <remarks>
///     <para>
///         <b>doc 07 § G's <c>Perf</c> row, second half.</b> The first half — a leaf shader's
///         incremental reparse, the one with the 500 ms budget on it — is
///         <see cref="IncrementalParseWorkTests" />. This is the other: *"a number that regresses
///         tells you a lowering or emitter change made everything slower, which is invisible
///         per-test"*.
///     </para>
///     <para>
///         ⚠ <b>It is not the duration the row asked for and does not pretend to be.</b> A
///         wall-clock number is this repository's largest flake source, and it is worse here than
///         usual: the machine this is measured on runs fifteen agents at once, so a second recorded
///         on it is a fact about the other fourteen. What is machine-independent is how much the
///         compiler <em>does</em>, and the part of that a lowering change moves is how many SSA
///         values the library lowers to.
///     </para>
///     <para>
///         <b>So the number is a ratio and not a total.</b> A total would be a golden number that
///         moves whenever somebody writes a shader — the library has grown from 47 files to over a
///         hundred while this row sat open. Values per parsed green node does not: a new shader adds
///         to both halves, and what moves it is the compiler starting to emit more IR for the same
///         source, which is exactly the regression the row is about.
///     </para>
///     <para>
///         ⚠ <b>What it cannot see</b>, said here rather than discovered later: time spent in the
///         binder, the emitters or `spirv-val` leaves no trace in this count, and a lowering that
///         got slower without getting larger passes it. The honest full number still wants CI's own
///         hardware, recorded against itself, beside <c>CheckShaders</c>.
///     </para>
/// </remarks>
public class LibraryCompileWorkTests {
    /// <summary>Where the shipped library is, relative to the test binary.</summary>
    /// <remarks>
    ///     Anchored on <see cref="AppContext.BaseDirectory" /> for the reason
    ///     <see cref="IncrementalParseWorkTests" /> gives: CI rewrites every compiled source path to
    ///     <c>/_/…</c>, so a test that found its own file by <c>[CallerFilePath]</c> fails on all
    ///     three runners at once.
    /// </remarks>
    static string LibraryRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Library"));

    /// <summary>
    ///     The library's lowered size stays within a fixed multiple of its source size.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The bound is deliberately loose, for the reason the incremental gate's is: a tight
    ///         one would be a golden number wearing a ratio's clothes and would go red on the next
    ///         shader somebody writes. What it is placed to catch is a change of <em>kind</em> — a
    ///         lowering that inlines what it used to call, a monomorphiser that stops sharing an
    ///         instantiation, a bounds check per element access — each of which multiplies the
    ///         count rather than nudging it.
    ///     </para>
    ///     <para>
    ///         ⚠ Both sides measured when this was written, and recorded because a bound with no
    ///         numbers beside it cannot be judged. The library is 28 276 SSA values against 184 915
    ///         green nodes — 0.15 per node — and the fixture below, which is nothing but chained
    ///         arithmetic, is 0.44. The bound is a quarter, so the library has 39% of headroom and
    ///         the negative control clears it by 76%.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheLibraryLowersToLessIrThanItHasSource() {
        var (values, nodes) = Measure();

        // Non-vacuous in both directions: an empty walk or a compilation that lowered nothing would
        // make any ratio true.
        Assert.True(nodes > 100_000, $"The library parses to only {nodes} green nodes, which is not the library.");
        Assert.True(values > 10_000, $"The library lowered to only {values} SSA values, so nothing was lowered.");

        Assert.True(
            values * 4 < nodes,
            $"The library lowers to {values} SSA values from {nodes} green nodes of source. Lowering now emits "
            + "more IR per unit of source than it did — look for an inline, an unroll or a check that used to be "
            + "one instruction and is now several."
        );
    }

    /// <summary>
    ///     ⚠ The ratio can exceed that bound, asserted by lowering something that makes it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The negative control the bound above is worthless without: a counter that returned
    ///         zero, or a ratio nothing could ever push over one, would make the gate green for
    ///         ever and say nothing. This fixture is dense on purpose — every line is an operation
    ///         and no line is a comment, a doc block or a declaration — which is what the library is
    ///         not, and it clears the same bound the library is a sixth of.
    ///     </para>
    ///     <para>
    ///         It is also the shape of the regression: a lowering that turned the library's calls
    ///         into inlined bodies would move it toward this fixture's density, which is the whole
    ///         reason the number is a ratio.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ADenseShaderExceedsThatBound() {
        var body = string.Join(
            "\n        ",
            Enumerable.Range(0, 200).Select(_ => "a = a * b * a * b * a * b * a * b * a * b")
        );

        var source = $$"""
                       package A

                       shader S {
                           func F(b: float3): float3 {
                               var a = float3(1f, 1f, 1f)
                               {{body}}
                               return a
                           }
                       }

                       """;

        var tree = SyntaxTree.ParseText(source, path: "Dense.rvn");
        Assert.Empty(tree.Diagnostics);

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(Compilation.Create("Dense", tree), bag);
        Assert.DoesNotContain(bag.ToArray(), d => d.IsError);

        var values = module.AllFunctions.Sum(f => f.ValueCount);
        var nodes = Count(tree.GetRoot().Green);

        Assert.True(
            values * 4 >= nodes,
            $"A shader that is nothing but arithmetic lowered to {values} SSA values from {nodes} green nodes, "
            + "so the bound in the test above cannot be exceeded by any input and proves nothing."
        );
    }

    /// <summary>
    ///     Every library file parsed, and the whole tree lowered as one compilation.
    /// </summary>
    /// <remarks>
    ///     One compilation rather than one per file, because that is what the files are: they import
    ///     each other, and it is also the arrangement whose cost the row is asking about.
    /// </remarks>
    /// <returns>SSA values across every lowered function, and green nodes across every parse.</returns>
    static (int Values, int Nodes) Measure() {
        var files = Directory
            .EnumerateFiles(LibraryRoot, "*.rvn", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(files.Length > 50, $"Only {files.Length} shaders under {LibraryRoot}.");

        var trees = files
            .Select(file => SyntaxTree.ParseText(File.ReadAllText(file), path: Path.GetFileName(file)))
            .ToArray();

        var nodes = trees.Sum(tree => Count(tree.GetRoot().Green));

        var compilation = Compilation.Create(
            "Library",
            PermutationValues.Empty,
            LibraryComposition.With(),
            trees
        );

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);

        Assert.DoesNotContain(bag.ToArray(), d => d.IsError);

        return (module.AllFunctions.Sum(f => f.ValueCount), nodes);
    }

    /// <summary>Green nodes in one tree, each counted once however many parents share it.</summary>
    /// <remarks>
    ///     ⚠ The visited set is not an optimisation, for the reason
    ///     <c>IncrementalParseWorkTests.Walk</c> gives: a green tree is a DAG and a plain recursion
    ///     would count a shared node once per parent.
    /// </remarks>
    /// <param name="root">The tree's green root.</param>
    /// <returns>The count.</returns>
    static int Count(GreenNode? root) {
        if (root is null) {
            return 0;
        }

        var seen = new HashSet<GreenNode>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<GreenNode>();
        stack.Push(root);

        while (stack.Count > 0) {
            var node = stack.Pop();

            if (!seen.Add(node)) {
                continue;
            }

            for (var slot = 0; slot < node.SlotCount; slot++) {
                if (node.GetSlot(slot) is { } child) {
                    stack.Push(child);
                }
            }
        }

        return seen.Count;
    }
}
