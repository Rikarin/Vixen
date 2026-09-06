// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Texturing.Layers;
using Vixen.Editor.Texturing.Painting;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>Which layers are in the composite below and above the one being painted.</summary>
public class PaintStackSlicesTests {
    /// <summary>The painted layer is in neither half.</summary>
    /// <remarks>
    ///     ⚠ It is the thing between them, and it is the live <c>PaintImage</c>. A slice that
    ///     included it would composite the stroke twice — once from the cache and once from the
    ///     layer — which reads as a stroke that paints at double opacity and only at 4K, because
    ///     that is where anyone would notice.
    /// </remarks>
    [Fact]
    public void The_painted_layer_is_in_neither_half() {
        var set = Set("a", "b", "c", "d");
        var slices = PaintStackSlices.Split(set, "b");

        Assert.True(slices.Succeeded, slices.Refusal);
        Assert.Equal(["a"], slices.Below!.Layers.Select(layer => layer.Id));
        Assert.Equal(["c", "d"], slices.Above!.Layers.Select(layer => layer.Id));
    }

    /// <summary>The bottom layer's below-half is empty, and that is not a refusal.</summary>
    [Fact]
    public void Painting_the_bottom_layer_leaves_an_empty_half() {
        var slices = PaintStackSlices.Split(Set("a", "b"), "a");

        Assert.True(slices.Succeeded, slices.Refusal);
        Assert.Empty(slices.Below!.Layers);
        Assert.Equal(["b"], slices.Above!.Layers.Select(layer => layer.Id));
    }

    /// <summary>The channels and the set's name travel with both halves.</summary>
    /// <remarks>
    ///     The instrument. A split that rebuilt the set from scratch would drop the channel list, and
    ///     both halves would compile to nothing at all — which produces two transparent slices and a
    ///     composite that looks exactly like a stack whose layers are all disabled.
    /// </remarks>
    [Fact]
    public void Both_halves_keep_the_sets_channels() {
        var slices = PaintStackSlices.Split(Set("a", "b", "c"), "b");

        Assert.Equal("body", slices.Below!.Name);
        Assert.Equal(["baseColor", "roughness"], slices.Below.Channels.Select(channel => channel.Usage));
        Assert.Equal(["baseColor", "roughness"], slices.Above!.Channels.Select(channel => channel.Usage));
    }

    /// <summary>A layer inside a plain organising group is sliced, flattened in composite order.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This test used to assert the opposite, and it firing is
    ///         <a href="https://github.com/Rikarin/Vixen/issues/851">#851</a>'s answer.</b> The
    ///         refusal it guarded said a group is a compositing boundary; that is true of an isolated
    ///         one and false of the kind artists actually make. <c>LayerStackGraph.Group</c> passes a
    ///         <c>Copy</c> group's children straight onto the cursor, so a group at Copy, opacity 1,
    ///         no mask and switched on <em>is</em> its flattened children as far as the composite is
    ///         concerned.
    ///     </para>
    ///     <para>
    ///         <b>The shape is what makes it an assertion about order rather than membership.</b>
    ///         Two siblings inside the group, one either side of the painted layer, and two layers
    ///         outside it, one either side of the group — so the two halves have to interleave
    ///         correctly, and a flattening that appended the outer suffix before the inner one gets
    ///         <c>["d", "c"]</c> here.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_layer_inside_a_plain_group_is_sliced_in_composite_order() {
        var slices = PaintStackSlices.Split(Grouped(), "inner");

