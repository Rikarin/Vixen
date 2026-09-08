// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;
using Vixen.Editor.Texturing.Layers;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>Doc 48 § D10's fourth filter kind: a graph with an <c>Input</c>, on a layer.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/1068">#1068</a>, and the asymmetry was
///         invisible from either file alone.</b> A mask's adjustments were never limited to a list —
///         <c>MaskEffectAsset.Node</c> is a node-type path, and its own remarks specify an effect as
///         anything with one image in and one image out — while <c>LayerStackGraph.Adjustment</c>
///         took its node type from <c>LayerFilterKind</c> and from nothing else. So the same sentence
///         of the same section was true of a mask and false of a layer.
///     </para>
///     <para>
///         ⚠ <b>The compound used here is a shipped one and is resolved the way the editor resolves
///         it.</b> <c>LayerStackCompiler.Compile</c>'s default library publishes
///         <c>Editor/Vixen.Editor.TextureGraph/Compounds</c>, so <c>Utility/Highpass</c> is a node
///         type this project really has rather than a fixture's invention — which matters, because a
///         test that built its own single-image node type would be asserting against a graph nobody
///         ships.
///     </para>
/// </remarks>
public class FilterCompoundTests {
    /// <summary>A shipped compound with one image in and one image out — § D10's own example.</summary>
    const string Highpass = "Utility/Highpass";

    /// <summary>⚠ A filter layer can be a published compound, and it is that compound that runs.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Three plans rather than an assertion that one compiled.</b> "It compiles with no
    ///         problems" is satisfied by a branch that silently did nothing — a filter layer that
    ///         resolved to no node at all leaves the stack looking exactly like a stack with one
    ///         fewer layer, and every diagnostic list stays empty.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it must differ from the <c>Levels</c> plan too</b>, which is the half that
    ///         says the <em>named</em> node ran. A branch that fell through to the enum path would
    ///         compile a <c>Colour/Levels</c> — a real node, a real difference from no filter at all,
    ///         and the wrong picture.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_filter_layer_can_name_a_published_compound() {
        var compound = Compile(new() { Id = "f", Kind = LayerKind.Filter, FilterNode = Highpass });

        Assert.NotNull(compound.Plan);
        Assert.Empty(compound.Problems);
        Assert.Empty(compound.Diagnostics);

        var none = Compile(null);
        var levels = Compile(new() { Id = "f", Kind = LayerKind.Filter, Filter = LayerFilterKind.Levels });

        Assert.NotNull(none.Plan);
        Assert.NotNull(levels.Plan);

        Assert.NotEqual(
            LayerStackDifferential.Describe(none.Plan),
            LayerStackDifferential.Describe(compound.Plan)
        );

