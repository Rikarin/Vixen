// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Texturing.Layers;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>Doc 48 § M10's <c>.vxsmartmat</c>: what a stack drops when it stops naming a mesh.</summary>
/// <remarks>
///     <para>
///         <b>The decision under test is which parts of a stack survive a change of model</b> —
///         <a href="https://github.com/Rikarin/Vixen/issues/575">#575</a>. A curvature-driven mask is
///         portable because <c>Source/Mesh Map</c> names no image; a hand-painted canvas is not,
///         because a <c>.vxpaint</c> is texels in one model's atlas.
///     </para>
///     <para>
///         ⚠ <b>Device-free on purpose.</b> Nothing here evaluates anything: what is asserted is what
///         the <em>file</em> carries, and a suite that had to bake to say so would be untestable on
///         the machine that mostly runs it.
///     </para>
/// </remarks>
public class SmartMaterialTests {
    /// <summary>A stack whose layers exercise every decision the extract makes.</summary>
    /// <returns>The stack.</returns>
    /// <remarks>
    ///     ⚠ <b>The bake mask and the paint mask are on two different layers on purpose</b>, so that
    ///     an extract which zeroed <em>every</em> mask rather than only the painted ones is a red
    ///     test rather than a green one. The fixture that cannot tell those apart is the one this
    ///     shape exists to avoid.
    /// </remarks>
    static LayerStackAsset Stack() =>
        new() {
            Name = "Hull",
            Model = "Assets/Hull.fbx",
            BaseWidth = 128,
            BaseHeight = 128,
            Sets = [
                new() {
                    Name = "Body",
                    Mesh = "Hull_Body",
                    Channels = [
                        new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] },
                        new() { Usage = "roughness", Default = [0.5f, 0.5f, 0.5f, 1f] }
                    ],
                    Layers = [
                        new() {
                            Id = "paint-base",
                            Name = "Rust Strokes",
                            Kind = LayerKind.Paint,
                            Paint = "Hull.Body.paint-base.vxpaint"
                        },
                        new() {
                            Id = "worn",
                            Name = "Edge Wear",
                            Kind = LayerKind.Fill,
                            Values = { ["baseColor"] = [0.3f, 0.3f, 0.3f, 1f] },

                            // Portable: a mesh map is bound by what it measures, so the same mask
                            // reads the next model's own bakes with no rewiring.
                            Mask = new() { Source = LayerMaskSource.Bake, Map = "curvature" }
                        },
                        new() {
                            Id = "dirt",
                            Name = "Dirt",
                            Kind = LayerKind.Fill,
                            Values = { ["baseColor"] = [0.1f, 0.1f, 0.1f, 1f] },

                            // Not portable: painted pixels are in this model's atlas.
                            Mask = new() { Source = LayerMaskSource.Paint, Paint = "Hull.Body.dirt.mask.vxpaint" }
                        },
                        new() {
                            Id = "follows",
                            Name = "Follows The Strokes",
                            Kind = LayerKind.Fill,
                            Values = { ["baseColor"] = [0.9f, 0.9f, 0.9f, 1f] },

                            // Not portable either, and only because of what it points at.
                            Mask = new() { Source = LayerMaskSource.Anchor, Anchor = "paint-base" }
                        }
                    ]
                }
            ]
        };

    /// <summary>A smart material is the stack without its mesh binding.</summary>
    /// <remarks>
    ///     ⚠ <b>Both halves of the binding, because they live at two levels.</b>
    ///     <c>LayerStackAsset.Model</c> is the model and <c>TextureSetAsset.Mesh</c> narrows a set to
    ///     one of its meshes (<a href="https://github.com/Rikarin/Vixen/issues/920">#920</a>), so an
    ///     extract that dropped only the first would hand the artist a fragment that re-bound itself
    ///     to a mesh name off the model it came from.
    /// </remarks>
    [Fact]
    public void An_extracted_smart_material_names_no_model_and_no_mesh() {
        var extract = SmartMaterial.Extract(Stack(), "Rusted Iron");

        Assert.NotNull(extract.Material);
        Assert.Equal("", extract.Material.Model);
        Assert.Equal("Rusted Iron", extract.Material.Name);

        var set = Assert.Single(extract.Material.Sets);

        Assert.Equal("", set.Mesh);

        // The set's own name and its channels come along: a reader has to be able to say which of
        // the target's maps the material was authored against.
        Assert.Equal("Body", set.Name);
        Assert.Equal(["baseColor", "roughness"], set.Channels.Select(channel => channel.Usage));
    }

    /// <summary>A painted layer stays behind, and the extract says so.</summary>
    [Fact]
    public void A_paint_layer_is_dropped_and_named() {
        var extract = SmartMaterial.Extract(Stack(), "Rusted Iron");

        Assert.NotNull(extract.Material);

        var layers = extract.Material.Sets[0].Layers;

        Assert.DoesNotContain(layers, layer => layer.Kind == LayerKind.Paint);
        Assert.Equal(["worn", "dirt", "follows"], layers.Select(layer => layer.Id));

        // ⚠ Named, which is the whole difference between this and "silently drops the artist's
        // strokes". A drop nobody is told about is found by applying the material to a second model
        // and looking for dirt that never comes.
        Assert.Contains(extract.Dropped, one => one.Contains("Rust Strokes", StringComparison.Ordinal));
    }

    /// <summary>A painted mask comes back as a constant zero and never as <c>None</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the assertion the whole file is for, and the wrong answer is the obvious
    ///     one.</b> <c>LayerMaskSource.None</c> does not mean "no coverage", it means <em>no
    ///     mask</em> — the layer then writes everywhere. So spelling "drop the mask I cannot carry"
    ///     as <c>None</c> would make a layer that painted a small dirt patch cover the entire model,
    ///     and dropping coverage would have increased it. Zero folds into the layer's opacity
    ///     (<a href="https://github.com/Rikarin/Vixen/issues/789">#789</a>) and the layer contributes
    ///     nothing until it is masked again.
    /// </remarks>
    [Fact]
    public void A_painted_mask_comes_back_as_a_constant_zero() {
        var extract = SmartMaterial.Extract(Stack(), "Rusted Iron");

        Assert.NotNull(extract.Material);

        var dirt = Assert.Single(extract.Material.Sets[0].Layers, layer => layer.Id == "dirt");

        Assert.Equal(LayerMaskSource.Constant, dirt.Mask.Source);
        Assert.Equal(0f, dirt.Mask.Value);
        Assert.Equal("", dirt.Mask.Paint);
        Assert.Contains(extract.Dropped, one => one.Contains("Dirt", StringComparison.Ordinal));

        // ⚠ And the mask that was portable is untouched, which is what says the pass discriminates
        // rather than zeroing everything. A curvature bake is the example doc 48 § D10 leads with.
        var worn = Assert.Single(extract.Material.Sets[0].Layers, layer => layer.Id == "worn");

        Assert.Equal(LayerMaskSource.Bake, worn.Mask.Source);
        Assert.Equal("curvature", worn.Mask.Map);
    }

    /// <summary>A mask anchored to a dropped paint layer is zeroed too, not left dangling.</summary>
    /// <remarks>
    ///     ⚠ <b>Otherwise the file refuses to compile on the first model anybody applies it to.</b>
    ///     An anchor names a layer by id and <c>LayerStackGraph</c> refuses one that names no layer,
    ///     so carrying the reference over a layer that stayed behind produces a smart material whose
    ///     only symptom is a diagnostic in somebody else's stack.
    /// </remarks>
    [Fact]
    public void A_mask_anchored_to_a_dropped_layer_is_zeroed_too() {
        var extract = SmartMaterial.Extract(Stack(), "Rusted Iron");

        Assert.NotNull(extract.Material);

        var follows = Assert.Single(extract.Material.Sets[0].Layers, layer => layer.Id == "follows");

        Assert.Equal(LayerMaskSource.Constant, follows.Mask.Source);
        Assert.Equal(0f, follows.Mask.Value);
        Assert.Equal("", follows.Mask.Anchor);
    }

    /// <summary>The extract is a copy, so editing the stack afterwards does not edit the material.</summary>
    /// <remarks>
    ///     ⚠ <b>A record's <c>with</c> is shallow, and half of <see cref="LayerAsset" />'s members are
    ///     mutable references.</b> A smart material sharing the live stack's <c>Values</c> dictionary
    ///     would follow every subsequent edit an artist made — and would be written to disk hours
    ///     later carrying values the artist never saved it with.
    /// </remarks>
    [Fact]
    public void The_extract_does_not_alias_the_stack_it_came_from() {
        var stack = Stack();
        var extract = SmartMaterial.Extract(stack, "Rusted Iron");

        Assert.NotNull(extract.Material);

        stack.Sets[0].Layers[1].Values["baseColor"][0] = 0.99f;
        stack.Sets[0].Layers.Clear();

        var worn = Assert.Single(extract.Material.Sets[0].Layers, layer => layer.Id == "worn");

        Assert.Equal(0.3f, worn.Values["baseColor"][0]);
    }

    /// <summary>A smart material is a <c>.vxlayers</c>, so the stack's own reader reads it back.</summary>
    /// <remarks>
    ///     ⚠ <b>The point of the format decision, asserted rather than asserted-about.</b> There is
    ///     no second serialiser to drift from <see cref="LayerStackYaml" />, no second set of
    ///     refusals for a blend mode this build does not know, and the explode differential's round
    ///     trip already covers these bytes.
    /// </remarks>
    [Fact]
    public void A_smart_material_round_trips_through_the_stack_reader() {
        var extract = SmartMaterial.Extract(Stack(), "Rusted Iron");

        Assert.NotNull(extract.Material);

        var read = LayerStackYaml.Read(LayerStackYaml.Write(extract.Material));

        Assert.Equal("", read.Model);
        Assert.Equal(["worn", "dirt", "follows"], read.Sets[0].Layers.Select(layer => layer.Id));
        Assert.Equal(LayerMaskSource.Constant, read.Sets[0].Layers[1].Mask.Source);
        Assert.Equal(0f, read.Sets[0].Layers[1].Mask.Value);
    }

    /// <summary>Applying the same smart material twice gives the second one ids of its own.</summary>
    /// <remarks>
    ///     ⚠ <b>The second apply is the ordinary case, not the edge.</b> A layer id is unique in a
    ///     set — an anchor and a <c>LayerPath</c> both assume it — so without this the second
    ///     application's <c>worn</c> collides with the first's,
    ///     <c>LayerStackEdit.Ambiguous</c> reports both and the panel draws two rows with no
    ///     controls (<a href="https://github.com/Rikarin/Vixen/issues/893">#893</a>).
    /// </remarks>
    [Fact]
    public void A_second_apply_renames_the_ids_it_would_otherwise_collide_with() {
        var extract = SmartMaterial.Extract(Stack(), "Rusted Iron");

        Assert.NotNull(extract.Material);

        TextureSetAsset target = new() {
            Name = "Head",
            Channels = [new() { Usage = "baseColor" }]
        };

        var first = SmartMaterial.Prepare(target, extract.Material);

        target.Layers.AddRange(first.Layers);

        var second = SmartMaterial.Prepare(target, extract.Material);

        target.Layers.AddRange(second.Layers);

        Assert.Empty(LayerStackEdit.Ambiguous(target));
        Assert.Equal(
            ["worn", "dirt", "follows", "worn-2", "dirt-2", "follows-2"],
            target.Layers.Select(layer => layer.Id)
        );
        Assert.NotEmpty(second.Renamed);
    }

    /// <summary>A renamed layer's anchors are rewritten to the new ids.</summary>
    /// <remarks>
    ///     ⚠ <b>What the assertion above cannot distinguish.</b> Every id can be unique and every
    ///     anchor still point at the first application's copy — which is worse than dangling,
    ///     because it silently resolves and the second material's layers are masked by the first
    ///     material's.
    /// </remarks>
    [Fact]
    public void A_second_applys_anchors_point_inside_itself() {
        LayerStackAsset material = new() {
            Name = "Anchored",
            Sets = [
                new() {
                    Name = "Body",
                    Channels = [new() { Usage = "baseColor" }],
                    Layers = [
                        new() { Id = "under", Kind = LayerKind.Fill },
                        new() {
                            Id = "over",
                            Kind = LayerKind.Fill,
                            Mask = new() { Source = LayerMaskSource.Anchor, Anchor = "under" }
                        }
                    ]
                }
            ]
        };

        TextureSetAsset target = new() { Name = "Head", Channels = [new() { Usage = "baseColor" }] };

        target.Layers.AddRange(SmartMaterial.Prepare(target, material).Layers);

        var second = SmartMaterial.Prepare(target, material);

        target.Layers.AddRange(second.Layers);

        var over = Assert.Single(target.Layers, layer => layer.Id == "over-2");

        Assert.Equal("under-2", over.Mask.Anchor);
    }

    /// <summary><see cref="SmartMaterial.Prepare" /> reads the target and does not change it.</summary>
    /// <remarks>
    ///     ⚠ <b>Because the insertion is an undo entry.</b> A prepare that also inserted would put
    ///     the layers in outside the command stack, and the undo the artist then pressed would remove
    ///     layers the history never recorded arriving.
    /// </remarks>
    [Fact]
    public void Preparing_does_not_put_anything_on_the_target() {
        var extract = SmartMaterial.Extract(Stack(), "Rusted Iron");

        Assert.NotNull(extract.Material);

        TextureSetAsset target = new() { Name = "Head", Channels = [new() { Usage = "baseColor" }] };

        var applied = SmartMaterial.Prepare(target, extract.Material);

        Assert.NotEmpty(applied.Layers);
        Assert.Empty(target.Layers);
    }

    /// <summary>A material authored against a channel the target does not produce says so.</summary>
    [Fact]
    public void A_channel_the_target_does_not_produce_is_named() {
        var extract = SmartMaterial.Extract(Stack(), "Rusted Iron");

        Assert.NotNull(extract.Material);

        TextureSetAsset target = new() { Name = "Head", Channels = [new() { Usage = "baseColor" }] };

        var applied = SmartMaterial.Prepare(target, extract.Material);

        Assert.Contains("roughness", applied.Status, StringComparison.Ordinal);
    }

    /// <summary>A stack of nothing but paint refuses rather than saving an empty material.</summary>
    [Fact]
    public void A_stack_of_nothing_but_paint_saves_nothing() {
        LayerStackAsset stack = new() {
            Name = "Painted",
            Sets = [
                new() {
                    Name = "Body",
                    Channels = [new() { Usage = "baseColor" }],
                    Layers = [new() { Id = "strokes", Kind = LayerKind.Paint, Paint = "a.vxpaint" }]
                }
            ]
        };

        var extract = SmartMaterial.Extract(stack, "Nothing");

        Assert.Null(extract.Material);
        Assert.NotEmpty(extract.Dropped);
    }

    /// <summary>A material name that is a path does not write outside the shelf.</summary>
    /// <remarks>
    ///     ⚠ <b><a href="https://github.com/Rikarin/Vixen/issues/680">#680</a>'s shape, which shipped
    ///     once on the mesh-map baker.</b> "Ship / Hull" is a perfectly good name for a material and
    ///     a path traversal in a file system.
    /// </remarks>
    [Theory]
    [InlineData("Ship / Hull", "Ship___Hull")]
    [InlineData("../../etc/passwd", "etc_passwd")]
    [InlineData("...", "SmartMaterial")]
    public void A_name_that_is_a_path_is_flattened(string name, string wanted) =>
        Assert.Equal(wanted, SmartMaterial.Safe(name));
}
