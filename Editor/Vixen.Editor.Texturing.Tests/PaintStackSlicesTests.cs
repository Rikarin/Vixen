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

    /// <summary>⚠ A nested paint layer is refused, and the refusal names the issue that removes it.</summary>
    [Fact]
    public void A_layer_inside_a_group_is_refused_by_name() {
        TextureSetAsset set = new() {
            Name = "body",
            Channels = [new() { Usage = "baseColor" }],
            Layers = [
                new() { Id = "a" },
                new() { Id = "group", Kind = LayerKind.Group, Children = [new() { Id = "inner" }] }
            ]
        };

        var slices = PaintStackSlices.Split(set, "inner");

        Assert.False(slices.Succeeded);
        Assert.Contains("#851", slices.Refusal);
        Assert.Contains("group", slices.Refusal);
    }

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