        Assert.NotEqual(
            LayerStackDifferential.Describe(levels.Plan),
            LayerStackDifferential.Describe(compound.Plan)
        );
    }

    /// <summary>⚠ The named node wins over the kind, because the kind's default is a real value.</summary>
    /// <remarks>
    ///     <b><c>LayerFilterKind.Levels</c> is zero</b>, so every filter layer that says nothing about
    ///     <c>filter</c> already says <c>Levels</c>. A file naming a compound therefore carries two
    ///     instructions whether its author meant to or not, and the compiler has to prefer one. This
    ///     is that choice made checkable rather than left to the order of two branches.
    /// </remarks>
    [Fact]
    public void A_named_node_wins_over_the_kind() {
        var both = Compile(
            new() { Id = "f", Kind = LayerKind.Filter, FilterNode = Highpass, Filter = LayerFilterKind.Blur }
        );

        var named = Compile(new() { Id = "f", Kind = LayerKind.Filter, FilterNode = Highpass });
        var blur = Compile(new() { Id = "f", Kind = LayerKind.Filter, Filter = LayerFilterKind.Blur });

        Assert.NotNull(both.Plan);
        Assert.NotNull(named.Plan);
        Assert.NotNull(blur.Plan);

        Assert.Equal(LayerStackDifferential.Describe(named.Plan), LayerStackDifferential.Describe(both.Plan));
        Assert.NotEqual(LayerStackDifferential.Describe(blur.Plan), LayerStackDifferential.Describe(both.Plan));
    }

    /// <summary>⚠ A node type this project does not have is refused, rather than dropping the layer.</summary>
    /// <remarks>
    ///     <b>A refusal and not a warning, which is where this parts company with a mask effect.</b>
    ///     <c>LayerStackGraph.Effect</c> answers a bad node by handing the cursor back — a mask with
    ///     one broken adjustment is still a mask. A filter layer <em>is</em> its adjustment, so the
    ///     same answer here would delete the layer from the picture and leave a file that looks like a
    ///     working stack with one layer switched off.
    /// </remarks>
    [Fact]
    public void A_filter_layer_naming_a_type_this_project_does_not_have_is_refused() {
        var compilation = Compile(
            new() { Id = "f", Kind = LayerKind.Filter, FilterNode = "Generators/Not A Compound" }
        );

        var refusal = Assert.Single(compilation.Problems, problem => problem.Severity == NodeSeverity.Error);

        Assert.Contains("Not A Compound", refusal.Message, StringComparison.Ordinal);
        Assert.Equal("f", refusal.Layer);
    }

    /// <summary>⚠ A node with two images in is a composite rather than an adjustment.</summary>
    /// <remarks>
    ///     <b>Which of <c>Colour/Blend</c>'s two images the layers beneath would be is not something a
    ///     stack file can decide</b>, and picking one — the first declared, say — is the shape of
    ///     answer that produces a picture nobody asked for and no diagnostic. The refusal names both
    ///     counts so the author can see why.
    /// </remarks>
    [Fact]
    public void A_filter_layer_naming_a_two_image_node_is_refused() {
        var compilation = Compile(new() { Id = "f", Kind = LayerKind.Filter, FilterNode = "Colour/Blend" });
        var refusal = Assert.Single(compilation.Problems, problem => problem.Severity == NodeSeverity.Error);

        Assert.Contains("single-input graph", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("2 in", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>⚠ A number reaches the node's port, and one named after the image input does not.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The ports are derived from the type rather than listed, and what that protects is
    ///         the image wire.</b> <c>LayerFilterKind</c>'s path carries a hand-written port list per
    ///         filter precisely so that a setting called <c>Input</c> cannot be written to the port
    ///         the layers beneath arrive on; a compound's ports cannot be listed here, so the check is
    ///         the one <c>MaskEffectAsset</c>'s remarks already specify.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both halves, because either alone is green for the wrong reason.</b> A rule that
    ///         dropped <em>every</em> setting would pass the second assertion and fail nothing else —
    ///         the compound filter would silently run on its defaults, which is what
    ///         <c>FilterLayerTests</c> was written to catch one enum member along. So the first half
    ///         proves a real port arrives, by difference.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_number_reaches_a_named_nodes_port_and_one_named_after_the_image_does_not() {
        LayerAsset plain = new() { Id = "f", Kind = LayerKind.Filter, FilterNode = "Colour/Levels" };
        LayerAsset set = new() { Id = "f", Kind = LayerKind.Filter, FilterNode = "Colour/Levels" };
        LayerAsset stolen = new() { Id = "f", Kind = LayerKind.Filter, FilterNode = "Colour/Levels" };

        set.Settings["Input Black"] = [0.75f];
        stolen.Settings["Input"] = [0.75f];

        var before = Compile(plain);
        var after = Compile(set);
        var wrong = Compile(stolen);

        Assert.NotNull(before.Plan);
        Assert.NotNull(after.Plan);
        Assert.NotNull(wrong.Plan);
        Assert.Empty(after.Problems);

        Assert.NotEqual(
            LayerStackDifferential.Describe(before.Plan),
            LayerStackDifferential.Describe(after.Plan)
        );

        // The value named after the image input is dropped and said so, and the plan is the plan of a
        // layer that set nothing — rather than one whose backdrop was replaced by a constant.
        var warning = Assert.Single(wrong.Problems);

        Assert.Equal(NodeSeverity.Warning, warning.Severity);
        Assert.Contains("Input", warning.Message, StringComparison.Ordinal);
        Assert.Equal(
            LayerStackDifferential.Describe(before.Plan),
            LayerStackDifferential.Describe(wrong.Plan)
        );
    }

    /// <summary>⚠ It survives the file, and the file never carries both instructions.</summary>
    /// <remarks>
    ///     <b>A member the writer forgot is a member that reads back as its default</b>, which for
    ///     this one is "not a compound" — an artist's compound filter would silently become a
    ///     <c>Levels</c> the next time the stack was saved, because <c>LayerFilterKind.Levels</c> is
    ///     zero. The second assertion is the other half: a file carrying <c>filter</c> as well would
    ///     say one thing and compile another.
    /// </remarks>
    [Fact]
    public void A_compound_filter_survives_the_file() {
        LayerStackAsset stack = Stack(new() { Id = "f", Kind = LayerKind.Filter, FilterNode = Highpass });

        var text = LayerStackYaml.Write(stack);
        var read = LayerStackYaml.Read(text);
        var layer = read.Sets[0].Layers.Single(entry => entry.Id == "f");

        Assert.Equal(Highpass, layer.FilterNode);
        Assert.DoesNotContain("filter:", text, StringComparison.Ordinal);

        var before = LayerStackCompiler.Compile(stack, stack.Sets[0]);
        var after = LayerStackCompiler.Compile(read, read.Sets[0]);

        Assert.NotNull(before.Plan);
        Assert.NotNull(after.Plan);
        LayerStackDifferential.AssertSamePlan(before.Plan, after.Plan);
    }

    /// <summary>The stack these tests compile, with one filter layer over a constant fill or none.</summary>
    /// <param name="filter">The filter layer, or <see langword="null" /> for a stack without one.</param>
    /// <returns>The stack.</returns>
    static LayerStackAsset Stack(LayerAsset? filter) {
        LayerAsset fill = new() {
            Id = "base",
            Kind = LayerKind.Fill,
            Fill = LayerFillSource.Constant
        };

        fill.Values["baseColor"] = [0.5f, 0.25f, 0.75f, 1f];

        return new() {
            Name = "Filters",
            BaseWidth = 16,
            BaseHeight = 16,
            Seed = 9u,
            Sets = [
                new() {
                    Name = "S",
                    Channels = [new() { Usage = "baseColor", Default = [0.5f, 0.25f, 0.75f, 1f] }],
                    Layers = filter is null ? [fill] : [fill, filter]
                }
            ]
        };
    }

    static LayerStackCompilation Compile(LayerAsset? filter) {
        var stack = Stack(filter);

        return LayerStackCompiler.Compile(stack, stack.Sets[0]);
    }
}