        Assert.True(slices.Succeeded, slices.Refusal);
        Assert.Equal(["a", "b"], slices.Below!.Layers.Select(layer => layer.Id));
        Assert.Equal(["c", "d"], slices.Above!.Layers.Select(layer => layer.Id));
    }

    /// <summary>⚠ A group that really is a compositing boundary still refuses, and says which of the five.</summary>
    /// <remarks>
    ///     <para>
    ///         Each of these puts an operation between the painted layer and the canvas that no
    ///         prefix and suffix can express. The message names the property rather than the group,
    ///         because "move the layer out" is the wrong advice when setting the opacity back to one
    ///         would do.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The <c>channels</c> case is
    ///         <a href="https://github.com/Rikarin/Vixen/issues/890">#890</a>, and the enumeration
    ///         asserted itself complete without it.</b> The other four say how a group composites;
    ///         that one says which channels it composites into at all, which
    ///         <c>LayerStackGraph.Layer</c> applies to a group exactly as it applies to a leaf —
    ///         asserted one test along, because a refusal for a restriction the compiler did not
    ///         honour would be a refusal for no reason.
    ///     </para>
    ///     <para>
    ///         The issue number is a parameter rather than an <c>if</c>, so a case added without one
    ///         has to say which citation it expects instead of inheriting a neighbour's.
    ///     </para>
    /// </remarks>
    /// <param name="what">Which property is broken.</param>
    /// <param name="issue">The issue the refusal must cite, or empty when it cites none.</param>
    [Theory]
    [InlineData("blend", "#851")]
    [InlineData("opacity", "#851")]
    [InlineData("mask", "#851")]
    [InlineData("channels", "#890")]
    [InlineData("disabled", "")]
    public void A_group_that_composites_is_refused_and_the_refusal_names_what_made_it_one(
        string what,
        string issue
    ) {
        var set = Grouped();

        set.Layers[1] = what switch {
            "blend" => set.Layers[1] with { Blend = LayerBlendMode.Multiply },
            "opacity" => set.Layers[1] with { Opacity = 0.5f },
            "mask" => set.Layers[1] with { Mask = new() { Source = LayerMaskSource.Constant, Value = 0.5f } },
            "channels" => set.Layers[1] with { Channels = ["baseColor"] },
            _ => set.Layers[1] with { Enabled = false }
        };

        var slices = PaintStackSlices.Split(set, "inner");

        Assert.False(slices.Succeeded);
        Assert.Contains("Wrap", slices.Refusal, StringComparison.Ordinal);

        // A switched-off group and a channel-restricted one are different problems with different
        // advice, so they are not the ones citing the issue about compiling over a backdrop.
        if (issue.Length > 0) {
            Assert.Contains(issue, slices.Refusal, StringComparison.Ordinal);
        }
    }

    /// <summary>⚠ The compiler really does apply a group's channel restriction to the group.</summary>
    /// <remarks>
    ///     <b>The instrument for the refusal above, and the half a slices test cannot see.</b>
    ///     <c>PaintStackSlices</c> refuses a channel-restricted group because
    ///     <c>LayerStackGraph.Layer</c> gates every layer — groups included — on
    ///     <c>Writes(channel.Usage)</c>, so a group restricted to <c>baseColor</c> composites nothing
    ///     into <c>roughness</c> while its flattened children, whose own channel lists are empty and
    ///     therefore mean all, would. If that gate ever stopped applying to groups the refusal would
    ///     be a refusal for no reason, and nothing else in this file would notice: the same set is
    ///     built twice here, once restricted and once not, so the expected value is a comparison
    ///     rather than a node count anybody has to re-bless.
    /// </remarks>
    [Fact]
    public void A_channel_restricted_group_composites_into_fewer_channels_than_an_unrestricted_one() {
        var open = Painting();
        var restricted = open with { Layers = [open.Layers[0] with { Channels = ["baseColor"] }] };

        var whole = Nodes(open);
        var half = Nodes(restricted);

        Assert.True(
            half < whole,
            $"A group restricted to baseColor built {half} nodes and an unrestricted one {whole}. The "
            + "compiler is not applying a group's channel restriction to the group, so PaintStackSlices "
            + "refuses a stack that would have sliced correctly."
        );
    }

    /// <summary>⚠ A non-group carrying children is refused, not reported as a success with nothing in it.</summary>
    /// <remarks>
    ///     <b>The defect this is written against reports <c>Succeeded</c>.</b> <c>Cut</c> skipped any
    ///     carrier that was not a group and fell off the end of the loop with both halves still
    ///     empty; the missing-id guard then did not fire either, because <c>Contains</c> walks
    ///     <c>Children</c> whatever the layer's kind — so <c>Split</c> returned two empty stacks and
    ///     every other layer in the set silently gone, which composites as a stroke on an empty
    ///     canvas. <a href="https://github.com/Rikarin/Vixen/issues/892">#892</a>. Only a hand-edited
    ///     <c>.vxlayers</c> makes one, which is exactly why it must not be the silent case.
    /// </remarks>
    [Fact]
    public void A_layer_nested_under_something_that_is_not_a_group_is_refused() {
        var set = Grouped();

        set.Layers[1] = set.Layers[1] with { Kind = LayerKind.Fill, Name = "Painted" };

        var slices = PaintStackSlices.Split(set, "inner");

        Assert.False(slices.Succeeded);
        Assert.Null(slices.Below);
        Assert.Null(slices.Above);
        Assert.Contains("Painted", slices.Refusal, StringComparison.Ordinal);
        Assert.Contains("Fill", slices.Refusal, StringComparison.Ordinal);
    }

    /// <summary>⚠ The upper half starts from nothing, or the painted layer is invisible under it.</summary>
    /// <remarks>
    ///     <b>The defect this is written against is silent and total.</b>
    ///     <c>LayerStackGraph</c> starts every channel from <c>ChannelAsset.Default</c>, and every
    ///     default a stack ships with is opaque — so an upper half compiled with the authored
    ///     defaults bakes an opaque picture, and <c>PaintComposite</c>'s source-over of it onto
    ///     anything returns it unchanged. Every stroke would land in the layer, be recorded, undo
    ///     correctly, and never appear.
    /// </remarks>
    [Fact]
    public void The_upper_half_starts_from_transparency_and_the_lower_half_does_not() {
        var set = Set("a", "b", "c") with {
            Channels = [new() { Usage = "baseColor", Default = [0.5f, 0.5f, 0.5f, 1f] }]
        };

        var slices = PaintStackSlices.Split(set, "b");

        Assert.True(slices.Succeeded, slices.Refusal);
        Assert.Equal([0.5f, 0.5f, 0.5f, 1f], slices.Below!.Channels[0].Default);
        Assert.Equal([0f, 0f, 0f, 0f], slices.Above!.Channels[0].Default);
    }

    /// <summary>A group over two channels holding one fill that writes both of them.</summary>
    /// <remarks>
    ///     ⚠ <b><c>Grouped</c> would not do, and finding out why is worth the second fixture.</b> Its
    ///     layers carry no <c>Values</c>, and a fill with no entry for a channel is a fill that does
    ///     not write it — so that set builds four nodes whatever the group says, and a node count
    ///     over it is a number that cannot move. The layers here paint.
    /// </remarks>
    static TextureSetAsset Painting() =>
        new() {
            Name = "body",
            Channels = [new() { Usage = "baseColor" }, new() { Usage = "roughness" }],
            Layers = [
                new() {
                    Id = "wrap",
                    Name = "Wrap",
                    Kind = LayerKind.Group,
                    Children = [
                        new() {
                            Id = "fill",
                            Kind = LayerKind.Fill,
                            Values = { ["baseColor"] = [1f, 0f, 0f, 1f], ["roughness"] = [0.4f, 0f, 0f, 1f] }
                        }
                    ]
                }
            ]
        };

    /// <summary>How many nodes one set builds.</summary>
    static int Nodes(TextureSetAsset set) =>
        LayerStackGraph.Build(new() { Name = "stack", Sets = [set] }, set).Graph.Nodes.Count;

    /// <summary>A set with a group in it, painted layer inside, one sibling either side of each.</summary>
    static TextureSetAsset Grouped() =>
        new() {
            Name = "body",
            Channels = [new() { Usage = "baseColor" }],
            Layers = [
                new() { Id = "a" },
                new() {
                    Id = "wrap",
                    Name = "Wrap",
                    Kind = LayerKind.Group,
                    Children = [new() { Id = "b" }, new() { Id = "inner" }, new() { Id = "c" }]
                },
                new() { Id = "d" }
            ]
        };

    /// <summary>An id nothing in the set has is a different refusal.</summary>
    /// <remarks>
    ///     Distinguished from the nested case deliberately: "move it out of the group" is advice, and
    ///     giving it to somebody holding a stale reference sends them looking for a group that is not
    ///     there.
    /// </remarks>
    [Fact]
    public void An_id_the_set_does_not_have_is_refused_differently() {
        var slices = PaintStackSlices.Split(Set("a", "b"), "missing");

        Assert.False(slices.Succeeded);
        Assert.DoesNotContain("#851", slices.Refusal);
        Assert.Contains("missing", slices.Refusal);
    }

    static TextureSetAsset Set(params string[] ids) =>
        new() {
            Name = "body",
            Channels = [new() { Usage = "baseColor" }, new() { Usage = "roughness" }],
            Layers = [.. ids.Select(id => new LayerAsset { Id = id, Name = id })]
        };
}
