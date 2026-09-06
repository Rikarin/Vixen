// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Texturing.Layers;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>The diagnostic half of the panel's message list, over diagnostics a stack really raises.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The half of <a href="https://github.com/Rikarin/Vixen/issues/830">#830</a> that had
///         no test with a reachable production input.</b> The one test of the new list feeds a
///         <c>Wobble</c> setting the filter node does not declare — which <c>LayerStackGraph</c>
///         catches itself and raises as a <c>LayerStackProblem.Warning</c>, the <em>other</em> loop.
///         Nothing exercised the <c>NodeDiagnostic</c> loop against anything a stack can express, so
///         the branch that reads <c>picture.Diagnostics</c> was covered only by a hand-built record.
///     </para>
///     <para>
///         <b>A mask effect is the reachable route.</b> <c>LayerStackGraph.Effect</c> writes an
///         effect's <c>Texts</c> straight onto the node whenever the type <em>declares</em> that
///         setting — so <c>Space/Crop</c> with <c>Filter = Box</c> is a stack an artist can author,
///         a setting the node accepts as a name and the compiler refuses as a value. It arrives here
///         as <c>TG0010</c> from the texture-graph compiler, which is the loop under test.
///     </para>
///     <para>
///         ⚠ <b>And it is what refutes #870's proposed edit.</b> Two layers carrying that one mistake
///         raise <em>fourteen</em> diagnostics — seven per layer, one per channel the set writes —
///         from fourteen distinct node ids with character-identical messages. Rendering
///         <c>NodeDiagnostic.Node</c> on the line, which is what #870 asked for, would print fourteen
///         lines for two mistakes and undo
///         <a href="https://github.com/Rikarin/Vixen/issues/842">#842</a>. What the line gained
///         instead is the count, so a collapsed line is no longer silently one of N;
///         <a href="https://github.com/Rikarin/Vixen/issues/880">#880</a> is the layer id that would
///         let it name something an artist can select.
///     </para>
/// </remarks>
public class LayerStackDiagnosticListTests {
    /// <summary>A setting the compiler refuses reaches the panel's list from a stack an artist wrote.</summary>
    [Fact]
    public void A_compiler_diagnostic_from_a_mask_effect_is_listed() {
        var compilation = Compiled("only");
        var diagnostic = Assert.Single(new HashSet<string>(compilation.Diagnostics.Select(one => one.Id)));

        Assert.Equal("TG0010", diagnostic);

        var line = Assert.Single(Describe(compilation));

        Assert.StartsWith("Error — TG0010:", line, StringComparison.Ordinal);
        Assert.Contains("Crop", line, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ One mistake on one layer is still one line, and the line says how many nodes raised it.
    /// </summary>
    /// <remarks>
    ///     The per-channel multiplicity, measured rather than assumed: the count in the line is the
    ///     number of diagnostics the compile actually produced, so a stack whose set grows a channel
    ///     moves both halves together and neither is a literal.
    /// </remarks>
    [Fact]
    public void One_layers_mistake_is_one_line_that_says_how_many_nodes_raised_it() {
        var compilation = Compiled("only");
        var line = Assert.Single(Describe(compilation));

        Assert.True(compilation.Diagnostics.Length > 1, "One diagnostic, so there is no collapse to test.");

        Assert.EndsWith(
            $"· {compilation.Diagnostics.Length} nodes in the exploded graph",
            line,
            StringComparison.Ordinal
        );
    }

    /// <summary>
    ///     ⚠ Two layers making the same mistake are still one line — and the count is what says so.
    /// </summary>
    /// <remarks>
    ///     <b>The defect #870 named, and the smallest true statement about it.</b> Before this the
    ///     two stacks below produced the same single sentence, and nothing in it distinguished one
    ///     mistake from two. They still produce one sentence, because the seven copies of each are
    ///     seven nodes and telling them apart by node would print fourteen lines; what differs now is
    ///     the number on the end of it.
    /// </remarks>
    [Fact]
    public void Two_layers_making_the_same_mistake_are_told_apart_by_the_count() {
        var one = Assert.Single(Describe(Compiled("first")));
        var two = Assert.Single(Describe(Compiled("first", "second")));

        Assert.NotEqual(one, two);

        // And the difference is exactly the multiplicity rather than the wording: the message is one
        // sentence about `Crop` either way, which is what makes the count the only thing carrying it.
        Assert.StartsWith(one[..one.IndexOf('·', StringComparison.Ordinal)], two, StringComparison.Ordinal);
    }

    /// <summary>The lines the panel would render for one compilation.</summary>
    /// <remarks>
    ///     ⚠ <b>Through <c>LayerStackPicture</c> and not through a fixture, because the compile is
    ///     pure.</b> Everything under test here is known before a device is asked for, so a test that
    ///     opened a panel would be asserting this and a graphics service at once — and would skip on
    ///     the machine that has neither.
    /// </remarks>
    static IReadOnlyList<string> Describe(LayerStackCompilation compilation) {
        LayerStackPicture picture = new(null, "baseColor", 0, 0, "") {
            Problems = compilation.Problems,
            Diagnostics = compilation.Diagnostics
        };

        return LayerStackView.Describe(picture);
    }

    /// <summary>A starter stack with one filter layer per name, each masked through a refused crop.</summary>
    static LayerStackCompilation Compiled(params string[] layers) {
        var stack = LayerStackDocument.Starter("Hull");

        foreach (var id in layers) {
            stack.Sets[0]
                .Layers.Add(
                    new() {
                        Id = id,
                        Name = id,
                        Kind = LayerKind.Filter,
                        Filter = LayerFilterKind.Blur,
                        Mask = new() {
                            Source = LayerMaskSource.Constant,
                            Value = 1f,

                            // ⚠ A setting `Space/Crop` declares and the kernel refuses. `Effect` drops a
                            // setting the type does not declare with a `LayerStackProblem`, which is the
                            // other loop — this one has to survive the builder to reach the compiler.
                            Effects = { new() { Node = "Space/Crop", Texts = { ["Filter"] = "Box" } } }
                        }
                    }
                );
        }

        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.Empty(compilation.Problems);
        Assert.NotEmpty(compilation.Diagnostics);

        return compilation;
    }
}
