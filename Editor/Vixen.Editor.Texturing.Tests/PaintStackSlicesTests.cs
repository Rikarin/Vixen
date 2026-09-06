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

    /// <summary>⚠ A group that really is a compositing boundary still refuses, and says which of the four.</summary>
    /// <remarks>
    ///     Each of these puts an operation between the painted layer and the canvas that no prefix
    ///     and suffix can express. The message names the property rather than the group, because
    ///     "move the layer out" is the wrong advice when setting the opacity back to one would do.
    /// </remarks>
    [Theory]
    [InlineData("blend")]
    [InlineData("opacity")]
    [InlineData("mask")]
    [InlineData("disabled")]
    public void A_group_that_composites_is_refused_and_the_refusal_names_what_made_it_one(string what) {
        var set = Grouped();

        set.Layers[1] = what switch {
            "blend" => set.Layers[1] with { Blend = LayerBlendMode.Multiply },
            "opacity" => set.Layers[1] with { Opacity = 0.5f },
            "mask" => set.Layers[1] with { Mask = new() { Source = LayerMaskSource.Constant, Value = 0.5f } },
            _ => set.Layers[1] with { Enabled = false }
        };

        var slices = PaintStackSlices.Split(set, "inner");

        Assert.False(slices.Succeeded);
        Assert.Contains("Wrap", slices.Refusal, StringComparison.Ordinal);

        // A switched-off group is a different problem with different advice, so it is the one that
        // does not cite the issue about compiling over a backdrop.
        if (what != "disabled") {
            Assert.Contains("#851", slices.Refusal, StringComparison.Ordinal);
        }
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
